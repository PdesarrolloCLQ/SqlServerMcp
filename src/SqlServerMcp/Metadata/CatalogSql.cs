using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Parámetros de una consulta de catálogo. Todos los valores (incluidos los del agente) viajan como
/// <see cref="SqlParameter"/> tipados; nunca se concatenan al texto SQL (RS-INJ-01).
/// </summary>
internal sealed class CatalogParameters
{
    private readonly List<(SqlParameter Parameter, string Declaration)> _items = [];
    private int _sequence;

    public int Count => _items.Count;

    /// <summary>Parámetro nvarchar con nombre explícito ("@schema").</summary>
    public string NVarChar(string name, string? value, int size = SqlNames.MaxIdentifierLength) =>
        Add(name, SqlDbType.NVarChar, size, value);

    /// <summary>Parámetro nvarchar con nombre generado ("@f0", "@f1"…).</summary>
    public string NextNVarChar(string? value, int size = 512) =>
        Add("@f" + (_sequence++).ToString(CultureInfo.InvariantCulture), SqlDbType.NVarChar, size, value);

    public string Int(string name, int value) => Add(name, SqlDbType.Int, 0, value);

    public string Bit(string name, bool value) => Add(name, SqlDbType.Bit, 0, value);

    /// <summary>Declaración de parámetros para sys.sp_executesql ("@a int, @b nvarchar(128)").</summary>
    public string Declarations => string.Join(", ", _items.Select(i => i.Declaration));

    public void CopyTo(SqlParameterCollection target)
    {
        foreach (var (parameter, _) in _items)
        {
            target.Add(parameter);
        }
    }

    private string Add(string name, SqlDbType type, int size, object? value)
    {
        var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
        string declaration;
        switch (type)
        {
            case SqlDbType.NVarChar:
                parameter.Size = size;
                declaration = size < 0 || size > 4000 ? "nvarchar(max)" : "nvarchar(" + size.ToString(CultureInfo.InvariantCulture) + ")";
                break;
            case SqlDbType.Int:
                declaration = "int";
                break;
            case SqlDbType.Bit:
                declaration = "bit";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        _items.Add((parameter, name + " " + declaration));
        return name;
    }
}

/// <summary>Ayudantes de ejecución de consultas de catálogo (RF-MET-*, RS-INJ-01, RS-LIM-02).</summary>
internal static class CatalogSql
{
    /// <summary>Intercalación insensible a mayúsculas usada para comparar nombres como lo hace el perfil.</summary>
    public const string CiCollation = "Latin1_General_100_CI_AS";

    /// <summary>
    /// Crea un comando de catálogo que se ejecuta en el contexto de <paramref name="database"/>.
    /// Si la base es distinta de la actual de la conexión se invoca <c>[base].sys.sp_executesql</c>: el texto es
    /// estático, la base ya fue validada contra la lista permitida del perfil y se delimita con QUOTENAME (RS-INJ-01).
    /// </summary>
    public static SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        ProfileRuntime profile,
        string? database,
        string sql,
        CatalogParameters parameters)
    {
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandTimeout = Math.Max(1, profile.Limits.CommandTimeoutSeconds);
            if (database is null || string.Equals(database, connection.Database, StringComparison.OrdinalIgnoreCase))
            {
                command.CommandType = CommandType.Text;
                command.CommandText = sql;
                parameters.CopyTo(command.Parameters);
            }
            else
            {
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = SqlNames.Quote(database) + ".sys.sp_executesql";
                command.Parameters.Add(new SqlParameter("@stmt", SqlDbType.NVarChar, -1) { Value = sql });
                if (parameters.Count > 0)
                {
                    command.Parameters.Add(new SqlParameter("@params", SqlDbType.NVarChar, -1) { Value = parameters.Declarations });
                    parameters.CopyTo(command.Parameters);
                }
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Traduce los errores de SQL Server esperables en consultas de catálogo a errores controlados
    /// sin detalles internos (RF-OUT-04): tiempo agotado (-2), bloqueo (1222) y base inaccesible (916, 4060).
    /// </summary>
    public static ToolFailureException? Translate(SqlException exception, ProfileRuntime profile, string? database)
    {
        switch (exception.Number)
        {
            case -2:
                return new ToolFailureException(
                    ErrorCodes.Timeout,
                    $"La consulta de metadatos superó el tiempo máximo de {profile.Limits.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s y fue cancelada.",
                    "Acote la consulta con filtros de esquema o patrón de nombre.");
            case 1222:
                return new ToolFailureException(
                    ErrorCodes.Timeout,
                    "Los metadatos solicitados están bloqueados por otra transacción en curso (tiempo de espera de bloqueo agotado).",
                    "Confirme o revierta la transacción explícita activa y vuelva a intentarlo.");
            case 916:
            case 4060:
            {
                var failure = new ToolFailureException(
                    ErrorCodes.SqlError,
                    $"El login de lectura del perfil no tiene acceso a la base de datos '{database ?? profile.InitialDatabase}'.",
                    "Use list_databases para ver las bases de datos accesibles.");
                failure.Details["sqlErrorNumber"] = exception.Number;
                return failure;
            }

            case 229:
            {
                // Mínimo privilegio: las vistas de dependencias requieren un GRANT explícito del DBA (RF-MET-12).
                var failure = new ToolFailureException(
                    ErrorCodes.SqlError,
                    exception.Message.Contains("sql_expression_dependencies", StringComparison.OrdinalIgnoreCase)
                        ? "El login de lectura no tiene permiso SELECT sobre sys.sql_expression_dependencies, necesario para dependencias, definiciones y búsquedas."
                        : "El login de lectura no tiene permiso sobre una vista de catálogo necesaria para esta operación.",
                    "El DBA debe conceder el permiso indicado en scripts/setup-principals.sql (GRANT SELECT ON sys.sql_expression_dependencies).");
                failure.Details["sqlErrorNumber"] = exception.Number;
                return failure;
            }

            default:
                return null;
        }
    }

    public static string? GetStringOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static string GetStringOrEmpty(this SqlDataReader reader, int ordinal) => reader.GetStringOrNull(ordinal) ?? string.Empty;

    public static int GetInt(this SqlDataReader reader, int ordinal, int fallback = 0) =>
        reader.IsDBNull(ordinal) ? fallback : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static int? GetIntOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static long? GetLongOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static long GetLong(this SqlDataReader reader, int ordinal) => reader.GetLongOrNull(ordinal) ?? 0;

    public static bool GetBool(this SqlDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static DateTime? GetDateTimeOrNull(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    /// <summary>Convierte un valor numérico de texto (sql_variant convertido) en long, o null si no cabe.</summary>
    public static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>Recorta un texto de la base de datos al largo máximo de celda del perfil (RF-QRY-06).</summary>
    public static string? Clip(string? text, int maxChars)
    {
        if (text is null || maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "…";
    }
}

/// <summary>Conversión de patrones con comodines del agente ("*", "?") a patrones LIKE seguros (RF-MET-02, RS-INJ-01).</summary>
internal static class LikePatterns
{
    /// <summary>Carácter de escape usado en todas las cláusulas LIKE generadas: <c>ESCAPE '\'</c>.</summary>
    public const char EscapeChar = '\\';

    /// <summary>
    /// Convierte un patrón con comodines en un patrón LIKE: "*" → "%", "?" → "_", y escapa los caracteres
    /// especiales de LIKE ("%", "_", "[", y el propio carácter de escape) para que se traten literalmente.
    /// El resultado se usa siempre como parámetro con <c>ESCAPE '\'</c>; nunca se concatena al SQL.
    /// </summary>
    /// <param name="pattern">Patrón del agente o de la configuración.</param>
    /// <param name="containsWhenNoWildcards">
    /// Si el patrón no tiene comodines, buscarlo como subcadena ("%texto%") en lugar de coincidencia exacta.
    /// </param>
    public static string FromWildcard(string pattern, bool containsWhenNoWildcards)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var hasWildcards = pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal);
        var sb = new StringBuilder(pattern.Length + 8);
        if (!hasWildcards && containsWhenNoWildcards)
        {
            sb.Append('%');
        }

        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*':
                    sb.Append('%');
                    break;
                case '?':
                    sb.Append('_');
                    break;
                case '%':
                case '_':
                case '[':
                case EscapeChar:
                    sb.Append(EscapeChar).Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        if (!hasWildcards && containsWhenNoWildcards)
        {
            sb.Append('%');
        }

        return sb.ToString();
    }

    /// <summary>Patrón LIKE de "contiene" para un patrón con comodines ("%" + patrón + "%").</summary>
    public static string ContainsWildcard(string pattern) => "%" + FromWildcard(pattern, containsWhenNoWildcards: false) + "%";
}

/// <summary>Normalización de la paginación de las herramientas de metadatos (RF-MET-02).</summary>
internal static class MetadataPaging
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Acota el desplazamiento a ≥ 0 y el tamaño de página a 1..500.</summary>
    public static (int Offset, int Limit) Normalize(int offset, int limit) => (Math.Max(0, offset), Math.Clamp(limit, 1, MaxLimit));

    public static MetadataPage<T> Page<T>(string database, IReadOnlyList<T> items, long totalCount, int offset, int limit)
    {
        var total = (int)Math.Min(int.MaxValue, Math.Max(0, totalCount));
        return new MetadataPage<T>(database, items, total, offset, limit, (long)offset + items.Count < total);
    }

    /// <summary>Pagina en memoria una lista ya filtrada.</summary>
    public static MetadataPage<T> PageInMemory<T>(string database, IReadOnlyList<T> all, int offset, int limit)
    {
        var items = all.Skip(offset).Take(limit).ToList();
        return Page(database, items, all.Count, offset, limit);
    }
}
