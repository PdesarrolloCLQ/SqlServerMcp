using System.Data;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Ejecuta consultas de lectura (RF-QRY-01..13) con el login de lectura (RF-QRY-08) o, si la sesión tiene una
/// transacción explícita activa, dentro de ella (RF-DML-10). Garantías:
/// <list type="bullet">
/// <item>Valores siempre como parámetros del driver (RF-QRY-02, RS-INJ-01), como máximo <see cref="ParameterBinder.MaxParameters"/>.</item>
/// <item>CommandTimeout del perfil; el driver envía la señal de atención y el servidor cancela (RF-QRY-04, RS-LIM-02).</item>
/// <item>Transacción de solo lectura que siempre se revierte si el perfil lo indica (RF-QRY-08) con el aislamiento del perfil (RF-QRY-12).</item>
/// <item>Si el lector se detiene antes del final (truncamiento, error, columna protegida) se cancela el comando en el
/// servidor en lugar de drenar el resto de filas (CA-20) y no quedan bloqueos (CA-21).</item>
/// <item>Solo se devuelve el primer conjunto de resultados.</item>
/// </list>
/// </summary>
public sealed class QueryExecutor : IQueryExecutor
{
    private const int MaxInfoMessages = 5;
    private const int MaxInfoMessageLength = 500;

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IResultReader _resultReader;
    private readonly IParameterBinder _parameterBinder;
    private readonly ITransactionManager _transactions;

    public QueryExecutor(ISqlConnectionFactory connectionFactory, IResultReader resultReader, IParameterBinder parameterBinder, ITransactionManager transactions)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _resultReader = resultReader ?? throw new ArgumentNullException(nameof(resultReader));
        _parameterBinder = parameterBinder ?? throw new ArgumentNullException(nameof(parameterBinder));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
    }

    /// <inheritdoc />
    public async Task<QueryExecutionResult> ExecuteQueryAsync(QueryExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Evaluation);
        var profile = request.Profile;

        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "La sentencia está vacía.");
        }

        // Defensa en profundidad: nunca se ejecuta una evaluación bloqueada (RS-POL-02).
        if (request.Evaluation.IsBlocked)
        {
            throw new ToolFailureException(
                request.Evaluation.ErrorCode ?? ErrorCodes.PolicyBlocked,
                request.Evaluation.Reason,
                request.Evaluation.Suggestion,
                request.Evaluation.RuleId,
                request.Evaluation.Alert);
        }

        if (request.SkipRows < 0)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "El número de filas a omitir no puede ser negativo.");
        }

        var maxRows = request.MaxRows <= 0 ? profile.Limits.MaxRows : Math.Min(request.MaxRows, profile.Limits.MaxRows);
        var parameters = QueryParameterBinding.BindAll(_parameterBinder, request.Parameters);
        var execution = new QueryRun(request, Math.Max(1, maxRows), parameters);

        try
        {
            var active = _transactions.GetActive(profile);
            var result = active is not null
                ? await ExecuteInActiveTransactionAsync(active, execution, cancellationToken).ConfigureAwait(false)
                : await ExecuteIsolatedAsync(execution, cancellationToken).ConfigureAwait(false);

            var warnings = ResultReader.BuildWarnings(result, profile.Limits);
            warnings.AddRange(execution.Warnings);
            return new QueryExecutionResult { Result = result, Warnings = warnings };
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is SqlException or InvalidOperationException)
        {
            throw new OperationCanceledException("La consulta se canceló.", ex, cancellationToken);
        }
    }

    internal static IsolationLevel MapIsolation(QueryIsolationLevel level) => level switch
    {
        QueryIsolationLevel.Snapshot => IsolationLevel.Snapshot,
        QueryIsolationLevel.ReadUncommitted => IsolationLevel.ReadUncommitted,
        QueryIsolationLevel.RepeatableRead => IsolationLevel.RepeatableRead,
        QueryIsolationLevel.Serializable => IsolationLevel.Serializable,
        _ => IsolationLevel.ReadCommitted,
    };

    /// <summary>Conexión de lectura propia y, si el perfil lo indica, transacción que siempre se revierte (RF-QRY-08).</summary>
    private async Task<TabularResult> ExecuteIsolatedAsync(QueryRun execution, CancellationToken cancellationToken)
    {
        var profile = execution.Request.Profile;
        try
        {
            var connection = await _connectionFactory.OpenAsync(profile, ConnectionRole.Read, execution.Request.CorrelationId, cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                SqlTransaction? transaction = null;
                try
                {
                    if (profile.Config.QueryInRollbackTransaction)
                    {
                        transaction = (SqlTransaction)await connection
                            .BeginTransactionAsync(MapIsolation(profile.Config.IsolationLevel), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return await RunAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (transaction is not null)
                    {
                        await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw SqlErrorMapper.Map(ex, profile.Limits.CommandTimeoutSeconds);
        }
    }

    /// <summary>
    /// Ejecución dentro de la transacción explícita de la sesión. XACT_ABORT se desactiva solo durante la lectura para
    /// que una cancelación (truncamiento o tiempo agotado) o un error de la consulta no reviertan el trabajo pendiente
    /// del agente; al terminar se restablece.
    /// </summary>
    private async Task<TabularResult> ExecuteInActiveTransactionAsync(ActiveTransaction active, QueryRun execution, CancellationToken cancellationToken)
    {
        var profile = execution.Request.Profile;
        await active.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _transactions.Touch(profile);
            if (active.Transaction.Connection is null)
            {
                throw new ToolFailureException(
                    ErrorCodes.TransactionError,
                    "La transacción explícita activa ya no es válida porque el servidor la revirtió.",
                    "Ejecute rollback_transaction para cerrarla e inicie una nueva si la necesita.");
            }

            try
            {
                await SetXactAbortAsync(active.Connection, active.Transaction, on: false, profile, cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await RunAsync(active.Connection, active.Transaction, execution, cancellationToken).ConfigureAwait(false);
                    active.StatementCount++;
                    return result;
                }
                finally
                {
                    await RestoreXactAbortQuietlyAsync(active, profile).ConfigureAwait(false);
                }
            }
            catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = SqlErrorMapper.Map(ex, profile.Limits.CommandTimeoutSeconds);
                if (active.Transaction.Connection is null)
                {
                    failure.Details["transactionRolledBack"] = true;
                }

                throw failure;
            }
        }
        finally
        {
            _transactions.Touch(profile);
            active.Gate.Release();
        }
    }

    private async Task<TabularResult> RunAsync(SqlConnection connection, SqlTransaction? transaction, QueryRun execution, CancellationToken cancellationToken)
    {
        var request = execution.Request;
        var profile = request.Profile;
        var infoMessages = new List<string>();
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            lock (infoMessages)
            {
                foreach (SqlError error in e.Errors)
                {
                    if (infoMessages.Count >= MaxInfoMessages)
                    {
                        return;
                    }

                    var text = SqlErrorMapper.Sanitize(error.Message);
                    if (text.Length > MaxInfoMessageLength)
                    {
                        text = text[..MaxInfoMessageLength] + ResultReader.TruncationMarker;
                    }

                    if (text.Length > 0)
                    {
                        infoMessages.Add("Mensaje de SQL Server: " + text);
                    }
                }
            }
        }

        connection.InfoMessage += OnInfoMessage;
        try
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = request.Sql;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = Math.Max(1, profile.Limits.CommandTimeoutSeconds);
                command.Transaction = transaction;
                command.Parameters.AddRange(execution.Parameters);
                return await ReadFirstResultAsync(command, execution, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
            lock (infoMessages)
            {
                execution.Warnings.AddRange(infoMessages);
            }
        }
    }

    private async Task<TabularResult> ReadFirstResultAsync(SqlCommand command, QueryRun execution, CancellationToken cancellationToken)
    {
        var request = execution.Request;
        SqlDataReader? reader = null;
        var fullyConsumed = false;
        var cancelled = false;
        try
        {
            // SequentialAccess: los valores grandes se transmiten por fragmentos sin materializarse (RF-QRY-06).
            reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            while (reader.FieldCount == 0)
            {
                if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                {
                    fullyConsumed = true;
                    execution.Warnings.Add("La sentencia no devolvió un conjunto de resultados.");
                    return new TabularResult();
                }
            }

            var result = await _resultReader
                .ReadAsync(reader, request.Profile, execution.MaxRows, request.SkipRows, request.Evaluation.MaskOrdinals, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Truncated)
            {
                if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                {
                    execution.Warnings.Add("La sentencia devolvió más de un conjunto de resultados; solo se devuelve el primero.");
                }
                else
                {
                    fullyConsumed = true;
                }
            }

            return result;
        }
        finally
        {
            if (reader is not null)
            {
                if (!fullyConsumed)
                {
                    // Detiene la ejecución en el servidor (señal de atención) en vez de drenar las filas restantes (CA-20, CA-21).
                    command.Cancel();
                    cancelled = true;
                }

                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (SqlException) when (cancelled)
                {
                    // La cancelación intencional puede notificarse al cerrar el lector ("Operation cancelled by user").
                }
            }
        }
    }

    private static async Task SetXactAbortAsync(SqlConnection connection, SqlTransaction? transaction, bool on, ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = on ? "SET XACT_ABORT ON;" : "SET XACT_ABORT OFF;";
            command.CommandTimeout = Math.Max(5, profile.Limits.CommandTimeoutSeconds);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RestoreXactAbortQuietlyAsync(ActiveTransaction active, ProfileRuntime profile)
    {
        try
        {
            if (active.Connection.State != ConnectionState.Open)
            {
                return;
            }

            var transaction = active.Transaction.Connection is null ? null : active.Transaction;
            await SetXactAbortAsync(active.Connection, transaction, on: true, profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Conexión rota o transacción terminada: el administrador de transacciones la revertirá y descartará.
        }
    }

    private static async Task RollbackQuietlyAsync(SqlTransaction transaction)
    {
        try
        {
            // Con XACT_ABORT ON una cancelación ya revierte en el servidor: la transacción puede estar terminada.
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // La transacción ya no existe en el servidor; no queda nada que revertir.
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class QueryRun(QueryExecutionRequest request, int maxRows, SqlParameter[] parameters)
    {
        public QueryExecutionRequest Request { get; } = request;

        public int MaxRows { get; } = maxRows;

        public SqlParameter[] Parameters { get; } = parameters;

        public List<string> Warnings { get; } = [];
    }
}
