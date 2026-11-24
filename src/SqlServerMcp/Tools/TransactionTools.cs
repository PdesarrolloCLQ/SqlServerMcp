using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;

namespace SqlServerMcp.Tools;

public sealed class TransactionData
{
    public required string TransactionId { get; init; }

    public required string State { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public int StatementCount { get; init; }

    public int IdleTimeoutSeconds { get; init; }
}

/// <summary>Transacciones explícitas de varios pasos: una por sesión, con reversión automática por inactividad (RF-DML-10, RS-LIM-03).</summary>
[McpServerToolType]
public sealed class TransactionTools(ToolRunner runner, ITransactionManager transactions)
{
    [McpServerTool(Name = "begin_transaction", Title = "Iniciar transacción explícita", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<TransactionData>))]
    [MinimumMode(OperationMode.EscrituraDatos)]
    [Description("""
        Inicia una transacción explícita para operaciones de varios pasos (una por sesión). Mientras esté abierta, execute_dml,
        insert_rows, execute_procedure, execute_ddl y run_query usan esa transacción. Se revierte automáticamente tras el tiempo de
        inactividad del perfil y al cerrar la sesión. Finalice siempre con commit_transaction o rollback_transaction.
        """)]
    public Task<CallToolResult> BeginTransaction(
        McpServer server,
        [Description("Perfil de conexión configurado; si se omite, se usa el perfil por defecto.")] string? profile = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync<TransactionData>(server, "begin_transaction", profile, new ToolSpec(OperationMode.EscrituraDatos, IsWrite: true), async ctx =>
        {
            ctx.Statement = "BEGIN TRANSACTION";
            await ctx.WritePreExecutionAuditAsync(cancellationToken).ConfigureAwait(false);
            var info = await transactions.BeginAsync(ctx.Profile, ctx.CorrelationId, cancellationToken).ConfigureAwait(false);
            return ToData(info, "activa");
        }, cancellationToken);

    [McpServerTool(Name = "commit_transaction", Title = "Confirmar transacción explícita", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<TransactionData>))]
    [MinimumMode(OperationMode.EscrituraDatos)]
    [Description("Confirma (COMMIT) la transacción explícita activa de la sesión. Los cambios quedan persistidos.")]
    public Task<CallToolResult> CommitTransaction(
        McpServer server,
        [Description("Perfil de conexión configurado; si se omite, se usa el perfil por defecto.")] string? profile = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync<TransactionData>(server, "commit_transaction", profile, new ToolSpec(OperationMode.EscrituraDatos, IsWrite: true), async ctx =>
        {
            ctx.Statement = "COMMIT TRANSACTION";
            await ctx.WritePreExecutionAuditAsync(cancellationToken).ConfigureAwait(false);
            var info = await transactions.CommitAsync(ctx.Profile, cancellationToken).ConfigureAwait(false);
            return ToData(info, "confirmada");
        }, cancellationToken);

    [McpServerTool(Name = "rollback_transaction", Title = "Revertir transacción explícita", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<TransactionData>))]
    [MinimumMode(OperationMode.EscrituraDatos)]
    [Description("Revierte (ROLLBACK) la transacción explícita activa de la sesión. Se permite incluso si la escritura está suspendida.")]
    public Task<CallToolResult> RollbackTransaction(
        McpServer server,
        [Description("Perfil de conexión configurado; si se omite, se usa el perfil por defecto.")] string? profile = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync<TransactionData>(server, "rollback_transaction", profile, new ToolSpec(OperationMode.EscrituraDatos, IsWrite: false), async ctx =>
        {
            ctx.Statement = "ROLLBACK TRANSACTION";
            var info = await transactions.RollbackAsync(ctx.Profile, "solicitada por el agente", cancellationToken).ConfigureAwait(false);
            return ToData(info, "revertida");
        }, cancellationToken);

    private static ToolOutcome<TransactionData> ToData(TransactionInfo info, string state) => new TransactionData
    {
        TransactionId = info.TransactionId,
        State = state,
        StartedAtUtc = info.StartedAtUtc,
        StatementCount = info.StatementCount,
        IdleTimeoutSeconds = info.IdleTimeoutSeconds,
    };
}
