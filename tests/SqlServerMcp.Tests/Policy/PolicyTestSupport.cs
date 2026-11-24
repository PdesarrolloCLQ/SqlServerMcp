using SqlServerMcp.Configuration;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>Perfiles de prueba equivalentes al perfil "desarrollo" de design.md (VentasTest, esquemas ventas/ia).</summary>
internal static class TestProfiles
{
    public static ProfileConfig BaseConfig() => new()
    {
        Mode = OperationMode.Completo,
        Connection = new ConnectionConfig { Server = "localhost,14333", Database = "VentasTest" },
        AllowedDatabases = ["VentasTest"],
        AllowedSchemas = ["ventas", "ia", "dbo"],
        DdlSchemas = ["ia"],
        Excluded = new ExclusionConfig { Schemas = ["seguridad"], Tables = ["ventas.TarjetasCredito"], Columns = ["*.Contrasena"] },
    };

    /// <summary>Perfil por defecto de las pruebas (dbo permitido para reproducir literalmente los CA del ERS).</summary>
    public static ProfileRuntime Default { get; } = Create();

    /// <summary>Igual que el perfil por defecto pero sin columnas excluidas (para aislar las reglas de columnas sensibles).</summary>
    public static ProfileRuntime WithoutExcludedColumns { get; } = Create(c => c.Excluded.Columns = []);

    /// <summary>Perfil idéntico al de las pruebas de punta a punta (sin dbo, con la regla R-LOCAL-01).</summary>
    public static ProfileRuntime EndToEnd { get; } = Create(c =>
    {
        c.AllowedSchemas = ["ventas", "ia"];
        c.Limits.MaxAffectedRows = 1000;
        c.Limits.ConfirmAboveRows = 100;
        c.Policy.Rules.Add(new PolicyRuleConfig
        {
            Id = "R-LOCAL-01",
            Statement = "Delete",
            Object = "ventas.Facturas",
            Action = PolicyAction.Block,
            Reason = "Las facturas no se eliminan",
        });
    });

    public static ProfileRuntime Create(Action<ProfileConfig>? customize = null)
    {
        var config = BaseConfig();
        customize?.Invoke(config);
        return new ProfileRuntime("desarrollo", config, config.Limits);
    }
}

/// <summary>Ayudas de evaluación y aserción para el motor de políticas.</summary>
internal static class Policies
{
    public static PolicyEngine Engine { get; } = new();

    public static PolicyEvaluation Evaluate(string sql, ToolKind tool, ProfileRuntime? profile = null) =>
        Engine.Evaluate(sql, tool, profile ?? TestProfiles.Default);

    public static PolicyEvaluation AssertDecision(string sql, ToolKind tool, PolicyAction action, string ruleId, ProfileRuntime? profile = null)
    {
        var evaluation = Evaluate(sql, tool, profile);
        AssertDecision(evaluation, action, ruleId);
        return evaluation;
    }

    public static void AssertDecision(PolicyEvaluation evaluation, PolicyAction action, string ruleId)
    {
        Assert.True(
            evaluation.Action == action && evaluation.RuleId == ruleId,
            $"Se esperaba {action} {ruleId} y se obtuvo {evaluation.Action} {evaluation.RuleId}: {evaluation.Reason}\n"
            + string.Join("\n", evaluation.Hits.Select(h => $"  {h.RuleId} {h.Action} {h.Reason}")));
    }

    public static void AssertBlockedWithPrefix(string sql, ToolKind tool, string prefix, ProfileRuntime? profile = null)
    {
        var evaluation = Evaluate(sql, tool, profile);
        Assert.True(
            evaluation.Action == PolicyAction.Block && evaluation.RuleId.StartsWith(prefix, StringComparison.Ordinal),
            $"Se esperaba BLOCK {prefix}* y se obtuvo {evaluation.Action} {evaluation.RuleId}: {evaluation.Reason}");
    }
}
