using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Configuration;

public enum ProfileStatus
{
    /// <summary>No se ha podido verificar todavía (por ejemplo, servidor inaccesible al iniciar).</summary>
    Unverified = 0,

    /// <summary>Conexión y permisos verificados.</summary>
    Verified = 1,

    /// <summary>Rechazado: el login tiene privilegios peligrosos (RS-DCL-05). No se usa hasta reiniciar.</summary>
    Rejected = 2,
}

/// <summary>
/// Perfil ya validado y normalizado, listo para usar en tiempo de ejecución.
/// Se construye una sola vez al iniciar (RF-CFG-05: los cambios requieren reinicio).
/// </summary>
public sealed class ProfileRuntime
{
    private static readonly string[] SystemDatabases = ["master", "msdb", "model", "tempdb", "distribution"];
    private static readonly string[] SystemSchemas = ["sys", "INFORMATION_SCHEMA", "guest"];

    public ProfileRuntime(string name, ProfileConfig config, LimitsConfig effectiveLimits)
    {
        Name = name;
        Config = config;
        Limits = effectiveLimits;
        InitialDatabase = config.Connection.Database;

        var dbs = config.AllowedDatabases.Count == 0 ? [config.Connection.Database] : config.AllowedDatabases;
        AllowedDatabases = new HashSet<string>(dbs.Select(SqlNames.Normalize), StringComparer.OrdinalIgnoreCase)
        {
            InitialDatabase,
        };

        var schemas = config.AllowedSchemas.Count == 0 ? ["dbo"] : config.AllowedSchemas;
        AllowedSchemas = new HashSet<string>(schemas.Select(SqlNames.Normalize), StringComparer.OrdinalIgnoreCase);
        DdlSchemas = new HashSet<string>(config.DdlSchemas.Select(SqlNames.Normalize), StringComparer.OrdinalIgnoreCase);
        ExcludedSchemas = new HashSet<string>(config.Excluded.Schemas.Select(SqlNames.Normalize), StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public ProfileConfig Config { get; }

    public OperationMode Mode => Config.Mode;

    /// <summary>Límites efectivos (los del perfil acotados por los máximos absolutos).</summary>
    public LimitsConfig Limits { get; }

    public PolicyConfig Policy => Config.Policy;

    public string InitialDatabase { get; }

    public IReadOnlySet<string> AllowedDatabases { get; }

    public IReadOnlySet<string> AllowedSchemas { get; }

    public IReadOnlySet<string> DdlSchemas { get; }

    public IReadOnlySet<string> ExcludedSchemas { get; }

    public ProfileStatus Status { get; set; } = ProfileStatus.Unverified;

    /// <summary>Motivo del rechazo, si <see cref="Status"/> es <see cref="ProfileStatus.Rejected"/>.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>Información del servidor obtenida en la verificación (versión, edición).</summary>
    public string? ServerVersion { get; set; }

    /// <summary>Advertencias de seguridad de la verificación (permisos que convendría denegar). No impiden el uso.</summary>
    public List<string> SecurityWarnings { get; } = [];

    /// <summary>Indica si el perfil tiene un login distinto para escritura (RF-CON-07).</summary>
    public bool HasSeparateWriter => Config.WriteConnection is not null;

    public bool ModeAllows(OperationMode required) => Mode >= required;

    public static bool IsSystemDatabase(string? database) =>
        database is not null && SystemDatabases.Contains(database, StringComparer.OrdinalIgnoreCase);

    public static bool IsSystemSchema(string? schema) =>
        schema is not null && SystemSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase);

    public bool IsDatabaseAllowed(string? database) =>
        database is null || (AllowedDatabases.Contains(database) && !IsSystemDatabase(database));

    /// <summary>Esquema visible/utilizable: en la lista permitida y no excluido.</summary>
    public bool IsSchemaAllowed(string? schema)
    {
        var s = schema ?? "dbo";
        return AllowedSchemas.Contains(s) && !ExcludedSchemas.Contains(s);
    }

    public bool IsDdlSchemaAllowed(string? schema)
    {
        var s = schema ?? "dbo";
        return DdlSchemas.Contains(s) && !ExcludedSchemas.Contains(s);
    }

    public bool IsSchemaExcluded(string? schema) => ExcludedSchemas.Contains(schema ?? "dbo");

    /// <summary>Tabla o vista excluida (por esquema o por patrón "esquema.objeto").</summary>
    public bool IsObjectExcluded(string? schema, string name)
    {
        var s = schema ?? "dbo";
        if (ExcludedSchemas.Contains(s))
        {
            return true;
        }

        foreach (var pattern in Config.Excluded.Tables)
        {
            var parts = SqlNames.SplitMultipart(pattern, 2);
            if (parts is null)
            {
                continue;
            }

            if (parts.Count == 1 ? SqlNames.WildcardMatch(parts[0], name)
                : SqlNames.WildcardMatch(parts[0], s) && SqlNames.WildcardMatch(parts[1], name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Columna excluida. <paramref name="schema"/> y <paramref name="table"/> pueden ser null cuando no se
    /// puede resolver la tabla de origen; en ese caso solo coinciden los patrones con comodín en la tabla.
    /// </summary>
    public bool IsColumnExcluded(string? schema, string? table, string column) =>
        MatchesColumnPattern(Config.Excluded.Columns, schema, table, column, keywordSegments: false);

    /// <summary>Columna sensible (secreto) por palabra clave, comodín o patrón calificado (RS-DAT-02).</summary>
    public bool IsColumnSensitive(string? schema, string? table, string column) =>
        MatchesColumnPattern(Config.Sensitive.Patterns, schema, table, column, keywordSegments: true);

    /// <summary>Columna con datos personales (PII), con independencia de la acción configurada.</summary>
    public bool IsColumnPii(string? schema, string? table, string column) =>
        MatchesColumnPattern(Config.Pii.Patterns, schema, table, column, keywordSegments: true);

    /// <summary>True si la PII se devuelve enmascarada.</summary>
    public bool MasksPii => Config.Pii.Action == PiiColumnAction.Mask && Config.Pii.Patterns.Count > 0;

    /// <summary>Columna PII que debe enmascararse en los resultados.</summary>
    public bool IsColumnPiiMasked(string? schema, string? table, string column) => MasksPii && IsColumnPii(schema, table, column);

    /// <remarks>
    /// Criterio conservador: si la tabla o el esquema de origen no se pueden resolver (null),
    /// esa parte del patrón se considera coincidente para no dejar pasar una columna protegida.
    /// Con <paramref name="keywordSegments"/>, una palabra sin comodines coincide por segmentos de palabra y los comodines
    /// también se comparan con el nombre sin separadores ("*password*" coincide con "pass_word").
    /// </remarks>
    private static bool MatchesColumnPattern(IEnumerable<string> patterns, string? schema, string? table, string column, bool keywordSegments)
    {
        foreach (var pattern in patterns)
        {
            var parts = SqlNames.SplitMultipart(pattern, 3);
            if (parts is null)
            {
                continue;
            }

            var columnPattern = parts[^1];
            bool ColumnMatches() =>
                keywordSegments && ColumnNameMatcher.IsKeyword(columnPattern)
                    ? ColumnNameMatcher.MatchesKeyword(columnPattern, column)
                    : SqlNames.WildcardMatch(columnPattern, column)
                      || (keywordSegments && SqlNames.WildcardMatch(columnPattern, ColumnNameMatcher.Compact(column)));

            switch (parts.Count)
            {
                case 1:
                    if (ColumnMatches())
                    {
                        return true;
                    }

                    break;
                case 2:
                    if (ColumnMatches() && (table is null || SqlNames.WildcardMatch(parts[0], table)))
                    {
                        return true;
                    }

                    break;
                case 3:
                    if (ColumnMatches()
                        && (table is null || SqlNames.WildcardMatch(parts[1], table))
                        && (schema is null || table is null || SqlNames.WildcardMatch(parts[0], schema)))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }
}
