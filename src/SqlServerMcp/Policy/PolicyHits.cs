using SqlServerMcp.Configuration;

namespace SqlServerMcp.Policy;

/// <summary>Coincidencia interna de una regla, con la información necesaria para decidir (RS-POL-11).</summary>
internal sealed record PolicyHit(
    string RuleId,
    PolicyAction Action,
    string Reason,
    string? Suggestion,
    AlertSeverity? Alert,
    bool Immutable,
    RiskLevel Risk,
    HitPhase Phase,
    int Sequence,
    string? ErrorCode)
{
    public RuleHit ToRuleHit() => new(RuleId, Action, Reason, Suggestion, Alert, Immutable);
}

/// <summary>Acumulador de coincidencias en orden de evaluación. No es seguro entre hilos (uno por análisis).</summary>
internal sealed class HitSink
{
    private readonly List<PolicyHit> _hits = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public IReadOnlyList<PolicyHit> Hits => _hits;

    public int Count => _hits.Count;

    /// <summary>Registra una regla del catálogo. <paramref name="detail"/> se agrega al motivo (objeto o construcción concreta).</summary>
    public void Add(PolicyRule rule, string? detail = null, PolicyAction? action = null, string? suggestion = null, string? reasonOverride = null)
    {
        var effective = action ?? rule.Action;
        var reason = reasonOverride ?? rule.Message;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            reason = reason.TrimEnd('.') + ": " + detail + ".";
        }

        AlertSeverity? alert = rule.Severity;
        if (rule.Configurable && rule.Severity is null && effective == PolicyAction.Alert)
        {
            alert = AlertSeverity.Warning;
        }

        if (rule.Configurable && effective < PolicyAction.Alert && rule.Phase != HitPhase.Runtime)
        {
            alert = null;
        }

        AddRaw(rule.Id, effective, reason, suggestion ?? rule.Suggestion, alert, rule.Immutable, rule.Risk, rule.Phase, rule.ErrorCode);
    }

    /// <summary>Registra una coincidencia ya construida (reglas configurables por el administrador).</summary>
    public void AddRaw(string ruleId, PolicyAction action, string reason, string? suggestion, AlertSeverity? alert, bool immutable, RiskLevel risk, HitPhase phase, string? errorCode = null)
    {
        // Se evita registrar dos veces exactamente la misma regla con el mismo motivo (por ejemplo, el mismo objeto referenciado varias veces).
        var key = string.Concat(ruleId, "\u001F", ((int)action).ToString(System.Globalization.CultureInfo.InvariantCulture), "\u001F", reason);
        if (!_keys.Add(key))
        {
            return;
        }

        _hits.Add(new PolicyHit(ruleId, action, reason, suggestion, alert, immutable, risk, phase, _hits.Count, errorCode));
    }

    public bool Any(Func<PolicyHit, bool> predicate) => _hits.Any(predicate);
}

/// <summary>Combina las coincidencias en la decisión final: la acción más restrictiva gana (sección 3).</summary>
internal static class PolicyDecision
{
    /// <summary>Ordena por fase y secuencia (orden de evaluación).</summary>
    public static List<PolicyHit> Order(IEnumerable<PolicyHit> hits) =>
        hits.OrderBy(h => (int)h.Phase).ThenBy(h => h.Sequence).ToList();

    public static PolicyEvaluation Build(
        SqlStatementInfo info,
        ToolKind tool,
        IReadOnlyList<PolicyHit> hits,
        IEnumerable<string> warnings,
        IEnumerable<int> maskOrdinals)
    {
        var ordered = Order(hits);
        if (ordered.Count == 0)
        {
            // Fail-closed: nunca debe ocurrir, pero una evaluación sin reglas no puede permitir nada.
            var sink = new HitSink();
            sink.Add(PolicyRules.UnknownStatement);
            ordered = Order(sink.Hits);
        }

        var max = ordered.Max(h => h.Action);
        var determining = ordered.First(h => h.Action == max);
        return new PolicyEvaluation
        {
            Statement = info,
            Tool = tool,
            Action = max,
            RuleId = determining.RuleId,
            Reason = determining.Reason,
            Suggestion = determining.Suggestion,
            Risk = ordered.Max(h => h.Risk),
            Alert = MaxAlert(ordered.Select(h => EffectiveAlert(h.Alert, h.Action))),
            Hits = ordered.Select(h => h.ToRuleHit()).ToList(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            MaskOrdinals = maskOrdinals.Distinct().Order().ToList(),
            ErrorCode = max == PolicyAction.Block ? determining.ErrorCode : null,
        };
    }

    /// <summary>Una coincidencia con acción ALERTAR implica al menos severidad de advertencia.</summary>
    public static AlertSeverity? EffectiveAlert(AlertSeverity? alert, PolicyAction action) =>
        action == PolicyAction.Alert ? MaxAlert([alert, AlertSeverity.Warning]) : alert;

    public static AlertSeverity? MaxAlert(IEnumerable<AlertSeverity?> values)
    {
        AlertSeverity? max = null;
        foreach (var v in values)
        {
            if (v is { } s && (max is null || s > max))
            {
                max = s;
            }
        }

        return max;
    }

    public static PolicyAction Max(PolicyAction a, PolicyAction b) => a >= b ? a : b;
}
