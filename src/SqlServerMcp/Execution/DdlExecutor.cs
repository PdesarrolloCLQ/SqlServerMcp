using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Execution;

/// <summary>
/// Ejecuta DDL aprobado sobre los esquemas de trabajo con análisis de impacto y respaldo previo de la definición
/// (RF-DDL-05..08, RS-AUD-05).
/// </summary>
/// <remarks>
/// Flujo de ejecución: se resuelve el destino y su definición actual; se escribe el registro de cambio "pending" ANTES de
/// ejecutar (si no se puede escribir, no se ejecuta: fail-closed); se ejecuta en BEGIN TRAN; se escribe "applied" y se
/// confirma. Ante un error se revierte, se registra "failed" y se propaga el error.
/// </remarks>
public sealed class DdlExecutor : IDdlExecutor
{
    private const string SavepointPrefix = "mcp_ddl_";
    private const string ImpactSavepointPrefix = "mcp_imp_";

    private static readonly HashSet<string> ModuleTypes = new(StringComparer.OrdinalIgnoreCase) { "P", "V", "FN", "IF", "TF", "TR" };

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ICatalogLookup _catalogLookup;
    private readonly ISchemaChangeLog _schemaChangeLog;
    private readonly ITransactionManager _transactions;
    private readonly ILogger<DdlExecutor> _logger;

    public DdlExecutor(
        ISqlConnectionFactory connectionFactory,
        ICatalogLookup catalogLookup,
        ISchemaChangeLog schemaChangeLog,
        ITransactionManager transactions,
        ILogger<DdlExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(catalogLookup);
        ArgumentNullException.ThrowIfNull(schemaChangeLog);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _catalogLookup = catalogLookup;
        _schemaChangeLog = schemaChangeLog;
        _transactions = transactions;
        _logger = logger;
    }

    /// <summary>
    /// Impacto estimado sin ejecutar (RF-DDL-06, RF-DDL-07, RF-DDL-08): existencia y tipo del destino, filas aproximadas
    /// (DROP TABLE, TRUNCATE, DROP/ALTER COLUMN), dependientes (DROP, ALTER, RENAME) y definición actual.
    /// </summary>
    public async Task<DdlImpact> AnalyzeImpactAsync(ProfileRuntime profile, PolicyEvaluation evaluation, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(evaluation);
        var active = _transactions.GetActive(profile);
        if (active is not null)
        {
            // Dentro de la transacción explícita: ve los objetos creados en ella y no se bloquea con sus propios bloqueos de esquema.
            var analysis = await TransactionExecution.RunExplicitAsync(
                _transactions,
                active,
                profile,
                correlationId,
                ImpactSavepointPrefix,
                countsAsStatement: false,
                (connection, transaction, _) => ComputeImpactAsync(profile, evaluation.Statement, connection, transaction, cancellationToken),
                _logger,
                cancellationToken).ConfigureAwait(false);
            return analysis.Impact;
        }

        var readConnection = await _connectionFactory.OpenAsync(profile, ConnectionRole.Read, correlationId, cancellationToken).ConfigureAwait(false);
        await using (readConnection.ConfigureAwait(false))
        {
            var analysis = await ComputeImpactAsync(profile, evaluation.Statement, readConnection, null, cancellationToken).ConfigureAwait(false);
            return analysis.Impact;
        }
    }

    /// <summary>
    /// Respalda la definición previa en el registro de cambios (RF-DDL-05, RS-AUD-05), ejecuta en transacción y confirma.
    /// Una <see cref="AuditWriteException"/> al registrar "pending" impide la ejecución.
    /// </summary>
    public async Task<DdlExecutionResult> ExecuteAsync(ProfileRuntime profile, PolicyEvaluation evaluation, string sql, DdlImpact? impact, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Validate(evaluation);

        var statement = evaluation.Statement;
        var nonTransactional = DdlStatementClassifier.IsNonTransactional(statement);
        var active = _transactions.GetActive(profile);
        if (active is not null)
        {
            if (nonTransactional)
            {
                throw new ToolFailureException(
                    ErrorCodes.TransactionError,
                    "Las sentencias de texto completo (catálogos e índices) no pueden ejecutarse dentro de una transacción explícita.",
                    "Confirme o revierta la transacción explícita y ejecute la sentencia por separado.");
            }

            var executed = await TransactionExecution.RunExplicitAsync(
                _transactions,
                active,
                profile,
                correlationId,
                SavepointPrefix,
                countsAsStatement: true,
                (connection, transaction, _) => ExecuteInTransactionAsync(profile, statement, sql, correlationId, connection, transaction, committed: false, cancellationToken),
                _logger,
                cancellationToken).ConfigureAwait(false);
            executed.Result.Warnings.Add(
                $"El cambio forma parte de la transacción explícita {active.TransactionId}: se aplicará solo al confirmarla " +
                $"(el registro de cambios {executed.Change.ChangeId} lo marca como aplicado).");
            return executed.Result;
        }

        if (nonTransactional)
        {
            return await ExecuteWithoutTransactionAsync(profile, statement, sql, correlationId, cancellationToken).ConfigureAwait(false);
        }

        SchemaChangeRecord? applied = null;
        try
        {
            return await TransactionExecution.RunManagedAsync(
                _connectionFactory,
                profile,
                ConnectionRole.Write,
                correlationId,
                IsolationLevel.ReadCommitted,
                async (connection, transaction) =>
                {
                    var executed = await ExecuteInTransactionAsync(profile, statement, sql, correlationId, connection, transaction, committed: true, cancellationToken).ConfigureAwait(false);
                    applied = executed.Change;
                    return new ManagedOutcome<DdlExecutionResult>(executed.Result, Commit: true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (applied is not null)
        {
            // Falló la confirmación después de registrar "applied": se deja constancia de que el cambio no se aplicó.
            await RecordFailedAsync(applied, ex).ConfigureAwait(false);
            throw;
        }
    }

    private static void Validate(PolicyEvaluation evaluation)
    {
        if (evaluation.IsBlocked)
        {
            throw new ToolFailureException(
                evaluation.ErrorCode ?? ErrorCodes.PolicyBlocked,
                "La sentencia fue bloqueada por la política y no puede ejecutarse.",
                evaluation.Suggestion,
                evaluation.RuleId);
        }

        if (evaluation.Tool != ToolKind.Ddl || !DdlStatementClassifier.IsDdlCategory(evaluation.Statement.Category))
        {
            throw new ToolFailureException(
                ErrorCodes.PolicyBlocked,
                "Solo se admite una sentencia DDL evaluada para execute_ddl.",
                "Use la herramienta adecuada para otros tipos de sentencia.");
        }
    }

    /// <summary>
    /// Registro "pending" → ejecución → registro "applied" dentro de la transacción (si "applied" no se puede escribir, la
    /// excepción revierte el cambio). Ante un error de ejecución registra "failed" y relanza.
    /// </summary>
    private async Task<(DdlExecutionResult Result, SchemaChangeRecord Change)> ExecuteInTransactionAsync(
        ProfileRuntime profile,
        SqlStatementInfo statement,
        string sql,
        string correlationId,
        SqlConnection connection,
        SqlTransaction transaction,
        bool committed,
        CancellationToken cancellationToken)
    {
        var analysis = await ComputeImpactAsync(profile, statement, connection, transaction, cancellationToken).ConfigureAwait(false);
        var change = CreateChange(profile, statement, sql, correlationId, analysis);
        await RecordAsync(change, "pending", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteSqlAsync(connection, transaction, sql, profile, cancellationToken).ConfigureAwait(false);
            await RecordAsync(change, "applied", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RecordFailedAsync(change, ex).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "DDL {Operation} sobre {Object} en el perfil {Profile} (cambio {ChangeId}). Correlación {CorrelationId}.",
            change.Operation,
            change.ObjectName ?? "-",
            profile.Name,
            change.ChangeId,
            correlationId);
        var result = new DdlExecutionResult
        {
            Executed = true,
            Committed = committed,
            Impact = analysis.Impact,
            SchemaChangeId = change.ChangeId,
            Warnings = BuildWarnings(analysis),
        };
        return (result, change);
    }

    /// <summary>Texto completo: no admite transacción de usuario; se ejecuta en modo de confirmación automática.</summary>
    private async Task<DdlExecutionResult> ExecuteWithoutTransactionAsync(ProfileRuntime profile, SqlStatementInfo statement, string sql, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await _connectionFactory.OpenAsync(profile, ConnectionRole.Write, correlationId, cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var analysis = await ComputeImpactAsync(profile, statement, connection, null, cancellationToken).ConfigureAwait(false);
            var change = CreateChange(profile, statement, sql, correlationId, analysis);
            await RecordAsync(change, "pending", cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteSqlAsync(connection, null, sql, profile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RecordFailedAsync(change, ex).ConfigureAwait(false);
                if (TransactionExecution.TranslateTimeout(ex, profile, rolledBack: null) is { } timeout)
                {
                    throw timeout;
                }

                throw;
            }

            var warnings = BuildWarnings(analysis);
            warnings.Add("La sentencia de texto completo no admite transacción: se aplicó con confirmación automática.");
            try
            {
                await RecordAsync(change, "applied", cancellationToken).ConfigureAwait(false);
            }
            catch (AuditWriteException ex)
            {
                // El cambio ya está aplicado y no puede deshacerse: se informa en lugar de ocultarlo.
                _logger.LogError(ex, "No se pudo registrar como aplicado el cambio de esquema {ChangeId}.", change.ChangeId);
                warnings.Add($"El cambio se aplicó pero no se pudo registrar como aplicado en el registro de cambios (identificador {change.ChangeId}).");
            }

            return new DdlExecutionResult
            {
                Executed = true,
                Committed = true,
                Impact = analysis.Impact,
                SchemaChangeId = change.ChangeId,
                Warnings = warnings,
            };
        }
    }

    private static async Task ExecuteSqlAsync(SqlConnection connection, SqlTransaction? transaction, string sql, ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Calcula el impacto con la conexión indicada. La definición previa es obligatoria; filas y dependientes son informativos.</summary>
    private async Task<ImpactAnalysis> ComputeImpactAsync(
        ProfileRuntime profile,
        SqlStatementInfo statement,
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var target = statement.TargetObject;
        if (target is null || target.IsTemp || target.Server is not null)
        {
            return new ImpactAnalysis(new DdlImpact { TargetExists = false }, null, notes);
        }

        var catalogObject = await _catalogLookup.FindObjectAsync(profile, target, cancellationToken, connection, transaction).ConfigureAwait(false);
        if (catalogObject is null)
        {
            return new ImpactAnalysis(new DdlImpact { TargetExists = false }, null, notes);
        }

        long? approximateRows = null;
        if (catalogObject.IsTable && DdlStatementClassifier.NeedsApproximateRows(statement))
        {
            try
            {
                approximateRows = await _catalogLookup.GetApproximateRowCountAsync(profile, catalogObject, cancellationToken, connection, transaction).ConfigureAwait(false);
            }
            catch (SqlException ex) when (transaction is null || transaction.Connection is not null)
            {
                _logger.LogWarning("No se pudieron obtener las filas aproximadas de {Object} (error {Number}).", catalogObject.QualifiedName, ex.Number);
                notes.Add("No se pudieron obtener las filas aproximadas del objeto.");
            }
        }

        List<string> dependents = [];
        if (DdlStatementClassifier.NeedsDependents(statement))
        {
            try
            {
                dependents = [.. await _catalogLookup.GetDependentsAsync(profile, catalogObject, cancellationToken, connection, transaction).ConfigureAwait(false)];
            }
            catch (SqlException ex) when (transaction is null || transaction.Connection is not null)
            {
                _logger.LogWarning("No se pudieron obtener los dependientes de {Object} (error {Number}).", catalogObject.QualifiedName, ex.Number);
                notes.Add("No se pudieron obtener los objetos dependientes.");
            }
        }

        var definition = await GetDefinitionAsync(profile, catalogObject, connection, transaction, cancellationToken).ConfigureAwait(false);
        if (definition is null && ModuleTypes.Contains(catalogObject.TypeCode))
        {
            notes.Add("La definición actual no está disponible (módulo cifrado o sin permiso VIEW DEFINITION); no podrá restaurarse desde el registro de cambios.");
        }

        var impact = new DdlImpact
        {
            TargetExists = true,
            TargetObjectType = catalogObject.TypeDescription,
            ApproximateRows = approximateRows,
            Dependents = dependents,
            CurrentDefinition = definition,
        };
        return new ImpactAnalysis(impact, catalogObject, notes);
    }

    /// <summary>Módulos: OBJECT_DEFINITION; tablas: JSON compacto de columnas; otros objetos: JSON con nombre y tipo.</summary>
    private async Task<string?> GetDefinitionAsync(ProfileRuntime profile, CatalogObject catalogObject, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (ModuleTypes.Contains(catalogObject.TypeCode))
        {
            return await _catalogLookup.GetModuleDefinitionAsync(profile, catalogObject, cancellationToken, connection, transaction).ConfigureAwait(false);
        }

        if (catalogObject.IsTable)
        {
            var columns = await _catalogLookup.GetColumnsAsync(profile, catalogObject, cancellationToken, connection, transaction).ConfigureAwait(false);
            return DescribeTable(catalogObject, columns);
        }

        return DescribeObject(catalogObject);
    }

    /// <summary>Descripción JSON compacta de una tabla para el registro de cambios.</summary>
    internal static string DescribeTable(CatalogObject table, IReadOnlyList<CatalogColumn> columns)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("object", table.QualifiedName);
            writer.WriteString("type", table.TypeDescription);
            writer.WriteStartArray("columns");
            foreach (var column in columns.OrderBy(c => c.ColumnId))
            {
                writer.WriteStartObject();
                writer.WriteString("name", column.Name);
                writer.WriteString("type", column.Type.Declaration);
                writer.WriteBoolean("nullable", column.IsNullable);
                writer.WriteBoolean("identity", column.IsIdentity);
                writer.WriteBoolean("computed", column.IsComputed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string DescribeObject(CatalogObject catalogObject)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("object", catalogObject.QualifiedName);
            writer.WriteString("type", catalogObject.TypeDescription);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static SchemaChangeRecord CreateChange(ProfileRuntime profile, SqlStatementInfo statement, string sql, string correlationId, ImpactAnalysis analysis)
    {
        var target = statement.TargetObject;
        return new SchemaChangeRecord
        {
            CorrelationId = correlationId,
            Profile = profile.Name,
            Database = target?.Database ?? profile.InitialDatabase,
            ObjectName = analysis.Object?.QualifiedName ?? target?.SchemaQualified(),
            ObjectType = analysis.Object?.TypeDescription ?? statement.ObjectType,
            Operation = DdlStatementClassifier.OperationName(statement),
            PreviousDefinition = analysis.Impact.TargetExists && DdlStatementClassifier.RequiresBackup(statement)
                ? analysis.Impact.CurrentDefinition
                : null,
            NewStatement = string.IsNullOrWhiteSpace(statement.NormalizedSql) ? sql : statement.NormalizedSql,
            Status = "pending",
        };
    }

    private Task RecordAsync(SchemaChangeRecord change, string status, CancellationToken cancellationToken) =>
        _schemaChangeLog.RecordAsync(
            new SchemaChangeRecord
            {
                ChangeId = change.ChangeId,
                CorrelationId = change.CorrelationId,
                Profile = change.Profile,
                Database = change.Database,
                ObjectName = change.ObjectName,
                ObjectType = change.ObjectType,
                Operation = change.Operation,
                PreviousDefinition = change.PreviousDefinition,
                NewStatement = change.NewStatement,
                Status = status,
            },
            cancellationToken);

    /// <summary>Registro "failed" de mejor esfuerzo: nunca oculta el error original.</summary>
    private async Task RecordFailedAsync(SchemaChangeRecord change, Exception cause)
    {
        if (cause is AuditWriteException)
        {
            return;
        }

        try
        {
            await RecordAsync(change, "failed", CancellationToken.None).ConfigureAwait(false);
        }
        catch (AuditWriteException ex)
        {
            _logger.LogError(ex, "No se pudo registrar como fallido el cambio de esquema {ChangeId}.", change.ChangeId);
        }
    }

    private static List<string> BuildWarnings(ImpactAnalysis analysis)
    {
        var warnings = new List<string>(analysis.Notes);
        if (analysis.Impact.Dependents.Count > 0)
        {
            warnings.Add($"Objetos dependientes que podrían quedar inválidos: {string.Join(", ", analysis.Impact.Dependents)}.");
        }

        return warnings;
    }

    private sealed record ImpactAnalysis(DdlImpact Impact, CatalogObject? Object, List<string> Notes);
}
