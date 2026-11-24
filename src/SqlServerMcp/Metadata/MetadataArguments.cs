using System.Reflection;
using System.Text.Json.Serialization;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Validación de los argumentos que el agente pasa a las herramientas de metadatos (RS-INJ-02):
/// longitud y caracteres de identificadores y patrones, y lista de bases de datos permitidas (RF-CON-05).
/// </summary>
internal static class MetadataArguments
{
    /// <summary>Longitud máxima del texto de search_objects.</summary>
    public const int MaxSearchTextLength = 256;

    /// <summary>
    /// Devuelve el nombre canónico (tal como figura en la configuración) de la base indicada, o la base inicial si es null.
    /// Lanza POLICY_BLOCKED si la base no está en la lista permitida del perfil o es una base del sistema.
    /// </summary>
    public static string ResolveDatabase(ProfileRuntime profile, string? database)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return profile.InitialDatabase;
        }

        var normalized = SqlNames.Normalize(database);
        if (!SqlNames.IsValidIdentifier(normalized))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "El nombre de base de datos no es válido (máximo 128 caracteres, sin caracteres de control).");
        }

        if (!profile.IsDatabaseAllowed(normalized))
        {
            throw new ToolFailureException(
                ErrorCodes.PolicyBlocked,
                $"La base de datos '{normalized}' no está permitida en el perfil '{profile.Name}'.",
                "Use list_databases para ver las bases de datos permitidas.",
                "RF-CON-05");
        }

        if (string.Equals(normalized, profile.InitialDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return profile.InitialDatabase;
        }

        return profile.AllowedDatabases.First(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Identificador obligatorio (sin delimitadores, ≤ 128 caracteres, sin caracteres de control).</summary>
    public static string RequireIdentifier(string? value, string argument)
    {
        var normalized = value is null ? null : SqlNames.Normalize(value);
        if (!SqlNames.IsValidIdentifier(normalized))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"El argumento '{argument}' es obligatorio y debe ser un identificador válido (máximo 128 caracteres, sin caracteres de control).");
        }

        return normalized!;
    }

    /// <summary>Identificador opcional; null o vacío se interpreta como "no indicado".</summary>
    public static string? OptionalIdentifier(string? value, string argument) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireIdentifier(value, argument);

    /// <summary>
    /// Resuelve esquema y nombre de un objeto. Si no se indica esquema, el nombre puede venir como "esquema.objeto";
    /// sin esquema se usa "dbo".
    /// </summary>
    public static (string Schema, string Name) ResolveObjectName(string? schema, string? name, string argument = "name")
    {
        var normalizedSchema = OptionalIdentifier(schema, "schema");
        if (normalizedSchema is null && name is not null && SqlNames.TryParseSchemaObject(name, out var parsedSchema, out var parsedName) && parsedSchema is not null)
        {
            return (parsedSchema, parsedName);
        }

        return (normalizedSchema ?? "dbo", RequireIdentifier(name, argument));
    }

    /// <summary>Patrón de nombre opcional con comodines "*" y "?" (≤ 128 caracteres, sin caracteres de control).</summary>
    public static string? OptionalPattern(string? pattern, string argument)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var trimmed = pattern.Trim();
        if (!SqlNames.IsValidIdentifier(trimmed))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"El argumento '{argument}' no es un patrón válido (máximo 128 caracteres, sin caracteres de control).");
        }

        return trimmed;
    }

    /// <summary>Texto de búsqueda obligatorio (≤ 256 caracteres, sin caracteres de control).</summary>
    public static string RequireSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSearchTextLength || text.Any(char.IsControl))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"El texto de búsqueda es obligatorio y debe tener como máximo {MaxSearchTextLength} caracteres, sin caracteres de control.");
        }

        return text;
    }
}

/// <summary>
/// Conversión de los argumentos de texto de las herramientas a las enumeraciones de <see cref="IMetadataService"/>.
/// Acepta el nombre JSON ("scalar_function") o el nombre del miembro ("ScalarFunction"), sin distinguir mayúsculas.
/// </summary>
public static class MetadataArgumentParser
{
    public static RoutineTypeFilter ParseRoutineType(string? value) => Parse(value, RoutineTypeFilter.Any, "type");

    public static SearchScope ParseSearchScope(string? value) => Parse(value, SearchScope.Both, "searchIn");

    public static SearchObjectType ParseSearchObjectType(string? value) => Parse(value, SearchObjectType.Any, "type");

    public static DependencyDirection ParseDependencyDirection(string? value) => Parse(value, DependencyDirection.Both, "direction");

    public static CatalogObjectKind ParseObjectKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "El argumento 'kind' es obligatorio (trigger, sequence, synonym o type).");
        }

        return Parse(value, CatalogObjectKind.Trigger, "kind");
    }

    /// <summary>Lanza INVALID_ARGUMENT si el valor no corresponde a ningún miembro.</summary>
    public static T Parse<T>(string? value, T defaultValue, string argument)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var text = value.Trim();
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var jsonName = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name;
            if (string.Equals(field.Name, text, StringComparison.OrdinalIgnoreCase)
                || (jsonName is not null && string.Equals(jsonName, text, StringComparison.OrdinalIgnoreCase)))
            {
                return (T)field.GetValue(null)!;
            }
        }

        var allowed = string.Join(", ", typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name));
        throw new ToolFailureException(ErrorCodes.InvalidArgument, $"Valor no válido para '{argument}'. Valores admitidos: {allowed}.");
    }
}
