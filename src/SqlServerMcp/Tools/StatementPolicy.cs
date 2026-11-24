using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tools;

/// <summary>
/// Política con acceso al catálogo: añade al motor (puro, sin E/S) lo que solo se puede decidir mirando la base de datos.
/// <list type="bullet">
/// <item>Sinónimos: cada objeto referenciado que resulte ser un sinónimo se evalúa también como su objeto base, reescribiendo
/// la sentencia; si no se puede resolver, se bloquea (POL-SYN-002).</item>
/// <item>CREATE SYNONYM: el destino debe existir y ser una tabla, vista, procedimiento o función de usuario (POL-SYN-001).</item>
/// <item>execute_procedure: la definición del procedimiento, y la de los que invoca, se analiza con las reglas de execute_ddl
/// antes de ejecutar (POL-PRC-DEF-001 si no se puede obtener).</item>
/// </list>
/// validate_sql usa el mismo camino que la ejecución, así que devuelve la misma decisión.
/// </summary>
public sealed class StatementPolicy(IPolicyEngine engine, ICatalogResolver catalog)
{
    private const int MaxModuleDepth = 8;
    private const int MaxCachedModules = 2000;

    private static readonly HashSet<string> SynonymTargetTypes = new(StringComparer.OrdinalIgnoreCase) { "U", "V", "P", "FN", "IF", "TF" };

    private readonly ConcurrentDictionary<string, PolicyEvaluation> _moduleCache = new(StringComparer.Ordinal);

    public IPolicyEngine Engine => engine;

    /// <summary>Evalúa una sentencia en texto libre con la resolución de sinónimos y la validación de CREATE SYNONYM.</summary>
    public async Task<PolicyEvaluation> EvaluateAsync(string sql, ToolKind tool, ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        var evaluation = engine.Evaluate(sql, tool, profile);
        if (evaluation.IsBlocked || !evaluation.Statement.Parsed)
        {
            return evaluation;
        }

        evaluation = await ResolveSynonymsAsync(evaluation, sql, text => engine.Evaluate(text, tool, profile), profile, correlationId, cancellationToken).ConfigureAwait(false);
        if (evaluation.IsBlocked)
        {
            return evaluation;
        }

        if (evaluation.Statement is { Category: StatementCategory.DdlCreate, ObjectType: "SYNONYM" })
        {
            evaluation = await CheckSynonymTargetAsync(evaluation, profile, correlationId, cancellationToken).ConfigureAwait(false);
        }

        return evaluation;
    }

    /// <summary>
    /// Evalúa execute_procedure: reglas del nombre, resolución de sinónimos y análisis de la definición (y de los
    /// procedimientos que invoca) antes de ejecutar.
    /// </summary>
    public async Task<PolicyEvaluation> EvaluateProcedureAsync(SqlObjectName name, ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        var evaluation = engine.EvaluateProcedure(name, profile);
        if (evaluation.IsBlocked || evaluation.RuleId == "POL-PRC-SYS-RO")
        {
            // Los procedimientos del sistema de solo lectura de la lista de excepciones no tienen definición de usuario.
            return evaluation;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return await InspectProcedureAsync(evaluation, name, profile, correlationId, depth: 0, visited, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PolicyEngine.AddRule(evaluation, PolicyRules.ModuleDefinitionUnavailable, $"'{PolicyNames.Display(name)}': error al leer el catálogo");
        }
    }

    private async Task<PolicyEvaluation> InspectProcedureAsync(
        PolicyEvaluation evaluation,
        SqlObjectName name,
        ProfileRuntime profile,
        string correlationId,
        int depth,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        var display = PolicyNames.Display(name);
        if (depth > MaxModuleDepth)
        {
            return PolicyEngine.AddRule(evaluation, PolicyRules.ModuleDefinitionUnavailable, $"más de {MaxModuleDepth} niveles de procedimientos anidados ('{display}')");
        }

        if (!visited.Add(name.ToString()))
        {
            return evaluation;
        }

        var objects = await catalog.GetObjectsAsync(profile, name, correlationId, cancellationToken).ConfigureAwait(false);
        if (objects.Count == 0)
        {
            return PolicyEngine.AddRule(evaluation, PolicyRules.ModuleDefinitionUnavailable, $"'{display}' no existe o el login de lectura no puede ver su definición");
        }

        foreach (var obj in objects)
        {
            var objName = obj.FullName;
            switch (obj.Type)
            {
                case "SN":
                    // Sinónimo: se evalúa el objeto base como si se ejecutara directamente y se inspecciona su definición.
                    var target = SynonymRewriter.ParseBaseObjectName(obj.SynonymBase);
                    if (target is null)
                    {
                        return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymUnresolved, $"'{PolicyNames.Display(objName)}'");
                    }

                    var context = $"a través del sinónimo {PolicyNames.Display(objName)} → {obj.SynonymBase}";
                    evaluation = PolicyEngine.Combine(evaluation, engine.EvaluateProcedure(target, profile), context);
                    if (evaluation.IsBlocked)
                    {
                        return evaluation;
                    }

                    evaluation = await InspectProcedureAsync(evaluation, WithDatabase(target, obj.Database), profile, correlationId, depth + 1, visited, cancellationToken).ConfigureAwait(false);
                    break;

                case "P":
                    if (string.IsNullOrEmpty(obj.Definition))
                    {
                        return PolicyEngine.AddRule(evaluation, PolicyRules.ModuleDefinitionUnavailable, $"'{PolicyNames.Display(objName)}' sin definición visible (cifrado o sin VIEW DEFINITION)");
                    }

                    var module = AnalyzeModule(obj.Definition, profile);
                    module = await ResolveSynonymsAsync(module, obj.Definition, text => engine.EvaluateModule(text, profile), profile, correlationId, cancellationToken).ConfigureAwait(false);
                    evaluation = PolicyEngine.Combine(evaluation, module, $"definición de {PolicyNames.Display(objName)}");
                    if (evaluation.IsBlocked)
                    {
                        return evaluation;
                    }

                    // Procedimientos invocados desde el cuerpo: se inspeccionan igual (un EXEC dbo.B no oculta el SQL dinámico de B).
                    foreach (var called in module.Statement.Objects.Where(o => o.Role == ObjectRole.Execute && o.Name.Server is null).Select(o => o.Name).Distinct())
                    {
                        if (Denylist.HasSystemProcedurePrefix(called.Name) && string.IsNullOrEmpty(called.Schema))
                        {
                            continue; // ya evaluado por el analizador (lista de excepciones de solo lectura o bloqueado)
                        }

                        evaluation = await InspectProcedureAsync(evaluation, WithDatabase(called, obj.Database), profile, correlationId, depth + 1, visited, cancellationToken).ConfigureAwait(false);
                        if (evaluation.IsBlocked)
                        {
                            return evaluation;
                        }
                    }

                    break;

                default:
                    // CLR (PC), procedimientos extendidos (X) u otros: no hay definición T-SQL que analizar.
                    return PolicyEngine.AddRule(evaluation, PolicyRules.ModuleDefinitionUnavailable, $"'{PolicyNames.Display(objName)}' es de tipo {obj.Type}");
            }

            if (evaluation.IsBlocked)
            {
                return evaluation;
            }
        }

        return evaluation;
    }

    /// <summary>Análisis estático de la definición, en caché por hash: la misma definición siempre da la misma decisión.</summary>
    private PolicyEvaluation AnalyzeModule(string definition, ProfileRuntime profile)
    {
        var key = profile.Name + "|" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
        if (_moduleCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_moduleCache.Count >= MaxCachedModules)
        {
            _moduleCache.Clear();
        }

        var evaluation = engine.EvaluateModule(definition, profile);
        _moduleCache[key] = evaluation;
        return evaluation;
    }

    /// <summary>
    /// Para cada objeto referenciado que sea un sinónimo, reevalúa la sentencia con el sinónimo sustituido por su objeto
    /// base y combina el resultado (gana lo más restrictivo). Fail-closed si el catálogo no responde o la sustitución falla.
    /// </summary>
    private async Task<PolicyEvaluation> ResolveSynonymsAsync(
        PolicyEvaluation evaluation,
        string text,
        Func<string, PolicyEvaluation> reevaluate,
        ProfileRuntime profile,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var references = evaluation.Statement.Objects
            .Where(o => o.Role != ObjectRole.DdlTarget && o.Name.Server is null && !o.Name.IsTemp && !string.IsNullOrEmpty(o.Name.Name))
            .Select(o => o.Name)
            .Distinct()
            .ToList();
        if (references.Count == 0)
        {
            return evaluation;
        }

        var synonymsByDatabase = new Dictionary<string, IReadOnlyList<SynonymEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var database = string.IsNullOrEmpty(reference.Database) ? profile.InitialDatabase : reference.Database;
            if (!profile.IsDatabaseAllowed(database))
            {
                continue; // la referencia ya está bloqueada por la regla de bases permitidas
            }

            if (!synonymsByDatabase.TryGetValue(database, out var synonyms))
            {
                try
                {
                    synonyms = await catalog.GetSynonymsAsync(profile, database, correlationId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymUnresolved, $"no se pudo consultar sys.synonyms en '{database}'");
                }

                synonymsByDatabase[database] = synonyms;
            }

            foreach (var synonym in synonyms.Where(s => Matches(s, reference)))
            {
                var display = $"{synonym.Schema}.{synonym.Name}";
                var target = SynonymRewriter.ParseBaseObjectName(synonym.BaseObjectName);
                if (target is null)
                {
                    return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymUnresolved, $"'{display}' → '{synonym.BaseObjectName}'");
                }

                var rewritten = SynonymRewriter.Rewrite(text, reference, target, out var replacements);
                if (rewritten is null || replacements == 0)
                {
                    return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymUnresolved, $"'{display}' aparece en una posición que no se puede analizar");
                }

                evaluation = PolicyEngine.Combine(evaluation, reevaluate(rewritten), $"a través del sinónimo {display} → {synonym.BaseObjectName}");
                if (evaluation.IsBlocked)
                {
                    return evaluation;
                }
            }
        }

        return evaluation;
    }

    /// <summary>El destino de CREATE SYNONYM debe existir y ser una tabla, vista, procedimiento o función de usuario.</summary>
    private async Task<PolicyEvaluation> CheckSynonymTargetAsync(PolicyEvaluation evaluation, ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        var target = evaluation.Statement.Objects.FirstOrDefault(o => o.ObjectType == "SYNONYM TARGET")?.Name;
        if (target is null)
        {
            return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymTarget, "sin objeto destino");
        }

        var display = PolicyNames.Display(target);
        IReadOnlyList<ModuleCatalogEntry> objects;
        try
        {
            objects = await catalog.GetObjectsAsync(profile, target, correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymUnresolved, $"no se pudo comprobar el destino '{display}'");
        }

        if (string.IsNullOrEmpty(target.Schema))
        {
            // Un nombre de una parte se resuelve en el esquema del usuario o en dbo: se exige que el de dbo sea válido.
            objects = objects.Where(o => string.Equals(o.Schema, PolicyNames.DefaultSchema, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (objects.Count == 0)
        {
            return PolicyEngine.AddRule(evaluation, PolicyRules.SynonymTarget, $"destino '{display}' no existe o no es visible para el login de lectura");
        }

        var invalid = objects.FirstOrDefault(o => !SynonymTargetTypes.Contains(o.Type));
        return invalid is null
            ? evaluation
            : PolicyEngine.AddRule(evaluation, PolicyRules.SynonymTarget, $"destino '{display}' es de tipo {invalid.Type} (solo tablas, vistas, procedimientos y funciones de usuario)");
    }

    /// <summary>Un nombre sin esquema coincide con los sinónimos de cualquier esquema (criterio conservador).</summary>
    private static bool Matches(SynonymEntry synonym, SqlObjectName reference) =>
        string.Equals(synonym.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(reference.Schema) || string.Equals(synonym.Schema, reference.Schema, StringComparison.OrdinalIgnoreCase));

    private static SqlObjectName WithDatabase(SqlObjectName name, string database) =>
        string.IsNullOrEmpty(name.Database) ? name with { Database = database } : name;
}
