using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>Acción configurable de una categoría, con su piso mínimo (sección 3).</summary>
internal sealed record CategoryDecision(PolicyRule Rule, PolicyAction BaseAction, PolicyAction Floor, string? Detail);

/// <summary>
/// Reglas configurables (RS-POL-10): acción por categoría según <see cref="PolicyConfig"/>, reglas adicionales
/// del perfil y pisos que nunca se relajan (DROP/TRUNCATE/ALTER destructivo ≥ CONFIRMAR, DML sin filtro ≥
/// CONFIRMAR, SQL dinámico ≥ CONFIRMAR, triggers ≥ ALERTAR). Las reglas inmutables siempre dominan.
/// </summary>
internal static class ConfigurableRules
{
    /// <summary>Calcula la acción por categoría de una unidad (null si la categoría no tiene acción configurable).</summary>
    public static CategoryDecision? Decide(StatementUnit unit, PolicyConfig policy)
    {
        var cls = unit.Class;
        var temp = unit.AllTargetsTemp;
        var target = unit.Target is null ? null : $"'{PolicyNames.Display(unit.Target)}'";

        switch (cls.Category)
        {
            case StatementCategory.Query:
                return new(PolicyRules.Query, PolicyAction.Allow, PolicyAction.Allow, null);
            case StatementCategory.SelectInto:
                return temp
                    ? new(PolicyRules.DdlTempObject, PolicyAction.Allow, PolicyAction.Allow, target)
                    : new(PolicyRules.DdlCreate, policy.CreateAction, PolicyAction.Allow, target);
            case StatementCategory.Insert:
                return new(PolicyRules.Insert, PolicyAction.Allow, PolicyAction.Allow, target);
            case StatementCategory.Update or StatementCategory.Delete:
                if (unit.Unfiltered)
                {
                    return new(PolicyRules.UnfilteredDml, policy.UnfilteredDml, PolicyAction.Confirm, target);
                }

                if (unit.TrivialPredicate)
                {
                    return new(PolicyRules.TrivialPredicate, policy.UnfilteredDml, PolicyAction.Confirm, target);
                }

                if (policy.AlwaysConfirmDml)
                {
                    return new(PolicyRules.AlwaysConfirmDml, PolicyAction.Confirm, PolicyAction.Confirm, target);
                }

                return new(cls.Category == StatementCategory.Update ? PolicyRules.Update : PolicyRules.Delete, PolicyAction.Allow, PolicyAction.Allow, target);
            case StatementCategory.Merge:
                return policy.AlwaysConfirmDml
                    ? new(PolicyRules.AlwaysConfirmDml, PolicyAction.Confirm, PolicyAction.Confirm, target)
                    : new(PolicyRules.Merge, PolicyAction.Allow, PolicyAction.Allow, target);
            case StatementCategory.DdlCreate:
                return Ddl(unit, temp, PolicyRules.DdlCreate, policy.CreateAction, PolicyAction.Allow, target);
            case StatementCategory.DdlAlter:
                return Ddl(unit, temp, PolicyRules.DdlAlter, policy.AlterAction, PolicyAction.Allow, target);
            case StatementCategory.DdlDrop:
                return Ddl(unit, temp, PolicyRules.DdlDrop, policy.DropAction, PolicyAction.Confirm, target);
            case StatementCategory.DdlTruncate:
                return Ddl(unit, temp, PolicyRules.DdlTruncate, policy.TruncateAction, PolicyAction.Confirm, target);
            case StatementCategory.DdlDestructiveAlter:
                return Ddl(unit, temp, PolicyRules.DdlDestructiveAlter, policy.DestructiveAlterAction, PolicyAction.Confirm, target);
            case StatementCategory.DdlTrigger:
                return new(PolicyRules.DdlTrigger, policy.TriggerAction, PolicyAction.Alert, target);
            case StatementCategory.Statistics:
                return new(PolicyRules.DdlStatistics, PolicyAction.Allow, PolicyAction.Allow, target);
            case StatementCategory.ExtendedProperty:
                return new(PolicyRules.DdlExtendedProperty, PolicyAction.Allow, PolicyAction.Allow, target);
            case StatementCategory.ExecuteProcedure:
                return unit.ProcedureDecision is { } decision
                    ? new(decision, decision.Action, decision.Action, unit.ProcedureDecisionDetail)
                    : null;
            case StatementCategory.DynamicSql:
                return unit.DynamicLiteralAnalyzed
                    ? new(PolicyRules.DynamicSqlLiteral, policy.DynamicSql, PolicyAction.Confirm, null)
                    : null;
            default:
                return null;
        }
    }

    private static CategoryDecision Ddl(StatementUnit unit, bool temp, PolicyRule rule, PolicyAction configured, PolicyAction floor, string? target)
    {
        if (temp)
        {
            return new(PolicyRules.DdlTempObject, PolicyAction.Allow, PolicyAction.Allow, target);
        }

        if (unit.Class.RequiresConfirmation)
        {
            var objectType = unit.Class.ObjectType is null ? null : $"{unit.Class.Operation} {unit.Class.ObjectType}";
            return new(PolicyRules.DdlConfirmObjects, PolicyDecision.Max(configured, PolicyAction.Confirm), PolicyAction.Confirm, objectType);
        }

        return new(rule, configured, floor, target);
    }

    /// <summary>
    /// Registra la acción configurable de una unidad: la de la categoría o, si coincide alguna regla adicional
    /// del perfil, la más restrictiva de esas reglas; en ambos casos acotada por el piso de la categoría (RS-POL-10).
    /// </summary>
    public static void Apply(AnalysisContext ctx, StatementUnit unit)
    {
        var decision = Decide(unit, ctx.Profile.Policy);
        if (decision is null)
        {
            return;
        }

        ApplyDecision(ctx, decision, unit.Class.Category, unit.Class.Operation, unit.Class.ObjectType, unit.Target, unit.Objects, unit.Columns, unit.IsRoot);
    }

    /// <summary>
    /// Aplica una decisión de categoría con las reglas estáticas del perfil (sin MinEstimatedRows ni MinEstimatedCost,
    /// que se evalúan en <see cref="PolicyEngine.ApplyRuntimeFacts"/>). En sentencias anidadas solo se registran las
    /// acciones distintas de PERMITIR.
    /// </summary>
    public static void ApplyDecision(
        AnalysisContext ctx,
        CategoryDecision decision,
        StatementCategory category,
        string? operation,
        string? objectType,
        SqlObjectName? target,
        IEnumerable<SqlObjectReference> objects,
        IEnumerable<SqlColumnReference> columns,
        bool isRoot)
    {
        var names = StatementNames(category, operation);
        var objectList = objects as IReadOnlyCollection<SqlObjectReference> ?? objects.ToList();
        var columnList = columns as IReadOnlyCollection<SqlColumnReference> ?? columns.ToList();
        var matching = ctx.Profile.Policy.Rules
            .Where(r => r.MinEstimatedRows is null && r.MinEstimatedCost is null)
            .Where(r => MatchesStatic(r, names, target, objectList, columnList, objectType))
            .ToList();

        if (matching.Count > 0)
        {
            // La más restrictiva; en empate, la primera configurada (determinismo).
            var strongest = matching.OrderByDescending(r => r.Action).First();
            var action = PolicyDecision.Max(strongest.Action, decision.Floor);
            if (!isRoot && action == PolicyAction.Allow)
            {
                return;
            }

            var reason = string.IsNullOrWhiteSpace(strongest.Reason) ? $"Regla del perfil {strongest.Id}." : strongest.Reason.Trim();
            if (action != strongest.Action)
            {
                reason = $"{reason.TrimEnd('.')} (acción mínima de la categoría: {ActionName(decision.Floor)}; {decision.Rule.Message.TrimEnd('.')}).";
            }
            else if (!reason.EndsWith('.'))
            {
                reason += ".";
            }

            ctx.Hits.AddRaw(
                string.IsNullOrWhiteSpace(strongest.Id) ? decision.Rule.Id : strongest.Id.Trim(),
                action,
                reason,
                $"Regla configurada por el administrador ({strongest.Id}). Revise la operación o consulte get_server_policy.",
                action is PolicyAction.Block or PolicyAction.Alert ? AlertSeverity.Warning : null,
                immutable: false,
                decision.Rule.Risk,
                decision.Rule.Phase);
            return;
        }

        var effective = PolicyDecision.Max(decision.BaseAction, decision.Floor);
        if (!isRoot && effective == PolicyAction.Allow)
        {
            return;
        }

        ctx.Hits.Add(decision.Rule, decision.Detail, effective);
    }

    /// <summary>Nombre legible de una acción (PERMITIR, ALERTAR, CONFIRMAR, BLOQUEAR).</summary>
    public static string ActionName(PolicyAction action) => action switch
    {
        PolicyAction.Allow => "PERMITIR",
        PolicyAction.Alert => "ALERTAR",
        PolicyAction.Confirm => "CONFIRMAR",
        _ => "BLOQUEAR",
    };

    /// <summary>Nombres de sentencia con los que puede coincidir una regla del perfil (PolicyRuleConfig.Statements).</summary>
    public static HashSet<string> StatementNames(StatementCategory category, string? operation)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { category.ToString() };
        var op = operation ?? string.Empty;
        switch (category)
        {
            case StatementCategory.Query:
                set.Add("Select");
                break;
            case StatementCategory.SelectInto:
                set.Add("Select");
                set.Add("Create");
                break;
            case StatementCategory.Insert or StatementCategory.Update or StatementCategory.Delete or StatementCategory.Merge:
                set.Add(category.ToString());
                break;
            case StatementCategory.DdlCreate:
                set.Add("Create");
                if (op.Contains("ALTER", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add("Alter");
                }

                break;
            case StatementCategory.DdlAlter or StatementCategory.DdlDestructiveAlter:
                set.Add("Alter");
                break;
            case StatementCategory.DdlDrop:
                set.Add("Drop");
                break;
            case StatementCategory.DdlTruncate:
                set.Add("Truncate");
                break;
            case StatementCategory.DdlTrigger:
                set.Add("Trigger");
                if (op.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add("Create");
                }

                if (op.Contains("ALTER", StringComparison.OrdinalIgnoreCase) || op is "ENABLE" or "DISABLE")
                {
                    set.Add("Alter");
                }

                if (op == "DROP")
                {
                    set.Add("Drop");
                }

                break;
            case StatementCategory.Statistics:
                if (op == "CREATE")
                {
                    set.Add("Create");
                }
                else if (op == "DROP")
                {
                    set.Add("Drop");
                }

                break;
            case StatementCategory.ExecuteProcedure or StatementCategory.ExtendedProperty or StatementCategory.DynamicSql:
                set.Add("Execute");
                break;
        }

        return set;
    }

    /// <summary>
    /// Coincidencia de una regla del perfil por tipo de sentencia, esquema, objeto y columnas.
    /// Esquema y objeto se comparan contra el destino y contra todos los objetos referenciados.
    /// </summary>
    public static bool MatchesStatic(
        PolicyRuleConfig rule,
        IReadOnlySet<string> statementNames,
        SqlObjectName? target,
        IEnumerable<SqlObjectReference> objects,
        IEnumerable<SqlColumnReference> columns,
        string? objectType = null)
    {
        var statements = rule.Statements.Append(rule.Statement).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (statements.Count > 0 && !statements.Any(s => statementNames.Contains(s.Trim())))
        {
            return false;
        }

        var candidates = objects.Select(o => o.Name).ToList();
        if (target is not null)
        {
            candidates.Add(target);
        }

        // En CREATE/DROP/ALTER SCHEMA el destino es el propio esquema.
        var schemas = candidates.Where(o => !PolicyNames.IsLocalTemp(o.Name)).Select(o => PolicyNames.EffectiveSchema(o.Schema)).ToList();
        if (target is not null && string.Equals(objectType, "SCHEMA", StringComparison.Ordinal))
        {
            schemas.Add(target.Name);
        }

        if (!string.IsNullOrWhiteSpace(rule.Schema) && !schemas.Any(s => SqlNames.WildcardMatch(rule.Schema.Trim(), s)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Object) && !candidates.Any(o => MatchesObject(rule.Object, o)))
        {
            return false;
        }

        if (rule.Columns.Count > 0 && !columns.Any(c => rule.Columns.Any(p => MatchesColumn(p, c))))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesObject(string pattern, SqlObjectName name)
    {
        var parts = SqlNames.SplitMultipart(pattern, 2);
        if (parts is null)
        {
            return false;
        }

        return parts.Count == 2
            ? SqlNames.WildcardMatch(parts[0], PolicyNames.EffectiveSchema(name.Schema)) && SqlNames.WildcardMatch(parts[1], name.Name)
            : SqlNames.WildcardMatch(parts[0], name.Name);
    }

    /// <summary>Patrón de columna "col", "tabla.col" o "esquema.tabla.col"; si la tabla no se resolvió, coincide de forma conservadora.</summary>
    private static bool MatchesColumn(string pattern, SqlColumnReference column)
    {
        var parts = SqlNames.SplitMultipart(pattern, 3);
        if (parts is null)
        {
            return false;
        }

        return parts.Count switch
        {
            1 => SqlNames.WildcardMatch(parts[0], column.Column),
            2 => SqlNames.WildcardMatch(parts[1], column.Column) && (column.Table is null || SqlNames.WildcardMatch(parts[0], column.Table)),
            _ => SqlNames.WildcardMatch(parts[2], column.Column)
                && (column.Table is null || SqlNames.WildcardMatch(parts[1], column.Table))
                && (column.Schema is null || column.Table is null || SqlNames.WildcardMatch(parts[0], column.Schema)),
        };
    }
}
