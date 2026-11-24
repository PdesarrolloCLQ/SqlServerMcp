using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Transacciones explícitas de varios pasos (RF-DML-10): como máximo una por perfil en la sesión, con conexión de
/// escritura dedicada, reversión automática por inactividad (<see cref="LimitsConfig.TransactionIdleSeconds"/>) y al
/// cerrar el proceso (RS-LIM-03). Es seguro para hilos: el estado se protege con un bloqueo y las sentencias, la
/// confirmación y la reversión se serializan con <see cref="ActiveTransaction.Gate"/>.
/// </summary>
public sealed class TransactionManager : ITransactionManager, IAsyncDisposable
{
    /// <summary>Espera máxima por una sentencia en curso al revertir durante el cierre.</summary>
    private static readonly TimeSpan ShutdownGateWait = TimeSpan.FromSeconds(10);

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<TransactionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private Task _pendingExpirations = Task.CompletedTask;
    private bool _disposed;

    public TransactionManager(ISqlConnectionFactory connectionFactory, IAuditLog auditLog, ILogger<TransactionManager> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connectionFactory = connectionFactory;
        _auditLog = auditLog;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public ActiveTransaction? GetActive(ProfileRuntime profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            return _slots.TryGetValue(profile.Name, out var slot) && !slot.Ending ? slot.Active : null;
        }
    }

    /// <summary>Inicia la transacción explícita del perfil; falla con TRANSACTION_ERROR si ya hay una (RF-DML-10).</summary>
    public async Task<TransactionInfo> BeginAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Slot slot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_slots.TryGetValue(profile.Name, out var existing))
            {
                var detail = existing.Active is null ? "otra solicitud la está iniciando" : $"identificador {existing.Active.TransactionId}";
                throw new ToolFailureException(
                    ErrorCodes.TransactionError,
                    $"Ya hay una transacción explícita activa para el perfil '{profile.Name}' ({detail}). Solo se permite una por sesión.",
                    "Confirme (commit_transaction) o revierta (rollback_transaction) la transacción actual antes de iniciar otra.");
            }

            slot = new Slot(correlationId, Math.Max(1, profile.Limits.TransactionIdleSeconds));
            _slots[profile.Name] = slot;
        }

        SqlConnection? connection = null;
        SqlTransaction? transaction = null;
        try
        {
            connection = await _connectionFactory.OpenAsync(profile, ConnectionRole.Write, correlationId, cancellationToken).ConfigureAwait(false);
            transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var active = new ActiveTransaction
            {
                TransactionId = "tx-" + Guid.NewGuid().ToString("N"),
                Profile = profile,
                Connection = connection,
                Transaction = transaction,
                StartedAtUtc = _timeProvider.GetUtcNow(),
            };

            lock (_sync)
            {
                if (_disposed)
                {
                    _slots.Remove(profile.Name);
                    throw new ObjectDisposedException(nameof(TransactionManager));
                }

                slot.Active = active;
                slot.Timer = new TransactionIdleTimer(
                    _timeProvider,
                    TimeSpan.FromSeconds(slot.IdleSeconds),
                    () => OnIdleExpired(profile, slot, active));
            }

            _logger.LogInformation(
                "Transacción explícita {TransactionId} iniciada en el perfil {Profile} (inactividad máxima {IdleSeconds} s).",
                active.TransactionId,
                profile.Name,
                slot.IdleSeconds);
            return ToInfo(active, slot.IdleSeconds);
        }
        catch
        {
            lock (_sync)
            {
                if (_slots.TryGetValue(profile.Name, out var current) && ReferenceEquals(current, slot))
                {
                    _slots.Remove(profile.Name);
                }
            }

            if (transaction is not null)
            {
                TransactionExecution.TryRollback(transaction);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Confirma la transacción explícita y libera su conexión.</summary>
    public async Task<TransactionInfo> CommitAsync(ProfileRuntime profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var (slot, active) = GetRequired(profile);
        await active.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Detach(profile, slot, active);
            try
            {
                if (active.Transaction.Connection is null)
                {
                    throw new ToolFailureException(
                        ErrorCodes.TransactionError,
                        $"La transacción {active.TransactionId} ya no estaba activa en el servidor (fue revertida por un error previo); no se confirmó ningún cambio.",
                        "Inicie una nueva transacción y repita los pasos.");
                }

                try
                {
                    active.Transaction.Commit();
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    TransactionExecution.TryRollback(active.Transaction);
                    var failure = new ToolFailureException(
                        ErrorCodes.TransactionError,
                        $"No se pudo confirmar la transacción {active.TransactionId} ({TransactionExecution.DescribeCause(ex)}); se revirtió.",
                        "Inicie una nueva transacción y repita los pasos.");
                    if (ex is SqlException sql)
                    {
                        failure.Details["sqlErrorNumber"] = sql.Number;
                    }

                    throw failure;
                }
            }
            finally
            {
                await DisposeResourcesAsync(active).ConfigureAwait(false);
            }

            _logger.LogInformation("Transacción explícita {TransactionId} confirmada ({Statements} sentencias).", active.TransactionId, active.StatementCount);
            return ToInfo(active, slot.IdleSeconds);
        }
        finally
        {
            active.Gate.Release();
        }
    }

    /// <summary>Revierte la transacción explícita y libera su conexión.</summary>
    public async Task<TransactionInfo> RollbackAsync(ProfileRuntime profile, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var (slot, active) = GetRequired(profile);
        await active.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Detach(profile, slot, active);
            TransactionExecution.TryRollback(active.Transaction);
            await DisposeResourcesAsync(active).ConfigureAwait(false);
            _logger.LogInformation("Transacción explícita {TransactionId} revertida. Motivo: {Reason}.", active.TransactionId, reason);
            return ToInfo(active, slot.IdleSeconds);
        }
        finally
        {
            active.Gate.Release();
        }
    }

    /// <inheritdoc />
    public void Touch(ProfileRuntime profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            if (_slots.TryGetValue(profile.Name, out var slot) && !slot.Ending)
            {
                slot.Timer?.Touch();
            }
        }
    }

    /// <summary>Revierte todas las transacciones abiertas (cierre del proceso o de la sesión, RS-LIM-03).</summary>
    public async Task RollbackAllAsync(string reason)
    {
        List<(string Profile, Slot Slot, ActiveTransaction Active)> pending = [];
        lock (_sync)
        {
            foreach (var (name, slot) in _slots)
            {
                if (slot.Active is not null && !slot.Ending)
                {
                    pending.Add((name, slot, slot.Active));
                }
            }
        }

        foreach (var (_, slot, active) in pending)
        {
            // Cada transacción se trata por separado: un fallo en una no debe impedir revertir las demás.
            try
            {
                var acquired = await active.Gate.WaitAsync(ShutdownGateWait).ConfigureAwait(false);
                try
                {
                    if (!TryDetach(active.Profile, slot, active))
                    {
                        continue;
                    }

                    TransactionExecution.TryRollback(active.Transaction);
                    await DisposeResourcesAsync(active).ConfigureAwait(false);
                }
                finally
                {
                    if (acquired)
                    {
                        active.Gate.Release();
                    }
                }

                _logger.LogWarning("Transacción explícita {TransactionId} del perfil {Profile} revertida automáticamente. Motivo: {Reason}.", active.TransactionId, active.Profile.Name, reason);
                await WriteAuditAsync(active, slot, reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al revertir la transacción {TransactionId} durante el cierre.", active.TransactionId);
            }
        }
    }

    /// <summary>Revierte todo lo pendiente y espera las expiraciones en curso.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await RollbackAllAsync("cierre del servidor").ConfigureAwait(false);
        await WhenExpirationsCompleteAsync().ConfigureAwait(false);
    }

    /// <summary>Espera a que terminen las reversiones por inactividad en curso (útil al cerrar y en pruebas).</summary>
    internal Task WhenExpirationsCompleteAsync()
    {
        lock (_sync)
        {
            return _pendingExpirations;
        }
    }

    private void OnIdleExpired(ProfileRuntime profile, Slot slot, ActiveTransaction active)
    {
        var task = ExpireAsync(profile, slot, active);
        lock (_sync)
        {
            _pendingExpirations = _pendingExpirations.IsCompleted ? task : Task.WhenAll(_pendingExpirations, task);
        }
    }

    /// <summary>Reversión automática por inactividad (RF-DML-10, RS-LIM-03). Nunca lanza excepciones.</summary>
    private async Task ExpireAsync(ProfileRuntime profile, Slot slot, ActiveTransaction active)
    {
        try
        {
            if (!active.Gate.Wait(0))
            {
                // Hay una sentencia en curso: eso es actividad. Se vuelve a armar el temporizador.
                lock (_sync)
                {
                    if (ReferenceEquals(slot.Active, active) && !slot.Ending)
                    {
                        slot.Timer?.Rearm();
                    }
                }

                return;
            }

            try
            {
                if (!TryDetach(profile, slot, active))
                {
                    return;
                }

                TransactionExecution.TryRollback(active.Transaction);
                await DisposeResourcesAsync(active).ConfigureAwait(false);
            }
            finally
            {
                active.Gate.Release();
            }

            _logger.LogWarning(
                "Transacción explícita {TransactionId} del perfil {Profile} revertida automáticamente tras {IdleSeconds} s de inactividad.",
                active.TransactionId,
                profile.Name,
                slot.IdleSeconds);
            await WriteAuditAsync(active, slot, "inactividad").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al revertir por inactividad la transacción {TransactionId}.", active.TransactionId);
        }
    }

    private (Slot Slot, ActiveTransaction Active) GetRequired(ProfileRuntime profile)
    {
        lock (_sync)
        {
            if (_slots.TryGetValue(profile.Name, out var slot) && !slot.Ending && slot.Active is { } active)
            {
                return (slot, active);
            }
        }

        throw NotActive(profile);
    }

    /// <summary>Retira la transacción del registro (con el Gate ya adquirido); lanza si otra operación la terminó antes.</summary>
    private void Detach(ProfileRuntime profile, Slot slot, ActiveTransaction active)
    {
        if (!TryDetach(profile, slot, active))
        {
            throw NotActive(profile);
        }
    }

    private bool TryDetach(ProfileRuntime profile, Slot slot, ActiveTransaction active)
    {
        TransactionIdleTimer? timer;
        lock (_sync)
        {
            if (slot.Ending || !ReferenceEquals(slot.Active, active))
            {
                return false;
            }

            slot.Ending = true;
            if (_slots.TryGetValue(profile.Name, out var current) && ReferenceEquals(current, slot))
            {
                _slots.Remove(profile.Name);
            }

            timer = slot.Timer;
            slot.Timer = null;
        }

        timer?.Dispose();
        return true;
    }

    private static async Task DisposeResourcesAsync(ActiveTransaction active)
    {
        try
        {
            await active.Transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Cerrar la conexión también revierte cualquier transacción pendiente antes de devolverla al grupo (CA-21).
            await active.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteAuditAsync(ActiveTransaction active, Slot slot, string reason)
    {
        var now = _timeProvider.GetUtcNow();
        try
        {
            await _auditLog.WriteAsync(new AuditRecord
            {
                TimestampUtc = now,
                Event = "transaction",
                CorrelationId = slot.CorrelationId,
                Profile = active.Profile.Name,
                Server = active.Profile.Config.Connection.Server,
                Database = active.Profile.InitialDatabase,
                Tool = "rollback_transaction",
                Statement = "ROLLBACK TRANSACTION",
                Outcome = "rolled_back",
                Reason = reason,
                DurationMs = (long)Math.Max(0, (now - active.StartedAtUtc).TotalMilliseconds),
                Extra = new Dictionary<string, string?>
                {
                    ["transactionId"] = active.TransactionId,
                    ["statementCount"] = active.StatementCount.ToString(CultureInfo.InvariantCulture),
                    ["idleTimeoutSeconds"] = slot.IdleSeconds.ToString(CultureInfo.InvariantCulture),
                },
            }).ConfigureAwait(false);
        }
        catch (AuditWriteException ex)
        {
            _logger.LogError(ex, "No se pudo auditar la reversión automática de la transacción {TransactionId}.", active.TransactionId);
        }
    }

    private static ToolFailureException NotActive(ProfileRuntime profile) => new(
        ErrorCodes.TransactionError,
        $"No hay una transacción explícita activa para el perfil '{profile.Name}' (puede haberse revertido por inactividad o por un error).",
        "Inicie una con begin_transaction si necesita agrupar varias operaciones.");

    private static TransactionInfo ToInfo(ActiveTransaction active, int idleSeconds) =>
        new(active.TransactionId, active.Profile.Name, active.StartedAtUtc, active.StatementCount, idleSeconds);

    /// <summary>Estado de la transacción de un perfil. Se modifica solo bajo el bloqueo del administrador.</summary>
    private sealed class Slot(string correlationId, int idleSeconds)
    {
        public string CorrelationId { get; } = correlationId;

        public int IdleSeconds { get; } = idleSeconds;

        public ActiveTransaction? Active { get; set; }

        public TransactionIdleTimer? Timer { get; set; }

        public bool Ending { get; set; }
    }
}
