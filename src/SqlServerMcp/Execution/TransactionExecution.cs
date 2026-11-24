using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Ayudantes compartidos por los ejecutores de escritura para trabajar dentro de una transacción gestionada por el MCP
/// (RF-DML-04, RF-PRC-04) o dentro de un punto de guardado de la transacción explícita activa (RF-DML-10), garantizando
/// la reversión y la liberación de la conexión ante cualquier error (RS-LIM-03, CA-21).
/// </summary>
internal static class TransactionExecution
{
    /// <summary>Error de SqlClient para el tiempo de espera del comando (envía la señal de atención, RS-LIM-02).</summary>
    internal const int TimeoutErrorNumber = -2;

    /// <summary>Tiempo máximo de las comprobaciones internas (XACT_STATE, SESSION_CONTEXT).</summary>
    private const int InternalCommandTimeoutSeconds = 15;

    private static long s_savepointCounter;

    /// <summary>Nombre único de punto de guardado (máximo 32 caracteres en SQL Server).</summary>
    internal static string NextSavepointName(string prefix) =>
        prefix + Interlocked.Increment(ref s_savepointCounter).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Abre una conexión del rol indicado, inicia una transacción y ejecuta <paramref name="work"/>. Confirma solo si el
    /// trabajo lo indica; en cualquier otro caso (incluidas excepciones y cancelación) revierte y libera la conexión.
    /// Un tiempo de espera agotado se traduce a <see cref="ErrorCodes.Timeout"/> tras revertir.
    /// </summary>
    /// <param name="isolationLevel">Nivel de aislamiento; <see cref="IsolationLevel.Unspecified"/> conserva el de la sesión.</param>
    internal static async Task<T> RunManagedAsync<T>(
        ISqlConnectionFactory factory,
        ProfileRuntime profile,
        ConnectionRole role,
        string correlationId,
        IsolationLevel isolationLevel,
        Func<SqlConnection, SqlTransaction, Task<ManagedOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        var connection = await factory.OpenAsync(profile, role, correlationId, cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = isolationLevel == IsolationLevel.Unspecified
                ? connection.BeginTransaction()
                : connection.BeginTransaction(isolationLevel);
            await using (transaction.ConfigureAwait(false))
            {
                ManagedOutcome<T> outcome;
                try
                {
                    outcome = await work(connection, transaction).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TryRollback(transaction);
                    if (TranslateTimeout(ex, profile, rolledBack: true) is { } timeout)
                    {
                        throw timeout;
                    }

                    throw;
                }

                if (outcome.Commit)
                {
                    try
                    {
                        transaction.Commit();
                    }
                    catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                    {
                        TryRollback(transaction);
                        throw;
                    }
                }
                else
                {
                    TryRollback(transaction);
                }

                return outcome.Result;
            }
        }
    }

    /// <summary>
    /// Ejecuta <paramref name="work"/> dentro de la transacción explícita activa, serializado por su <see cref="ActiveTransaction.Gate"/>
    /// y protegido por un punto de guardado (cuyo nombre recibe el trabajo para revertir parcialmente si lo necesita).
    /// Si tras un error la transacción quedó condenada o revertida (XACT_STATE() &lt;&gt; 1, @@TRANCOUNT = 0), se descarta en el
    /// administrador y se informa <see cref="ErrorCodes.TransactionError"/>; si sigue viva, se revierte al punto de guardado
    /// y se relanza el error original.
    /// </summary>
    internal static async Task<T> RunExplicitAsync<T>(
        ITransactionManager manager,
        ActiveTransaction active,
        ProfileRuntime profile,
        string correlationId,
        string savepointPrefix,
        bool countsAsStatement,
        Func<SqlConnection, SqlTransaction, string, Task<T>> work,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await active.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateHeld = true;
        try
        {
            EnsureStillActive(manager, active, profile);
            var savepoint = NextSavepointName(savepointPrefix);
            T result;
            try
            {
                await SetCorrelationAsync(active, correlationId, cancellationToken).ConfigureAwait(false);
                active.Transaction.Save(savepoint);
                result = await work(active.Connection, active.Transaction, savepoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!await IsDoomedAsync(active).ConfigureAwait(false))
                {
                    TryRollbackToSavepoint(active.Transaction, savepoint);
                    if (TranslateTimeout(ex, profile, rolledBack: false) is { } timeout)
                    {
                        throw timeout;
                    }

                    throw;
                }

                active.Gate.Release();
                gateHeld = false;
                await DiscardAsync(manager, profile, active, logger).ConfigureAwait(false);
                throw CreateDoomedFailure(active, ex, profile);
            }

            if (countsAsStatement)
            {
                active.StatementCount++;
            }

            return result;
        }
        finally
        {
            if (gateHeld)
            {
                active.Gate.Release();
            }

            if (ReferenceEquals(manager.GetActive(profile), active))
            {
                manager.Touch(profile);
            }
        }
    }

    /// <summary>Revierte sin propagar errores (la transacción puede haber sido revertida ya por el servidor con XACT_ABORT).</summary>
    internal static void TryRollback(SqlTransaction transaction)
    {
        try
        {
            if (transaction.Connection is not null)
            {
                transaction.Rollback();
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // La transacción ya no es utilizable: el servidor la revirtió o la conexión se rompió.
        }
    }

    /// <summary>Revierte hasta el punto de guardado sin propagar errores.</summary>
    internal static void TryRollbackToSavepoint(SqlTransaction transaction, string savepoint)
    {
        try
        {
            if (transaction.Connection is not null)
            {
                transaction.Rollback(savepoint);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // El punto de guardado no existe o la transacción ya terminó; la comprobación de estado lo detecta.
        }
    }

    /// <summary>Traduce el tiempo de espera del driver (error -2) a un error controlado (RS-LIM-02).</summary>
    /// <param name="rolledBack">True: se revirtió la transacción gestionada; false: se revirtió el punto de guardado; null: operación de solo lectura.</param>
    internal static ToolFailureException? TranslateTimeout(Exception exception, ProfileRuntime profile, bool? rolledBack)
    {
        if (exception is not SqlException { Number: TimeoutErrorNumber })
        {
            return null;
        }

        var seconds = profile.Limits.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var message = rolledBack switch
        {
            true => $"La operación superó el tiempo máximo de {seconds} s: se canceló en el servidor y la transacción se revirtió.",
            false => $"La operación superó el tiempo máximo de {seconds} s y se canceló en el servidor; sus cambios se revirtieron.",
            null => $"La consulta superó el tiempo máximo de {seconds} s y se canceló en el servidor.",
        };
        var failure = new ToolFailureException(
            ErrorCodes.Timeout,
            message,
            "Acote el filtro, use columnas indexadas o procese en lotes más pequeños.");
        failure.Details["sqlErrorNumber"] = TimeoutErrorNumber;
        return failure;
    }

    /// <summary>Descripción segura de la causa de un error para el agente (sin trazas ni rutas, RF-OUT-04).</summary>
    internal static string DescribeCause(Exception exception) => exception switch
    {
        SqlException { Number: TimeoutErrorNumber } => "se superó el tiempo máximo de ejecución",
        SqlException sql => $"error SQL {sql.Number.ToString(CultureInfo.InvariantCulture)}: {sql.Message}",
        ToolFailureException tool => tool.Message,
        OperationCanceledException => "la operación se canceló",
        _ => "error interno durante la ejecución",
    };

    private static void EnsureStillActive(ITransactionManager manager, ActiveTransaction active, ProfileRuntime profile)
    {
        if (!ReferenceEquals(manager.GetActive(profile), active) || active.Transaction.Connection is null)
        {
            throw new ToolFailureException(
                ErrorCodes.TransactionError,
                "La transacción explícita ya no está activa (se confirmó, se revirtió o venció por inactividad).",
                "Inicie una nueva transacción si necesita agrupar varias operaciones.");
        }
    }

    /// <summary>Actualiza el identificador de correlación de la sesión para la sentencia en curso (RF-CON-08).</summary>
    private static async Task SetCorrelationAsync(ActiveTransaction active, string correlationId, CancellationToken cancellationToken)
    {
        var command = active.Connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = active.Transaction;
            command.CommandTimeout = InternalCommandTimeoutSeconds;
            command.CommandText = "EXEC sys.sp_set_session_context @key = N'mcp_correlation_id', @value = @cid;";
            command.Parameters.Add(new SqlParameter("@cid", SqlDbType.NVarChar, 64) { Value = correlationId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True si la transacción ya no es confirmable (revertida por el servidor, condenada o conexión rota).</summary>
    private static async Task<bool> IsDoomedAsync(ActiveTransaction active)
    {
        if (active.Transaction.Connection is null || active.Connection.State != ConnectionState.Open)
        {
            return true;
        }

        try
        {
            var command = active.Connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.Transaction = active.Transaction;
                command.CommandTimeout = InternalCommandTimeoutSeconds;
                command.CommandText = "SELECT CAST(XACT_STATE() AS int), CAST(@@TRANCOUNT AS int);";
                var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        return true;
                    }

                    return reader.GetInt32(0) != 1 || reader.GetInt32(1) == 0;
                }
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task DiscardAsync(ITransactionManager manager, ProfileRuntime profile, ActiveTransaction active, ILogger logger)
    {
        try
        {
            await manager.RollbackAsync(profile, "error", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ToolFailureException or SqlException or InvalidOperationException)
        {
            logger.LogWarning(ex, "No se pudo descartar la transacción explícita {TransactionId} del perfil {Profile}.", active.TransactionId, profile.Name);
        }
    }

    private static ToolFailureException CreateDoomedFailure(ActiveTransaction active, Exception cause, ProfileRuntime profile)
    {
        var failure = new ToolFailureException(
            ErrorCodes.TransactionError,
            $"La sentencia falló ({DescribeCause(cause)}) y la transacción explícita {active.TransactionId} se revirtió por completo; " +
            "ninguno de sus cambios se conservó.",
            "Corrija la sentencia e inicie una nueva transacción para repetir los pasos.");
        failure.Details["transactionId"] = active.TransactionId;
        failure.Details["profile"] = profile.Name;
        if (cause is SqlException sql)
        {
            failure.Details["sqlErrorNumber"] = sql.Number;
        }

        return failure;
    }
}

/// <summary>Resultado de un trabajo dentro de una transacción gestionada y si debe confirmarse.</summary>
internal readonly record struct ManagedOutcome<T>(T Result, bool Commit);
