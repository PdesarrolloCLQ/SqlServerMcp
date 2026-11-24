using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tools;

public sealed class DdlData
{
    public bool Executed { get; init; }

    public bool Committed { get; init; }

    public bool Preview { get; init; }

    /// <summary>ALLOW, ALERT, CONFIRM o BLOCK.</summary>
    public required string Decision { get; init; }

    public required string RuleId { get; init; }

    public required string Reason { get; init; }

    public string? Operation { get; init; }

    public string? ObjectType { get; init; }

    public string? TargetObject { get; init; }

    /// <summary>Script DDL normalizado (RF-DDL-08).</summary>
    public string? NormalizedSql { get; init; }

    public bool? TargetExists { get; init; }

    public long? ApproximateRows { get; init; }

    public List<string> Dependents { get; init; } = [];

    /// <summary>Identificador del registro de cambio de esquema con la definición anterior (RF-DDL-05).</summary>
    public string? SchemaChangeId { get; init; }
}

/// <summary>Definición de objetos permitidos con análisis recursivo de módulos y respaldo de definiciones (RF-DDL-01..10).</summary>
[McpServerToolType]
public sealed class DdlTools(
    ToolRunner runner,
    PolicyGate gate,
    IPolicyEngine policy,
    StatementPolicy statements,
    IDdlExecutor ddl)
{
    [McpServerTool(Name = "execute_ddl", Title = "Crear, modificar o eliminar objetos", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<DdlData>))]
    [MinimumMode(OperationMode.Completo)]
    [Description("""
        Ejecuta UNA sentencia DDL (CREATE, CREATE OR ALTER, ALTER o DROP de esquemas, tablas, vistas, índices, procedimientos, funciones,
        triggers DML, secuencias, sinónimos, tipos y estadísticas; TRUNCATE TABLE; sp_rename y propiedades extendidas) solo en los
        esquemas de DDL del perfil y con el esquema escrito explícitamente. El cuerpo de procedimientos, funciones, triggers y vistas
        se analiza con las mismas reglas (sin DCL, SQL dinámico, acceso externo ni WITH EXECUTE AS).
        DROP, TRUNCATE, DROP COLUMN, cambios de tipo y triggers requieren aprobación humana, mostrando filas aproximadas y objetos
        dependientes. Antes de ALTER/DROP se respalda la definición previa. preview=true devuelve el script normalizado y el impacto sin
        ejecutar. Use validate_sql antes de ejecutar.
        """)]
    public Task<CallToolResult> ExecuteDdl(
        McpServer server,
        [Description("Sentencia DDL única.")][MinLength(1)][MaxLength(262_144)] string sql,
        [Description("Vista previa: devuelve el script normalizado, la decisión y el impacto estimado sin ejecutar.")] bool preview = false,
        [Description("Perfil de conexión configurado; si se omite, se usa el perfil por defecto.")] string? profile = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync<DdlData>(server, "execute_ddl", profile, new ToolSpec(OperationMode.Completo, IsWrite: true), async ctx =>
        {
            ctx.Statement = sql;
            var evaluation = await statements.EvaluateAsync(sql, ToolKind.Ddl, ctx.Profile, ctx.CorrelationId, cancellationToken).ConfigureAwait(false);
            if (evaluation.IsBlocked)
            {
                await gate.EnforceAsync(ctx, evaluation, null, cancellationToken).ConfigureAwait(false);
            }

            var impact = await ddl.AnalyzeImpactAsync(ctx.Profile, evaluation, ctx.CorrelationId, cancellationToken).ConfigureAwait(false);
            evaluation = policy.ApplyRuntimeFacts(evaluation, new RuntimeFacts(null, null, impact.ApproximateRows), ctx.Profile);
            var statement = evaluation.Statement;

            if (preview)
            {
                ctx.ApplyEvaluation(evaluation);
                return new DdlData
                {
                    Preview = true,
                    Decision = evaluation.Action.ToString().ToUpperInvariant(),
                    RuleId = evaluation.RuleId,
                    Reason = evaluation.Reason,
                    Operation = statement.Operation,
                    ObjectType = statement.ObjectType,
                    TargetObject = statement.TargetObject?.ToString(),
                    NormalizedSql = statement.NormalizedSql,
                    TargetExists = impact.TargetExists,
                    ApproximateRows = impact.ApproximateRows,
                    Dependents = impact.Dependents,
                };
            }

            var pass = await gate.EnforceAsync(ctx, evaluation, new GateDetails
            {
                EstimatedRows = impact.ApproximateRows,
                Dependents = impact.Dependents,
            }, cancellationToken).ConfigureAwait(false);
            gate.EnsureApprovalStillValid(pass);
            await ctx.WritePreExecutionAuditAsync(cancellationToken).ConfigureAwait(false);

            var result = await ddl.ExecuteAsync(ctx.Profile, evaluation, sql, impact, ctx.CorrelationId, cancellationToken).ConfigureAwait(false);
            ctx.Warnings.AddRange(result.Warnings);

            // RS-AUD-04: DROP de tablas confirmados generan alerta crítica.
            if (result.Executed && statement.Category == StatementCategory.DdlDrop && string.Equals(statement.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                await runner.RaiseAlertAsync(AlertSeverity.Critical, "DROP_TABLE_EXECUTED",
                    $"Se eliminó la tabla {statement.TargetObject} tras confirmación ({ctx.Confirmation}).", ctx, ctx.Profile).ConfigureAwait(false);
            }

            return new DdlData
            {
                Executed = result.Executed,
                Committed = result.Committed,
                Decision = evaluation.Action.ToString().ToUpperInvariant(),
                RuleId = evaluation.RuleId,
                Reason = evaluation.Reason,
                Operation = statement.Operation,
                ObjectType = statement.ObjectType,
                TargetObject = statement.TargetObject?.ToString(),
                NormalizedSql = statement.NormalizedSql,
                TargetExists = impact.TargetExists,
                ApproximateRows = impact.ApproximateRows,
                Dependents = impact.Dependents,
                SchemaChangeId = result.SchemaChangeId,
            };
        }, cancellationToken);
}
