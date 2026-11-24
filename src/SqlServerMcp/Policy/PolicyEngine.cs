using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Motor de políticas (sección 3, RS-POL-01..14, RS-DCL-01..03, RS-DAT-01/02/06, RF-DDL-02/03/04/06, RF-DML-02,
/// Anexo C). Analiza cada sentencia con ScriptDom (TSql170Parser), recorre el árbol completo, aplica las reglas
/// inmutables y las configurables del perfil y devuelve una decisión determinista y explicable (RS-POL-11).
/// </summary>
/// <remarks>
/// Sin estado mutable: cada evaluación crea su propio parser y contexto, por lo que la instancia es segura entre
/// hilos y puede registrarse como singleton. Nunca lanza excepciones por SQL inválido o malicioso: ante cualquier
/// problema de análisis devuelve BLOQUEAR (fail-closed, RS-POL-02). El análisis se ejecuta en un hilo con pila grande
/// para que entradas profundamente anidadas no puedan terminar el proceso (desbordamiento de pila no capturable).
/// </remarks>
public sealed class PolicyEngine : IPolicyEngine
{
    /// <summary>Crea el motor. No tiene dependencias ni realiza E/S.</summary>
    public PolicyEngine()
    {
    }

    /// <inheritdoc />
    public PolicyEvaluation Evaluate(string sql, ToolKind tool, ProfileRuntime profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var text = sql ?? string.Empty;
        try
        {
            // RS-INJ-02: tamaño máximo antes de cualquier análisis.
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > profile.Limits.MaxStatementBytes)
            {
                return StructuralBlock(
                    PolicyRules.SizeExceeded,
                    $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes; máximo {profile.Limits.MaxStatementBytes.ToString(CultureInfo.InvariantCulture)}",
                    text,
                    tool,
                    parsed: false);
            }

            return SqlParsing.RunWithLargeStack(() => EvaluateCore(text, tool, profile));
        }
        catch (PolicyComplexityException)
        {
            return StructuralBlock(PolicyRules.TooComplex, null, text, tool, parsed: false);
        }
        catch (Exception)
        {
            // Fail-closed (RS-POL-02): un error inesperado del análisis nunca permite la sentencia.
            return StructuralBlock(PolicyRules.InternalError, null, text, tool, parsed: false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// RS-LIM, RF-DML-03/05, RS-POL-10/14, CA-12, CA-13: máximo de filas afectadas (POL-LIM-001, LIMIT_EXCEEDED),
    /// umbral de confirmación de DML, costo estimado del plan, productos cartesianos sobre tablas grandes, índices
    /// sobre tablas grandes y reglas del perfil con MinEstimatedRows / MinEstimatedCost. Devuelve una evaluación nueva
    /// cuya acción es la más restrictiva entre la previa y las nuevas coincidencias: nunca relaja la decisión.
    /// </remarks>
    public PolicyEvaluation ApplyRuntimeFacts(PolicyEvaluation evaluation, RuntimeFacts facts, ProfileRuntime profile)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(profile);
        if (evaluation.IsBlocked)
        {
            return evaluation;
        }

        var info = evaluation.Statement;
        var limits = profile.Limits;
        var sink = new HitSink();
        var category = info.Category;

        if (category is StatementCategory.Insert or StatementCategory.Update or StatementCategory.Delete or StatementCategory.Merge
            && facts.EstimatedRows is { } rows)
        {
            if (rows > limits.MaxAffectedRows)
            {
                sink.Add(PolicyRules.MaxAffectedRows, $"{Format(rows)} filas estimadas; máximo {Format(limits.MaxAffectedRows)}");
            }
            else if (rows > limits.ConfirmAboveRows)
            {
                sink.Add(PolicyRules.DmlThreshold, $"{Format(rows)} filas estimadas; umbral {Format(limits.ConfirmAboveRows)}");
            }

            // Dentro de una transacción explícita, los umbrales se aplican al acumulado: trocear la operación no los evita.
            if (facts.TransactionRows is { } prior && prior > 0)
            {
                var total = prior + rows;
                var detail = $"{Format(prior)} filas ya afectadas en la transacción + {Format(rows)} estimadas = {Format(total)}";
                if (total > limits.MaxAffectedRows && rows <= limits.MaxAffectedRows)
                {
                    sink.Add(PolicyRules.TransactionMaxAffectedRows, $"{detail}; máximo {Format(limits.MaxAffectedRows)}");
                }
                else if (total > limits.ConfirmAboveRows && rows <= limits.ConfirmAboveRows)
                {
                    sink.Add(PolicyRules.TransactionThreshold, $"{detail}; umbral {Format(limits.ConfirmAboveRows)}");
                }
            }
        }

        if (category == StatementCategory.Query)
        {
            if (limits.ConfirmAboveEstimatedCost is { } threshold && facts.EstimatedCost is { } cost && cost >= threshold)
            {
                sink.Add(PolicyRules.EstimatedCost, $"costo estimado {cost.ToString("0.###", CultureInfo.InvariantCulture)}; umbral {threshold.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            if (info.HasCartesianJoin && facts.TableRows is { } estimated && estimated >= limits.LargeTableRows)
            {
                sink.Add(PolicyRules.CartesianProduct, $"{Format(estimated)} filas estimadas");
            }
        }

        if (category == StatementCategory.DdlCreate && string.Equals(info.ObjectType, "INDEX", StringComparison.Ordinal)
            && facts.TableRows is { } tableRows && tableRows >= limits.LargeTableRows)
        {
            sink.Add(PolicyRules.LargeTableIndex, $"{Format(tableRows)} filas en la tabla");
        }

        ApplyRuntimeRules(sink, info, facts, profile, evaluation.Risk);
        return sink.Count == 0 ? evaluation : Merge(evaluation, sink.Hits);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-PRC-02/03, sección 5.4, Anexo C, CA-18, CA-30: servidor → bloqueado; base de datos permitida y no del sistema;
    /// listas de denegación (críticas); prefijos sp_/xp_ bloqueados salvo sp_help, sp_helptext y
    /// sp_describe_first_result_set (POL-PRC-SYS-RO); esquema permitido y no excluido; procedimientos denegados y lista
    /// de permitidos del perfil (con confirmación opcional).
    /// </remarks>
    public PolicyEvaluation EvaluateProcedure(SqlObjectName procedure, ProfileRuntime profile)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            var ctx = new AnalysisContext(profile, ToolKind.Any);
            var name = Clean(procedure);
            var category = StatementCategory.ExecuteProcedure;
            string normalized;
            if (!IsValidName(name))
            {
                ctx.Hits.Add(PolicyRules.ProcedureInvalidName);
                normalized = SqlParsing.RawNormalized("EXEC " + procedure);
            }
            else
            {
                ctx.AddObject(null, name, ObjectRole.Execute, "PROCEDURE");
                var decision = ProcedureRules.Check(ctx, name, out var detail);
                if (decision is not null)
                {
                    ConfigurableRules.ApplyDecision(
                        ctx,
                        new CategoryDecision(decision, decision.Action, decision.Action, detail),
                        StatementCategory.ExecuteProcedure,
                        "EXECUTE",
                        "PROCEDURE",
                        name,
                        ctx.Objects,
                        ctx.Columns,
                        isRoot: true);
                }

                category = ProcedureCategory(name);
                normalized = "EXEC " + QuotedName(name);
            }

            var info = new SqlStatementInfo
            {
                Parsed = true,
                BatchCount = 1,
                StatementCount = 1,
                StatementType = nameof(ExecuteStatement),
                Category = category,
                Operation = "EXECUTE",
                ObjectType = "PROCEDURE",
                TargetObject = name,
                Objects = ctx.Objects,
                NormalizedSql = normalized,
                Hash = SqlParsing.Hash(normalized),
            };
            return PolicyDecision.Build(info, ToolKind.Any, ctx.Hits.Hits, ctx.Warnings, []);
        }
        catch (Exception)
        {
            return StructuralBlock(PolicyRules.InternalError, null, "EXEC " + procedure, ToolKind.Any, parsed: false);
        }
    }

    /// <inheritdoc />
    public PolicyEvaluation EvaluateModule(string definition, ProfileRuntime profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var text = definition ?? string.Empty;
        try
        {
            return SqlParsing.RunWithLargeStack(() => EvaluateModuleCore(text, profile));
        }
        catch (PolicyComplexityException)
        {
            return StructuralBlock(PolicyRules.TooComplex, null, text, ToolKind.Ddl, parsed: false);
        }
        catch (Exception)
        {
            return StructuralBlock(PolicyRules.InternalError, null, text, ToolKind.Ddl, parsed: false);
        }
    }

    private static PolicyEvaluation EvaluateModuleCore(string definition, ProfileRuntime profile)
    {
        var script = SqlParsing.Parse(definition, out var errors);
        if (errors.Count > 0 || script is null)
        {
            return ParseFailure(definition, ToolKind.Ddl, errors);
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements is not [ProcedureStatementBody root])
        {
            return StructuralBlock(PolicyRules.ModuleDefinitionUnavailable, "la definición no es un único CREATE PROCEDURE", definition, ToolKind.Ddl, parsed: true);
        }

        var ctx = new AnalysisContext(profile, ToolKind.Ddl) { ModuleInspection = true };
        new SqlAnalyzer(ctx, AnalyzerMode.Root).Analyze(root);
        foreach (var unit in ctx.Units.Where(u => !u.IsRoot))
        {
            ConfigurableRules.Apply(ctx, unit);
        }

        // El CREATE raíz no se ejecuta: una definición sin coincidencias de bloqueo o confirmación es ejecutable.
        ctx.Hits.Add(PolicyRules.ProcedureAllowed, "definición analizada");
        var normalized = SqlParsing.Generate(root) ?? SqlParsing.RawNormalized(definition);
        var info = new SqlStatementInfo
        {
            Parsed = true,
            BatchCount = 1,
            StatementCount = 1,
            StatementType = root.GetType().Name,
            Category = StatementCategory.ExecuteProcedure,
            Operation = "EXECUTE",
            ObjectType = "PROCEDURE",
            TargetObject = root.ProcedureReference?.Name is { } procedureName ? PolicyNames.From(procedureName) : null,
            Objects = ctx.Objects,
            Columns = ctx.Columns,
            Functions = ctx.Functions,
            NestedStatementTypes = ctx.NestedStatementTypes,
            NormalizedSql = normalized,
            Hash = SqlParsing.Hash(normalized),
            Fragment = root,
        };

        return PolicyDecision.Build(info, ToolKind.Ddl, ctx.Hits.Hits, ctx.Warnings, []);
    }

    // =====================================================================================================
    // Evaluación de texto libre
    // =====================================================================================================

    private static PolicyEvaluation EvaluateCore(string sql, ToolKind tool, ProfileRuntime profile)
    {
        // Incluye el pre-chequeo léxico sin recursión (protección DoS antes de invocar al parser recursivo).
        var script = SqlParsing.Parse(sql, out var errors);
        if (errors.Count > 0 || script is null)
        {
            return ParseFailure(sql, tool, errors);
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (script.Batches.Count == 0 || statements.Count == 0)
        {
            return StructuralBlock(PolicyRules.EmptyStatement, null, sql, tool, parsed: true, batches: script.Batches.Count);
        }

        if (script.Batches.Count != 1 || statements.Count != 1)
        {
            return MultipleStatements(sql, script, statements, tool, profile);
        }

        return EvaluateSingle(sql, statements[0], tool, profile);
    }

    private static PolicyEvaluation EvaluateSingle(string sql, TSqlStatement root, ToolKind tool, ProfileRuntime profile)
    {
        var ctx = new AnalysisContext(profile, tool);
        new SqlAnalyzer(ctx, AnalyzerMode.Root).Analyze(root);
        foreach (var unit in ctx.Units)
        {
            ConfigurableRules.Apply(ctx, unit);
        }

        var rootUnit = ctx.Units.FirstOrDefault(u => u.IsRoot);
        var normalized = SqlParsing.Generate(root) ?? SqlParsing.RawNormalized(sql);
        var info = new SqlStatementInfo
        {
            Parsed = true,
            BatchCount = 1,
            StatementCount = 1,
            StatementType = root.GetType().Name,
            Category = rootUnit?.Class.Category ?? StatementCategory.Unknown,
            Operation = rootUnit?.Class.Operation,
            ObjectType = rootUnit?.Class.ObjectType,
            TargetObject = rootUnit?.Target,
            Objects = ctx.Objects,
            Columns = ctx.Columns,
            Functions = ctx.Functions,
            NestedStatementTypes = ctx.NestedStatementTypes,
            HasSelectStar = ctx.HasSelectStar,
            HasWhereClause = rootUnit?.HasWhere ?? false,
            HasTrivialPredicate = rootUnit?.TrivialPredicate ?? false,
            HasTopClause = rootUnit?.HasTop ?? false,
            HasOutputClause = rootUnit?.HasOutput ?? false,
            HasCartesianJoin = ctx.HasCartesianJoin,
            NormalizedSql = normalized,
            Hash = SqlParsing.Hash(normalized),
            Fragment = root,
        };

        return PolicyDecision.Build(info, tool, ctx.Hits.Hits, ctx.Warnings, ctx.MaskOrdinals);
    }

    /// <summary>
    /// Lote con varias sentencias o separadores GO (RS-POL-03, CA-02, CA-03): siempre BLOQUEAR con POL-BATCH-001. Las
    /// sentencias se analizan igualmente para que la auditoría y las alertas reflejen intentos críticos (RS-DCL-03).
    /// </summary>
    private static PolicyEvaluation MultipleStatements(string sql, TSqlScript script, List<TSqlStatement> statements, ToolKind tool, ProfileRuntime profile)
    {
        var ctx = new AnalysisContext(profile, tool);
        ctx.Hits.Add(
            PolicyRules.MultipleStatements,
            $"{statements.Count.ToString(CultureInfo.InvariantCulture)} sentencias en {script.Batches.Count.ToString(CultureInfo.InvariantCulture)} lote(s)");
        try
        {
            var analyzer = new SqlAnalyzer(ctx, AnalyzerMode.Batch);
            foreach (var statement in statements)
            {
                analyzer.Analyze(statement);
            }
        }
        catch (PolicyComplexityException)
        {
            // El lote ya está bloqueado; el análisis adicional es solo informativo.
        }

        var normalized = SqlParsing.Generate(script) ?? SqlParsing.RawNormalized(sql);
        var info = new SqlStatementInfo
        {
            Parsed = true,
            BatchCount = script.Batches.Count,
            StatementCount = statements.Count,
            Category = StatementCategory.Unknown,
            Objects = ctx.Objects,
            Columns = ctx.Columns,
            Functions = ctx.Functions,
            NestedStatementTypes = statements.Select(s => s.GetType().Name).ToList(),
            HasSelectStar = ctx.HasSelectStar,
            HasCartesianJoin = ctx.HasCartesianJoin,
            NormalizedSql = normalized,
            Hash = SqlParsing.Hash(normalized),
        };

        return PolicyDecision.Build(info, tool, ctx.Hits.Hits, ctx.Warnings, []);
    }

    private static PolicyEvaluation ParseFailure(string sql, ToolKind tool, IList<ParseError> errors)
    {
        var parseErrors = errors.Select(e => new SqlParseError(e.Number, e.Line, e.Column, e.Message)).ToList();
        var first = errors.Count > 0 ? errors[0] : null;
        var detail = first is null
            ? null
            : $"cerca de '{NearText(sql, first.Offset)}' (línea {first.Line.ToString(CultureInfo.InvariantCulture)}, columna {first.Column.ToString(CultureInfo.InvariantCulture)}, código {first.Number.ToString(CultureInfo.InvariantCulture)})";
        return StructuralBlock(PolicyRules.ParseError, detail, sql, tool, parsed: false, parseErrors: parseErrors);
    }

    /// <summary>Texto cercano a la posición del error (no se usa el mensaje del parser, que está localizado).</summary>
    private static string NearText(string sql, int offset)
    {
        if (offset < 0 || offset >= sql.Length)
        {
            return "fin del texto";
        }

        var sb = new StringBuilder();
        for (var i = offset; i < sql.Length && sb.Length < 30; i++)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                break;
            }

            sb.Append(c == '\'' ? '"' : c);
        }

        return sb.Length == 0 ? "fin del texto" : sb.ToString();
    }

    private static PolicyEvaluation StructuralBlock(
        PolicyRule rule,
        string? detail,
        string sql,
        ToolKind tool,
        bool parsed,
        int batches = 0,
        IReadOnlyList<SqlParseError>? parseErrors = null)
    {
        var sink = new HitSink();
        sink.Add(rule, detail);
        var normalized = SqlParsing.RawNormalized(sql);
        var info = new SqlStatementInfo
        {
            Parsed = parsed,
            ParseErrors = parseErrors ?? [],
            BatchCount = batches,
            StatementCount = 0,
            Category = StatementCategory.Unknown,
            NormalizedSql = normalized,
            Hash = SqlParsing.Hash(normalized),
        };
        return PolicyDecision.Build(info, tool, sink.Hits, [], []);
    }

    // =====================================================================================================
    // Hechos de ejecución
    // =====================================================================================================

    private static void ApplyRuntimeRules(HitSink sink, SqlStatementInfo info, RuntimeFacts facts, ProfileRuntime profile, RiskLevel risk)
    {
        var rules = profile.Policy.Rules.Where(r => r.MinEstimatedRows is not null || r.MinEstimatedCost is not null).ToList();
        if (rules.Count == 0)
        {
            return;
        }

        var rows = facts.EstimatedRows ?? facts.TableRows;
        var names = ConfigurableRules.StatementNames(info.Category, info.Operation);
        foreach (var rule in rules)
        {
            if (rule.MinEstimatedRows is { } minRows && (rows is null || rows < minRows))
            {
                continue;
            }

            if (rule.MinEstimatedCost is { } minCost && (facts.EstimatedCost is null || facts.EstimatedCost < minCost))
            {
                continue;
            }

            if (!ConfigurableRules.MatchesStatic(rule, names, info.TargetObject, info.Objects, info.Columns, info.ObjectType))
            {
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(rule.Reason) ? $"Regla del perfil {rule.Id}." : rule.Reason.Trim();
            var measured = rule.MinEstimatedRows is not null
                ? $" ({Format(rows ?? 0)} filas estimadas)"
                : $" (costo estimado {(facts.EstimatedCost ?? 0).ToString("0.###", CultureInfo.InvariantCulture)})";
            sink.AddRaw(
                string.IsNullOrWhiteSpace(rule.Id) ? "POL-RULE" : rule.Id.Trim(),
                rule.Action,
                reason.TrimEnd('.') + measured + ".",
                $"Regla configurada por el administrador ({rule.Id}). Reduzca el alcance de la operación o espere la aprobación.",
                rule.Action is PolicyAction.Block or PolicyAction.Alert ? AlertSeverity.Warning : null,
                immutable: false,
                risk,
                HitPhase.Runtime);
        }
    }

    /// <summary>Añade una regla del catálogo a una evaluación existente sin relajarla (fail-closed en capas con E/S).</summary>
    internal static PolicyEvaluation AddRule(PolicyEvaluation evaluation, PolicyRule rule, string? detail)
    {
        var sink = new HitSink();
        sink.Add(rule, detail);
        return Merge(evaluation, sink.Hits);
    }

    /// <summary>
    /// Combina la evaluación de una sentencia con la de una forma derivada (la misma sentencia con un sinónimo sustituido
    /// por su objeto base, o la definición de un procedimiento): gana la acción más restrictiva y se conservan las
    /// coincidencias, advertencias y columnas a enmascarar de ambas.
    /// </summary>
    internal static PolicyEvaluation Combine(PolicyEvaluation evaluation, PolicyEvaluation derived, string context)
    {
        var result = new PolicyEvaluation
        {
            Statement = evaluation.Statement,
            Tool = evaluation.Tool,
            Action = evaluation.Action,
            RuleId = evaluation.RuleId,
            Reason = evaluation.Reason,
            Suggestion = evaluation.Suggestion,
            Risk = derived.Risk > evaluation.Risk ? derived.Risk : evaluation.Risk,
            Alert = PolicyDecision.MaxAlert([evaluation.Alert, derived.Alert]),
            ErrorCode = evaluation.ErrorCode,
            Hits = [.. evaluation.Hits, .. derived.Hits.Where(h => h.Action > PolicyAction.Allow).Select(h => h with { Reason = $"{h.Reason} ({context})" })],
            Warnings = [.. evaluation.Warnings.Concat(derived.Warnings).Distinct(StringComparer.Ordinal)],
            MaskOrdinals = [.. evaluation.MaskOrdinals.Concat(derived.MaskOrdinals).Distinct().Order()],
        };

        if (derived.Action > evaluation.Action)
        {
            result.Action = derived.Action;
            result.RuleId = derived.RuleId;
            result.Reason = $"{derived.Reason.TrimEnd('.')} ({context}).";
            result.Suggestion = derived.Suggestion;
            result.ErrorCode = derived.Action == PolicyAction.Block ? derived.ErrorCode : null;
        }

        return result;
    }

    /// <summary>Combina la evaluación previa con nuevas coincidencias sin relajar nunca la acción.</summary>
    private static PolicyEvaluation Merge(PolicyEvaluation evaluation, IReadOnlyList<PolicyHit> added)
    {
        var ordered = PolicyDecision.Order(added);
        var result = new PolicyEvaluation
        {
            Statement = evaluation.Statement,
            Tool = evaluation.Tool,
            Action = evaluation.Action,
            RuleId = evaluation.RuleId,
            Reason = evaluation.Reason,
            Suggestion = evaluation.Suggestion,
            Risk = evaluation.Risk,
            Alert = evaluation.Alert,
            ErrorCode = evaluation.ErrorCode,
            Hits = [.. evaluation.Hits, .. ordered.Select(h => h.ToRuleHit())],
            Warnings = [.. evaluation.Warnings],
            MaskOrdinals = [.. evaluation.MaskOrdinals],
        };

        var maxAdded = ordered.Max(h => h.Action);
        if (maxAdded > evaluation.Action)
        {
            var determining = ordered.First(h => h.Action == maxAdded);
            result.Action = maxAdded;
            result.RuleId = determining.RuleId;
            result.Reason = determining.Reason;
            result.Suggestion = determining.Suggestion;
            result.ErrorCode = maxAdded == PolicyAction.Block ? determining.ErrorCode : null;
        }

        var maxRisk = ordered.Max(h => h.Risk);
        result.Risk = maxRisk > evaluation.Risk ? maxRisk : evaluation.Risk;
        result.Alert = PolicyDecision.MaxAlert(ordered.Select(h => PolicyDecision.EffectiveAlert(h.Alert, h.Action)).Append(evaluation.Alert));
        return result;
    }

    // =====================================================================================================
    // Auxiliares de procedimientos
    // =====================================================================================================

    private static SqlObjectName Clean(SqlObjectName name)
    {
        static string? Part(string? value) => string.IsNullOrEmpty(value) ? null : PolicyNames.Clean(value);
        return new SqlObjectName(Part(name.Server), Part(name.Database), Part(name.Schema), PolicyNames.Clean(name.Name));
    }

    private static bool IsValidName(SqlObjectName name) =>
        SqlNames.IsValidIdentifier(name.Name)
        && (name.Server is null || SqlNames.IsValidIdentifier(name.Server))
        && (name.Database is null || SqlNames.IsValidIdentifier(name.Database))
        && (name.Schema is null || SqlNames.IsValidIdentifier(name.Schema));

    private static StatementCategory ProcedureCategory(SqlObjectName name)
    {
        if (name.Server is not null || Denylist.IsMsdb(name.Database))
        {
            return StatementCategory.ExternalAccess;
        }

        return Denylist.ProcedureRule(name.Name) is { } rule ? StatementClassifier.CategoryOf(rule) : StatementCategory.ExecuteProcedure;
    }

    /// <summary>"[esquema].[nombre]" (con base de datos o servidor si se indicaron) para auditoría y hash.</summary>
    private static string QuotedName(SqlObjectName name)
    {
        var parts = new List<string>(4);
        if (name.Server is not null)
        {
            parts.Add(SqlNames.Quote(name.Server));
        }

        if (name.Server is not null || name.Database is not null)
        {
            parts.Add(name.Database is null ? string.Empty : SqlNames.Quote(name.Database));
        }

        var schema = name.Schema
            ?? (Denylist.HasSystemProcedurePrefix(name.Name) ? "sys" : PolicyNames.DefaultSchema);
        parts.Add(SqlNames.Quote(schema));
        parts.Add(SqlNames.Quote(name.Name));
        return string.Join('.', parts);
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
