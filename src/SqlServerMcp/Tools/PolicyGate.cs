using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Confirmation;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tools;

/// <summary>Información adicional para la solicitud de confirmación.</summary>
public sealed class GateDetails
{
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    public long? EstimatedRows { get; init; }

    public IReadOnlyList<string> Dependents { get; init; } = [];

    /// <summary>Texto de la operación cuando no es una sentencia SQL libre (herramientas estructuradas).</summary>
    public string? StatementOverride { get; init; }
}

/// <summary>Resultado de pasar la puerta de política.</summary>
public sealed class GatePass
{
    public static readonly GatePass NotRequired = new();

    public ConfirmationResult? Approval { get; init; }

    public ConfirmationRequest? Request { get; init; }

    /// <summary>Filas aprobadas por el usuario (RS-HIL-07).</summary>
    public long? ApprovedRows => Approval?.Status == ConfirmationStatus.Approved ? Approval.ApprovedRows : null;
}

/// <summary>
/// Aplica la decisión del motor de políticas: BLOQUEAR → error con regla y sugerencia; ALERTAR → alerta;
/// CONFIRMAR → elicitation vinculada al hash (RS-HIL-01..06); PERMITIR → continúa.
/// </summary>
public sealed class PolicyGate
{
    private readonly IConfirmationService _confirmation;
    private readonly ToolRunner _runner;
    private readonly ISecurityMonitor _monitor;

    public PolicyGate(IConfirmationService confirmation, ToolRunner runner, ISecurityMonitor monitor)
    {
        _confirmation = confirmation;
        _runner = runner;
        _monitor = monitor;
    }

    public async Task<GatePass> EnforceAsync(ToolContext context, PolicyEvaluation evaluation, GateDetails? details, CancellationToken cancellationToken)
    {
        details ??= new GateDetails();
        context.ApplyEvaluation(evaluation);
        if (details.EstimatedRows is not null)
        {
            context.EstimatedRows = details.EstimatedRows;
        }

        if (evaluation.Action == PolicyAction.Block)
        {
            var failure = new ToolFailureException(evaluation.ErrorCode ?? ErrorCodes.PolicyBlocked, evaluation.Reason, evaluation.Suggestion, evaluation.RuleId, evaluation.Alert);
            failure.Details["category"] = evaluation.Statement.Category.ToString();
            failure.Details["risk"] = evaluation.Risk.ToString();
            if (details.EstimatedRows is not null)
            {
                failure.Details["estimatedRows"] = details.EstimatedRows;
            }

            throw failure;
        }

        if (evaluation.Alert is { } severity)
        {
            await _runner.RaiseAlertAsync(severity, evaluation.RuleId, evaluation.Reason, context, context.Profile).ConfigureAwait(false);
        }

        if (evaluation.Action != PolicyAction.Confirm)
        {
            context.Confirmation = "not_required";
            return GatePass.NotRequired;
        }

        var request = new ConfirmationRequest
        {
            Tool = context.ToolName,
            Profile = context.Profile,
            Database = context.Profile.InitialDatabase,
            Statement = details.StatementOverride ?? evaluation.Statement.NormalizedSql,
            Parameters = details.Parameters,
            Operation = DescribeOperation(evaluation),
            AffectedObjects = evaluation.Statement.TargetObject is { } target
                ? [target.ToString()]
                : evaluation.Statement.Objects.Where(o => o.Role is ObjectRole.Write or ObjectRole.DdlTarget or ObjectRole.Execute).Select(o => o.Name.ToString()).Distinct().ToList(),
            EstimatedRows = details.EstimatedRows,
            Risk = evaluation.Risk,
            RuleId = evaluation.RuleId,
            Reason = evaluation.Reason,
            Dependents = details.Dependents,
        };

        _monitor.IncrementMetric("confirmations.requested");
        var approval = await _confirmation.RequestAsync(context.Server, request, cancellationToken).ConfigureAwait(false);
        context.Confirmation = approval.AuditValue;
        context.ConfirmationAtUtc = approval.DecidedAtUtc;
        await _runner.WriteAuditAsync(context, "confirmation", approval.AuditValue, null, null, null, null, null, cancellationToken, throwOnFailure: false).ConfigureAwait(false);
        _monitor.IncrementMetric("confirmations." + approval.AuditValue);

        switch (approval.Status)
        {
            case ConfirmationStatus.Approved:
                return new GatePass { Approval = approval, Request = request };
            case ConfirmationStatus.Unavailable when context.Profile.Policy.ConfirmationUnavailable == ConfirmationUnavailableAction.AllowWithAlert
                                                    && context.Profile.Config.Environment == ProfileEnvironment.Development:
                context.Confirmation = "unavailable_allowed";
                await _runner.RaiseAlertAsync(AlertSeverity.Warning, "CONFIRMATION_BYPASSED_DEV",
                    $"Operación ejecutada sin confirmación porque el cliente no soporta elicitation (perfil de desarrollo, regla {evaluation.RuleId}).",
                    context, context.Profile).ConfigureAwait(false);
                return GatePass.NotRequired;
            case ConfirmationStatus.Unavailable:
                throw new ToolFailureException(ErrorCodes.ConfirmationUnavailable,
                    $"La operación requiere confirmación humana ({evaluation.RuleId}: {evaluation.Reason}) y el cliente MCP no soporta elicitation.",
                    "Use un cliente MCP con soporte de elicitation o pida al administrador que ejecute la operación.", evaluation.RuleId);
            case ConfirmationStatus.Expired:
                throw new ToolFailureException(ErrorCodes.ConfirmationExpired,
                    "La solicitud de confirmación expiró sin respuesta del usuario.", "Vuelva a solicitar la operación cuando el usuario esté disponible.", evaluation.RuleId);
            default:
                throw new ToolFailureException(ErrorCodes.ConfirmationDenied,
                    "El usuario rechazó o canceló la operación.", "No reintente la misma operación sin una nueva instrucción explícita del usuario.", evaluation.RuleId);
        }
    }

    /// <summary>Verifica, justo antes de ejecutar, que la aprobación sigue vigente y corresponde a la misma sentencia (RS-HIL-03).</summary>
    public void EnsureApprovalStillValid(GatePass pass)
    {
        if (pass.Approval is null || pass.Request is null)
        {
            return;
        }

        if (!_confirmation.TryConsume(pass.Approval, pass.Request))
        {
            throw new ToolFailureException(ErrorCodes.ConfirmationExpired,
                "La aprobación expiró, ya fue usada o no corresponde a la sentencia que se iba a ejecutar.",
                "Solicite de nuevo la operación para obtener una aprobación nueva.");
        }
    }

    private static string DescribeOperation(PolicyEvaluation evaluation)
    {
        var s = evaluation.Statement;
        var op = s.Operation ?? s.Category.ToString();
        return s.ObjectType is null ? op : $"{op} {s.ObjectType}";
    }
}
