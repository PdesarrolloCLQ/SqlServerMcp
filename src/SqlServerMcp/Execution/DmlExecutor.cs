using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Execution;

/// <summary>
/// Ejecuta DML aprobado por la política con transacción gestionada, estimación previa y umbrales de filas
/// (RF-DML-01..10, RS-HIL-07, CA-12, CA-13, CA-21).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Cada sentencia se ejecuta en una transacción del MCP (o en un punto de guardado de la transacción explícita activa)
/// y se revierte si las filas reales superan <see cref="LimitsConfig.MaxAffectedRows"/> o las aprobadas más el margen.</item>
/// <item>Las filas afectadas se obtienen con <c>@@ROWCOUNT</c> de la propia sentencia, en un conjunto de resultados final con
/// un alias aleatorio por ejecución: no incluye filas de desencadenadores y el agente no puede falsificarlo con OUTPUT.</item>
/// <item>Ante cualquier error se revierte y se libera la conexión, sin bloqueos residuales.</item>
/// </list>
/// </remarks>
public sealed class DmlExecutor : IDmlExecutor
{
    private const string EstimateSavepointPrefix = "mcp_est_";
    private const string StatementSavepointPrefix = "mcp_sp_";
    private const string InsertSavepointPrefix = "mcp_ins_";

    /// <summary>Errores de permisos (SELECT denegado al lector): la estimación recurre a la ejecución de prueba.</summary>
    private static readonly HashSet<int> PermissionErrors = [229, 230, 262, 297, 300];

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IParameterBinder _parameterBinder;
    private readonly IResultReader _resultReader;
    private readonly ICatalogLookup _catalogLookup;
    private readonly ITransactionManager _transactions;
    private readonly ILogger<DmlExecutor> _logger;
    private readonly DmlEstimator _estimator = new(new DmlCountQueryBuilder());

    public DmlExecutor(
        ISqlConnectionFactory connectionFactory,
        IParameterBinder parameterBinder,
        IResultReader resultReader,
        ICatalogLookup catalogLookup,
        ITransactionManager transactions,
        ILogger<DmlExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(parameterBinder);
        ArgumentNullException.ThrowIfNull(resultReader);
        ArgumentNullException.ThrowIfNull(catalogLookup);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _parameterBinder = parameterBinder;
        _resultReader = resultReader;
        _catalogLookup = catalogLookup;
        _transactions = transactions;
        _logger = logger;
    }

    /// <summary>
    /// Estima las filas afectadas (RF-DML-03): conteo con el mismo origen y predicado para UPDATE/DELETE (conexión de lectura,
    /// o la de la transacción explícita activa), listas de VALUES para INSERT … VALUES y ejecución de prueba siempre revertida
    /// para INSERT … SELECT, MERGE y UPDATE/DELETE con FROM. Devuelve null solo si no puede estimar (INSERT … EXEC).
    /// </summary>
    public async Task<long?> EstimateAffectedRowsAsync(DmlExecutionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var plan = _estimator.Plan(request.Evaluation.Statement);
        switch (plan.Strategy)
        {
            case DmlEstimationStrategy.ValuesRowCount:
                return plan.KnownRows;
            case DmlEstimationStrategy.CountQuery:
                try
                {
                    return await CountAsync(request, plan.CountSql!, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException ex) when (PermissionErrors.Contains(ex.Number) && _transactions.GetActive(request.Profile) is null)
                {
                    _logger.LogInformation(
                        "Conteo de estimación sin permiso de lectura (error {Number}); se usa una ejecución de prueba revertida. Correlación {CorrelationId}.",
                        ex.Number,
                        request.CorrelationId);
                    return await TrialExecutionAsync(request, cancellationToken).ConfigureAwait(false);
                }

            case DmlEstimationStrategy.TrialExecution:
                return await TrialExecutionAsync(request, cancellationToken).ConfigureAwait(false);
            default:
                return null;
        }
    }

    /// <summary>
    /// Ejecuta la sentencia (RF-DML-04, RF-DML-06, RF-DML-09). Devuelve <c>RollbackReason = "LIMIT_EXCEEDED"</c> sin lanzar
    /// excepción si se superan los umbrales, y <c>"preview"</c> en vista previa. Los errores SQL se propagan tras revertir;
    /// el tiempo de espera agotado se informa como TIMEOUT.
    /// </summary>
    public async Task<DmlExecutionResult> ExecuteAsync(DmlExecutionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var profile = request.Profile;
        var active = _transactions.GetActive(profile);
        DmlExecutionResult result;
        if (active is not null)
        {
            result = await TransactionExecution.RunExplicitAsync(
                _transactions,
                active,
                profile,
                request.CorrelationId,
                StatementSavepointPrefix,
                countsAsStatement: true,
                async (connection, transaction, savepoint) =>
                {
                    var execution = await ExecuteStatementAsync(connection, transaction, request.Sql, request.Parameters, profile, readOutput: true, cancellationToken).ConfigureAwait(false);
                    var decision = Decide(request, execution.Rows);
                    if (decision.RollbackReason is not null)
                    {
                        transaction.Rollback(savepoint);
                    }
                    else
                    {
                        decision.Warnings.Add(
                            $"Los cambios forman parte de la transacción explícita {active.TransactionId}: se aplicarán solo al confirmarla.");
                    }

                    return BuildResult(request, execution, decision, committed: false);
                },
                _logger,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await TransactionExecution.RunManagedAsync(
                _connectionFactory,
                profile,
                ConnectionRole.Write,
                request.CorrelationId,
                IsolationLevel.ReadCommitted,
                async (connection, transaction) =>
                {
                    var execution = await ExecuteStatementAsync(connection, transaction, request.Sql, request.Parameters, profile, readOutput: true, cancellationToken).ConfigureAwait(false);
                    var decision = Decide(request, execution.Rows);
                    var commit = decision.RollbackReason is null;
                    return new ManagedOutcome<DmlExecutionResult>(BuildResult(request, execution, decision, committed: commit), commit);
                },
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "DML {Category} en el perfil {Profile}: {Rows} filas, confirmado={Committed}, revertido={RolledBack} ({Reason}). Correlación {CorrelationId}.",
            request.Evaluation.Statement.Category,
            profile.Name,
            result.RowsAffected,
            result.Committed,
            result.RolledBack,
            result.RollbackReason ?? "-",
            request.CorrelationId);
        return result;
    }

    /// <summary>
    /// Inserción estructurada (RF-DML-07, RF-DML-08): valida tabla y columnas contra el catálogo, agrupa por conjunto de
    /// columnas, usa INSERT parametrizados por lotes (≤ 1.000 filas y ≤ 2.000 parámetros) o SqlBulkCopy para grupos grandes,
    /// todo en una única transacción. El total insertado debe coincidir con las filas recibidas.
    /// </summary>
    public async Task<DmlExecutionResult> InsertRowsAsync(InsertRowsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        ValidateInsertRequest(request);

        var active = _transactions.GetActive(profile);
        DmlExecutionResult result;
        if (active is not null)
        {
            result = await TransactionExecution.RunExplicitAsync(
                _transactions,
                active,
                profile,
                request.CorrelationId,
                InsertSavepointPrefix,
                countsAsStatement: true,
                async (connection, transaction, _) =>
                {
                    var inserted = await InsertCoreAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
                    inserted.Warnings.Add($"Las filas forman parte de la transacción explícita {active.TransactionId}: se aplicarán solo al confirmarla.");
                    return inserted;
                },
                _logger,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await TransactionExecution.RunManagedAsync(
                _connectionFactory,
                profile,
                ConnectionRole.Write,
                request.CorrelationId,
                IsolationLevel.ReadCommitted,
                async (connection, transaction) =>
                {
                    var inserted = await InsertCoreAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
                    return new ManagedOutcome<DmlExecutionResult>(
                        new DmlExecutionResult
                        {
                            RowsAffected = inserted.RowsAffected,
                            Committed = true,
                            Output = inserted.Output,
                            Warnings = inserted.Warnings,
                        },
                        Commit: true);
                },
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "insert_rows en {Table} (perfil {Profile}): {Rows} filas, confirmado={Committed}. Correlación {CorrelationId}.",
            request.Table.SchemaQualified(),
            profile.Name,
            result.RowsAffected,
            result.Committed,
            request.CorrelationId);
        return result;
    }

    private static void ValidateRequest(DmlExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Evaluation);
        if (request.Evaluation.IsBlocked)
        {
            throw new ToolFailureException(
                request.Evaluation.ErrorCode ?? ErrorCodes.PolicyBlocked,
                "La sentencia fue bloqueada por la política y no puede ejecutarse.",
                request.Evaluation.Suggestion,
                request.Evaluation.RuleId);
        }

        if (request.Evaluation.Statement.Category is not (StatementCategory.Insert or StatementCategory.Update
            or StatementCategory.Delete or StatementCategory.Merge))
        {
            throw new ToolFailureException(
                ErrorCodes.PolicyBlocked,
                "Solo se admite una sentencia INSERT, UPDATE, DELETE o MERGE.",
                "Use la herramienta adecuada para otros tipos de sentencia.");
        }
    }

    private static void ValidateInsertRequest(InsertRowsRequest request)
    {
        var profile = request.Profile;
        var table = request.Table;
        if (table.Server is not null
            || (table.Database is not null && !string.Equals(table.Database, profile.InitialDatabase, StringComparison.OrdinalIgnoreCase))
            || !SqlNames.IsValidIdentifier(table.Name)
            || (table.Schema is not null && !SqlNames.IsValidIdentifier(table.Schema)))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                "El nombre de tabla no es válido: indique 'esquema.tabla' de la base de datos del perfil.",
                "Use list_tables para ver las tablas disponibles.");
        }

        var count = request.Rows?.Count ?? 0;
        if (count == 0)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Debe indicar al menos una fila.", "Envíe una lista de objetos columna → valor.");
        }

        var maxRows = Math.Min(profile.Limits.MaxInsertRows, profile.Limits.MaxAffectedRows);
        if (count > maxRows)
        {
            var failure = new ToolFailureException(
                ErrorCodes.LimitExceeded,
                $"Se recibieron {count.ToString(CultureInfo.InvariantCulture)} filas; el máximo por llamada es {maxRows.ToString(CultureInfo.InvariantCulture)}.",
                "Divida la inserción en varias llamadas.");
            failure.Details["rows"] = count;
            failure.Details["maxRows"] = maxRows;
            throw failure;
        }
    }

    /// <summary>Conteo COUNT_BIG(*) con los mismos parámetros que la sentencia original (RF-DML-03).</summary>
    private async Task<long> CountAsync(DmlExecutionRequest request, string countSql, CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var active = _transactions.GetActive(profile);
        if (active is not null)
        {
            return await TransactionExecution.RunExplicitAsync(
                _transactions,
                active,
                profile,
                request.CorrelationId,
                EstimateSavepointPrefix,
                countsAsStatement: false,
                (connection, transaction, _) => ExecuteCountAsync(connection, transaction, countSql, request.Parameters, profile, cancellationToken),
                _logger,
                cancellationToken).ConfigureAwait(false);
        }

        var readConnection = await _connectionFactory.OpenAsync(profile, ConnectionRole.Read, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        await using (readConnection.ConfigureAwait(false))
        {
            try
            {
                return await ExecuteCountAsync(readConnection, null, countSql, request.Parameters, profile, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex) when (TransactionExecution.TranslateTimeout(ex, profile, rolledBack: null) is { } timeout)
            {
                throw timeout;
            }
        }
    }

    private async Task<long> ExecuteCountAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string countSql,
        IReadOnlyList<QueryParameter> parameters,
        ProfileRuntime profile,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;
            command.CommandText = countSql;
            BindParameters(command, parameters);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Ejecución de prueba siempre revertida (transacción gestionada o punto de guardado mcp_est_N).</summary>
    private async Task<long?> TrialExecutionAsync(DmlExecutionRequest request, CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var active = _transactions.GetActive(profile);
        if (active is not null)
        {
            return await TransactionExecution.RunExplicitAsync<long?>(
                _transactions,
                active,
                profile,
                request.CorrelationId,
                EstimateSavepointPrefix,
                countsAsStatement: false,
                async (connection, transaction, savepoint) =>
                {
                    var execution = await ExecuteStatementAsync(connection, transaction, request.Sql, request.Parameters, profile, readOutput: false, cancellationToken).ConfigureAwait(false);
                    transaction.Rollback(savepoint);
                    return execution.Rows;
                },
                _logger,
                cancellationToken).ConfigureAwait(false);
        }

        return await TransactionExecution.RunManagedAsync<long?>(
            _connectionFactory,
            profile,
            ConnectionRole.Write,
            request.CorrelationId,
            IsolationLevel.ReadCommitted,
            async (connection, transaction) =>
            {
                var execution = await ExecuteStatementAsync(connection, transaction, request.Sql, request.Parameters, profile, readOutput: false, cancellationToken).ConfigureAwait(false);
                return new ManagedOutcome<long?>(execution.Rows, Commit: false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ejecuta la sentencia del agente seguida de un SELECT propio con @@ROWCOUNT bajo un alias aleatorio. Lee las filas de
    /// OUTPUT (limitadas a <see cref="LimitsConfig.MaxRows"/>) y descarta el resto sin leerlo en memoria.
    /// </summary>
    private async Task<StatementExecution> ExecuteStatementAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        ProfileRuntime profile,
        bool readOutput,
        CancellationToken cancellationToken)
    {
        var rowCountAlias = NewRowCountAlias();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;
            // El salto de línea cierra un posible comentario de línea final; ";" termina la sentencia (obligatorio en MERGE).
            command.CommandText = sql + "\n;SELECT CAST(@@ROWCOUNT AS bigint) AS " + SqlNames.Quote(rowCountAlias) + ";";
            BindParameters(command, parameters);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var results = await ReadResultsAsync(reader, rowCountAlias, profile, readOutput ? profile.Limits.MaxRows : 0, cancellationToken).ConfigureAwait(false);
                await reader.CloseAsync().ConfigureAwait(false);
                var rows = results.RowCount ?? Math.Max(0, reader.RecordsAffected);
                return new StatementExecution(rows, results.Output);
            }
        }
    }

    /// <summary>
    /// Recorre todos los conjuntos de resultados: el primero con columnas distinto del conteo son las filas de OUTPUT;
    /// el conjunto con el alias del conteo aporta las filas afectadas exactas.
    /// </summary>
    private async Task<(long? RowCount, TabularResult? Output)> ReadResultsAsync(
        SqlDataReader reader,
        string rowCountAlias,
        ProfileRuntime profile,
        int maxOutputRows,
        CancellationToken cancellationToken)
    {
        long? rowCount = null;
        TabularResult? output = null;
        do
        {
            if (reader.FieldCount == 0)
            {
                continue;
            }

            if (reader.FieldCount == 1 && string.Equals(reader.GetName(0), rowCountAlias, StringComparison.Ordinal))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rowCount = (rowCount ?? 0) + (reader.IsDBNull(0) ? 0 : reader.GetInt64(0));
                }

                continue;
            }

            if (output is null && maxOutputRows > 0)
            {
                output = await _resultReader.ReadAsync(reader, profile, maxOutputRows, 0, [], cancellationToken).ConfigureAwait(false);
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        return (rowCount, output);
    }

    /// <summary>Umbrales posteriores a la ejecución (RF-DML-04, RS-HIL-07) y vista previa (RF-DML-09).</summary>
    private static Decision Decide(DmlExecutionRequest request, long rows)
    {
        var limits = request.Profile.Limits;
        var warnings = new List<string>();
        string? reason = null;
        if (rows > limits.MaxAffectedRows)
        {
            reason = ErrorCodes.LimitExceeded;
            warnings.Add(
                $"La sentencia afectó {rows.ToString(CultureInfo.InvariantCulture)} filas y el máximo del perfil es " +
                $"{limits.MaxAffectedRows.ToString(CultureInfo.InvariantCulture)}: los cambios se revirtieron.");
        }
        else if (DmlRowLimits.ExceedsApproval(rows, request.ApprovedRows, limits.AffectedRowsMarginPercent))
        {
            reason = ErrorCodes.LimitExceeded;
            var allowed = DmlRowLimits.MaxAllowedForApproval(request.ApprovedRows!.Value, limits.AffectedRowsMarginPercent);
            warnings.Add(
                $"La sentencia afectó {rows.ToString(CultureInfo.InvariantCulture)} filas, más que las aprobadas " +
                $"({request.ApprovedRows.Value.ToString(CultureInfo.InvariantCulture)}) con el margen del " +
                $"{limits.AffectedRowsMarginPercent.ToString(CultureInfo.InvariantCulture)} % (máximo {allowed.ToString(CultureInfo.InvariantCulture)}): los cambios se revirtieron.");
        }
        else if (request.Preview)
        {
            reason = "preview";
            warnings.Add("Vista previa: la sentencia se ejecutó y se revirtió; no se conservó ningún cambio.");
        }

        return new Decision(reason, warnings);
    }

    private static DmlExecutionResult BuildResult(DmlExecutionRequest request, StatementExecution execution, Decision decision, bool committed)
    {
        var warnings = new List<string>(decision.Warnings);
        if (request.Preview && execution.Output is null)
        {
            warnings.Add("Para ver una muestra de las filas afectadas en la vista previa, agregue una cláusula OUTPUT (por ejemplo, OUTPUT inserted.*, deleted.*).");
        }

        if (execution.Output is { Truncated: true })
        {
            warnings.Add("Las filas de OUTPUT se truncaron al máximo de filas o de tamaño de respuesta del perfil.");
        }

        return new DmlExecutionResult
        {
            RowsAffected = execution.Rows,
            Committed = committed,
            RolledBack = decision.RollbackReason is not null,
            RollbackReason = decision.RollbackReason,
            Output = execution.Output,
            Warnings = warnings,
        };
    }

    private async Task<DmlExecutionResult> InsertCoreAsync(SqlConnection connection, SqlTransaction transaction, InsertRowsRequest request, CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var table = await _catalogLookup.FindObjectAsync(profile, request.Table, cancellationToken, connection, transaction).ConfigureAwait(false);
        if (table is null || !profile.IsSchemaAllowed(table.Schema) || profile.IsObjectExcluded(table.Schema, table.Name))
        {
            throw new ToolFailureException(
                ErrorCodes.NotFound,
                $"La tabla '{request.Table.SchemaQualified()}' no existe o no está disponible en este perfil.",
                "Use list_tables para ver las tablas disponibles.");
        }

        if (!table.IsTable)
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"'{table.QualifiedName}' no es una tabla de usuario ({table.TypeDescription}); insert_rows solo admite tablas.",
                "Indique una tabla de usuario.");
        }

        var columns = await _catalogLookup.GetColumnsAsync(profile, table, cancellationToken, connection, transaction).ConfigureAwait(false);
        var plan = DmlInsertPlanner.Plan(profile, table, columns, request.Rows);
        var outputColumns = request.ReturnInserted ? plan.OutputColumns : null;

        long total = 0;
        var output = new OutputAccumulator(profile.Limits.MaxRows, profile.Limits.MaxResponseBytes);
        var bulkGroups = 0;
        foreach (var group in plan.Groups)
        {
            if (DmlRowLimits.ShouldUseBulkCopy(group.Rows.Count, request.ReturnInserted))
            {
                total += await BulkCopyAsync(connection, transaction, plan, group, profile, cancellationToken).ConfigureAwait(false);
                bulkGroups++;
                continue;
            }

            var chunkSize = DmlRowLimits.RowsPerInsertStatement(group.Columns.Count);
            for (var start = 0; start < group.Rows.Count; start += chunkSize)
            {
                var count = Math.Min(chunkSize, group.Rows.Count - start);
                total += await InsertChunkAsync(connection, transaction, plan, group, start, count, outputColumns, output, profile, cancellationToken).ConfigureAwait(false);
            }
        }

        var expected = request.Rows.Count;
        if (total != expected)
        {
            var failure = new ToolFailureException(
                ErrorCodes.SqlError,
                $"Se insertaron {total.ToString(CultureInfo.InvariantCulture)} filas en lugar de {expected.ToString(CultureInfo.InvariantCulture)}; la operación se revirtió.",
                "Revise si la tabla tiene desencadenadores INSTEAD OF que alteren la inserción.");
            failure.Details["rowsAffected"] = total;
            throw failure;
        }

        if (total > profile.Limits.MaxAffectedRows)
        {
            throw new ToolFailureException(
                ErrorCodes.LimitExceeded,
                $"La inserción afectó {total.ToString(CultureInfo.InvariantCulture)} filas y el máximo del perfil es {profile.Limits.MaxAffectedRows.ToString(CultureInfo.InvariantCulture)}; se revirtió.",
                "Divida la inserción en varias llamadas.");
        }

        _logger.LogDebug("insert_rows: {Groups} grupos, {BulkGroups} con SqlBulkCopy.", plan.Groups.Count, bulkGroups);
        var warnings = new List<string>();
        if (output.Result is { Truncated: true })
        {
            warnings.Add("Las filas insertadas devueltas se truncaron al máximo de filas o de tamaño de respuesta del perfil.");
        }

        return new DmlExecutionResult
        {
            RowsAffected = total,
            Committed = false,
            Output = output.Result,
            Warnings = warnings,
        };
    }

    private async Task<long> InsertChunkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DmlInsertPlan plan,
        DmlInsertGroup group,
        int start,
        int count,
        IReadOnlyList<CatalogColumn>? outputColumns,
        OutputAccumulator output,
        ProfileRuntime profile,
        CancellationToken cancellationToken)
    {
        var rowCountAlias = NewRowCountAlias();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;
            command.CommandText = DmlInsertPlanner.BuildInsertSql(plan, group.Columns, count, outputColumns, rowCountAlias);
            for (var r = 0; r < count; r++)
            {
                var values = group.Rows[start + r];
                for (var c = 0; c < group.Columns.Count; c++)
                {
                    command.Parameters.Add(_parameterBinder.BindForCatalogType(DmlInsertPlanner.ParameterName(r, c), group.Columns[c].Type, values[c]));
                }
            }

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var results = await ReadResultsAsync(reader, rowCountAlias, profile, outputColumns is null ? 0 : output.RemainingRows, cancellationToken).ConfigureAwait(false);
                await reader.CloseAsync().ConfigureAwait(false);
                if (outputColumns is not null)
                {
                    output.Add(results.Output, count);
                }

                return results.RowCount ?? Math.Max(0, reader.RecordsAffected);
            }
        }
    }

    /// <summary>
    /// SqlBulkCopy dentro de la transacción externa, con CHECK_CONSTRAINTS y FIRE_TRIGGERS (no exige ALTER sobre la tabla)
    /// y KeepNulls para que un null explícito se comporte igual que en la ruta parametrizada.
    /// </summary>
    private async Task<long> BulkCopyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DmlInsertPlan plan,
        DmlInsertGroup group,
        ProfileRuntime profile,
        CancellationToken cancellationToken)
    {
        using var data = DmlBulkCopyTableBuilder.Build(group.Columns, group.Rows, group.RowNumbers, _parameterBinder);
        using var bulkCopy = new SqlBulkCopy(
            connection,
            SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.FireTriggers | SqlBulkCopyOptions.KeepNulls,
            transaction)
        {
            DestinationTableName = plan.QuotedTable,
            BulkCopyTimeout = profile.Limits.CommandTimeoutSeconds,
            BatchSize = 0,
        };
        for (var c = 0; c < group.Columns.Count; c++)
        {
            bulkCopy.ColumnMappings.Add(c, group.Columns[c].Name);
        }

        await bulkCopy.WriteToServerAsync(data, cancellationToken).ConfigureAwait(false);
        return bulkCopy.RowsCopied64;
    }

    private void BindParameters(SqlCommand command, IReadOnlyList<QueryParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(_parameterBinder.Bind(parameter));
        }
    }

    /// <summary>Alias impredecible para el conjunto de resultados con @@ROWCOUNT (el agente no puede imitarlo).</summary>
    private static string NewRowCountAlias() => "mcp_rowcount_" + Guid.NewGuid().ToString("N");

    private sealed record StatementExecution(long Rows, TabularResult? Output);

    private sealed record Decision(string? RollbackReason, List<string> Warnings);

    /// <summary>Acumula las filas de OUTPUT de varios lotes respetando el máximo de filas y de bytes (RF-DML-06).</summary>
    private sealed class OutputAccumulator(int maxRows, long maxBytes)
    {
        public TabularResult? Result { get; private set; }

        public int RemainingRows => Result switch
        {
            null => maxRows,
            { } r when r.ApproximateBytes >= maxBytes => 0,
            { } r => Math.Max(0, maxRows - r.Rows.Count),
        };

        public void Add(TabularResult? chunk, int insertedRows)
        {
            if (chunk is null)
            {
                if (Result is not null && insertedRows > 0)
                {
                    Result.Truncated = true;
                    Result.TruncationReason ??= Result.ApproximateBytes >= maxBytes ? "maxResponseBytes" : "maxRows";
                }

                return;
            }

            if (Result is null)
            {
                Result = chunk;
                return;
            }

            if (Result.ApproximateBytes >= maxBytes)
            {
                Result.Truncated = true;
                Result.TruncationReason = "maxResponseBytes";
                return;
            }

            Result.Rows.AddRange(chunk.Rows);
            Result.ApproximateBytes += chunk.ApproximateBytes;
            Result.TruncatedCells += chunk.TruncatedCells;
            if (chunk.Truncated)
            {
                Result.Truncated = true;
                Result.TruncationReason = chunk.TruncationReason;
            }
        }
    }
}
