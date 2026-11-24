using System.Text.RegularExpressions;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Genera los predicados SQL de visibilidad del perfil (RF-MET-06): esquemas permitidos y no excluidos,
/// patrones de tablas excluidas y textos que no deben revelarse. Todos los nombres y patrones viajan como
/// parámetros (<see cref="CatalogParameters"/>); el SQL generado solo contiene nombres de parámetro.
/// Las comparaciones usan una intercalación insensible a mayúsculas para coincidir con <see cref="ProfileRuntime"/>.
/// El filtrado en SQL mantiene coherentes la paginación y los totales; además, cada método vuelve a filtrar en C#
/// con <see cref="ProfileRuntime"/> como red de seguridad.
/// </summary>
internal sealed class VisibilityFilter
{
    private const string Escape = " ESCAPE N'\\'";
    private const string Ci = " COLLATE " + CatalogSql.CiCollation;

    private readonly List<string> _allowedSchemaParameters = [];
    private readonly List<string> _excludedSchemaParameters = [];
    private readonly List<(string? SchemaParameter, string NameParameter)> _objectPatternParameters = [];
    private readonly List<string> _textPatternParameters = [];

    public VisibilityFilter(ProfileRuntime profile, CatalogParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(parameters);

        foreach (var schema in profile.AllowedSchemas.Where(s => !profile.ExcludedSchemas.Contains(s)).Order(StringComparer.OrdinalIgnoreCase))
        {
            _allowedSchemaParameters.Add(parameters.NextNVarChar(schema));
        }

        foreach (var schema in profile.ExcludedSchemas.Order(StringComparer.OrdinalIgnoreCase))
        {
            _excludedSchemaParameters.Add(parameters.NextNVarChar(schema));
        }

        foreach (var pattern in profile.Config.Excluded.Tables)
        {
            var parts = SqlNames.SplitMultipart(pattern, 2);
            if (parts is null)
            {
                continue;
            }

            var nameParameter = parameters.NextNVarChar(LikePatterns.FromWildcard(parts[^1], containsWhenNoWildcards: false));
            var schemaParameter = parts.Count == 2
                ? parameters.NextNVarChar(LikePatterns.FromWildcard(parts[0], containsWhenNoWildcards: false))
                : null;
            _objectPatternParameters.Add((schemaParameter, nameParameter));
        }

        foreach (var pattern in ExclusionText.TextPatterns(profile))
        {
            _textPatternParameters.Add(parameters.NextNVarChar(LikePatterns.ContainsWildcard(pattern)));
        }
    }

    /// <summary>Indica si el perfil no permite ningún esquema (todo resultado será vacío).</summary>
    public bool HasNoVisibleSchemas => _allowedSchemaParameters.Count == 0;

    /// <summary>Predicado: el esquema está en la lista permitida y no está excluido.</summary>
    public string SchemaAllowed(string schemaExpression) =>
        _allowedSchemaParameters.Count == 0
            ? "(1 = 0)"
            : $"({schemaExpression}{Ci} IN ({string.Join(", ", _allowedSchemaParameters)}))";

    /// <summary>
    /// Predicado: el objeto está excluido (esquema excluido o patrón de tabla excluida).
    /// Con <paramref name="unknownSchemaMatches"/> un esquema NULL (referencia sin calificar) coincide con
    /// los patrones "esquema.objeto" (criterio conservador).
    /// </summary>
    public string ObjectExcluded(string schemaExpression, string nameExpression, bool unknownSchemaMatches = false)
    {
        var conditions = new List<string>();
        if (_excludedSchemaParameters.Count > 0)
        {
            conditions.Add($"{schemaExpression}{Ci} IN ({string.Join(", ", _excludedSchemaParameters)})");
        }

        foreach (var (schemaParameter, nameParameter) in _objectPatternParameters)
        {
            var nameCondition = $"{nameExpression}{Ci} LIKE {nameParameter}{Escape}";
            if (schemaParameter is null)
            {
                conditions.Add(nameCondition);
                continue;
            }

            var schemaCondition = unknownSchemaMatches
                ? $"({schemaExpression} IS NULL OR {schemaExpression}{Ci} LIKE {schemaParameter}{Escape})"
                : $"{schemaExpression}{Ci} LIKE {schemaParameter}{Escape}";
            conditions.Add($"({schemaCondition} AND {nameCondition})");
        }

        return conditions.Count == 0 ? "(1 = 0)" : "(" + string.Join(" OR ", conditions) + ")";
    }

    /// <summary>Predicado: objeto visible (esquema permitido y objeto no excluido).</summary>
    public string ObjectVisible(string schemaExpression, string nameExpression) =>
        $"({SchemaAllowed(schemaExpression)} AND NOT {ObjectExcluded(schemaExpression, nameExpression)})";

    /// <summary>Predicado: el texto (definición) no menciona esquemas, tablas ni columnas excluidos.</summary>
    public string TextClean(string textExpression) =>
        _textPatternParameters.Count == 0
            ? "(1 = 1)"
            : "(" + string.Join(" AND ", _textPatternParameters.Select(p => $"{textExpression}{Ci} NOT LIKE {p}{Escape}")) + ")";

    /// <summary>
    /// Expresión EXISTS: el módulo indicado referencia (sys.sql_expression_dependencies) algún objeto excluido.
    /// </summary>
    public string ReferencesExcluded(string objectIdExpression)
    {
        var excluded = ObjectExcluded(
            "COALESCE(SCHEMA_NAME(xobj.schema_id), xdep.referenced_schema_name)",
            "COALESCE(xobj.name, xdep.referenced_entity_name)",
            unknownSchemaMatches: true);
        if (excluded == "(1 = 0)")
        {
            return "(1 = 0)";
        }

        return "EXISTS (SELECT 1 FROM sys.sql_expression_dependencies AS xdep " +
               "LEFT JOIN sys.objects AS xobj ON xobj.object_id = xdep.referenced_id AND xdep.referenced_class = 1 AND xdep.referenced_database_name IS NULL " +
               $"WHERE xdep.referencing_id = {objectIdExpression} AND {excluded})";
    }
}

/// <summary>
/// Comprobaciones en C# de textos devueltos al agente (definiciones, fragmentos, expresiones, descripciones):
/// no deben mencionar esquemas, tablas ni columnas excluidos (RF-MET-06). El criterio es conservador:
/// basta con que el texto contenga el nombre (o el patrón) excluido, sin distinguir mayúsculas.
/// </summary>
internal static class ExclusionText
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Patrones (formato "*"/"?") que no pueden aparecer en textos devueltos: esquemas excluidos, la parte de
    /// nombre de los patrones de tablas excluidas y la parte de columna de los patrones de columnas excluidas.
    /// Si una parte es solo comodines se usa la parte anterior (por ejemplo "temp.*" → "temp").
    /// </summary>
    public static IReadOnlyList<string> TextPatterns(ProfileRuntime profile)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in profile.ExcludedSchemas)
        {
            if (HasLiteral(schema))
            {
                result.Add(schema);
            }
        }

        AddMostSpecific(result, profile.Config.Excluded.Tables, 2);
        AddMostSpecific(result, profile.Config.Excluded.Columns, 3);
        return [.. result];
    }

    /// <summary>Indica si el texto menciona algún elemento excluido del perfil.</summary>
    public static bool Reveals(ProfileRuntime profile, string? text) => Reveals(TextPatterns(profile), text);

    public static bool Reveals(IReadOnlyList<string> patterns, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
            {
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            var regex = Regex.Escape(pattern).Replace("\\*", ".*?", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
            try
            {
                if (Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Fallo cerrado: si no se puede comprobar, se considera que revela.
                return true;
            }
        }

        return false;
    }

    /// <summary>Devuelve el texto si no revela elementos excluidos; si no, null.</summary>
    public static string? Sanitize(IReadOnlyList<string> patterns, string? text) => Reveals(patterns, text) ? null : text;

    internal static bool HasLiteral(string pattern) => pattern.Any(c => c != '*' && c != '?');

    private static void AddMostSpecific(SortedSet<string> target, IEnumerable<string> patterns, int maxParts)
    {
        foreach (var pattern in patterns)
        {
            var parts = SqlNames.SplitMultipart(pattern, maxParts);
            if (parts is null)
            {
                continue;
            }

            for (var i = parts.Count - 1; i >= 0; i--)
            {
                if (HasLiteral(parts[i]))
                {
                    target.Add(parts[i]);
                    break;
                }
            }
        }
    }
}
