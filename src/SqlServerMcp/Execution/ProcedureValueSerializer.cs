using System.Data.SqlTypes;
using System.Globalization;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Execution;

/// <summary>
/// Serializa valores de parámetros de salida con las mismas reglas que los resultados tabulares (RF-PRC-01, RF-QRY-05):
/// null, string, long, double o bool; DECIMAL como texto invariante, fechas ISO 8601, uniqueidentifier como texto y
/// binarios en Base64 truncados a <see cref="LimitsConfig.MaxCellChars"/>.
/// </summary>
public static class ProcedureValueSerializer
{
    /// <summary>Convierte un valor CLR o SqlTypes en un primitivo JSON estable.</summary>
    /// <param name="value">Valor leído de SqlParameter.Value.</param>
    /// <param name="sqlTypeName">Tipo SQL (para distinguir date de datetime); puede ser null.</param>
    /// <param name="maxCellChars">Largo máximo del texto resultante.</param>
    /// <param name="truncated">True si el valor se recortó.</param>
    public static object? Serialize(object? value, string? sqlTypeName, int maxCellChars, out bool truncated)
    {
        truncated = false;
        var limit = Math.Max(16, maxCellChars);
        switch (Unwrap(value))
        {
            case null:
                return null;
            case string s:
                return Truncate(s, limit, ref truncated);
            case char[] chars:
                return Truncate(new string(chars), limit, ref truncated);
            case bool b:
                return b;
            case byte or sbyte or short or ushort or int or uint or long:
                return Convert.ToInt64(Unwrap(value), CultureInfo.InvariantCulture);
            case ulong ul:
                return ul <= long.MaxValue ? (long)ul : ul.ToString(CultureInfo.InvariantCulture);
            case float f:
                return float.IsFinite(f) ? (double)f : f.ToString(CultureInfo.InvariantCulture);
            case double d:
                return double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return string.Equals(sqlTypeName, "date", StringComparison.OrdinalIgnoreCase)
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
            case DateOnly date:
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly time:
                return time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString("c", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString("D");
            case byte[] bytes:
            {
                var maxBytes = limit / 4 * 3;
                if (bytes.Length > maxBytes)
                {
                    truncated = true;
                    return Convert.ToBase64String(bytes, 0, maxBytes);
                }

                return Convert.ToBase64String(bytes);
            }

            case IFormattable formattable:
                return Truncate(formattable.ToString(null, CultureInfo.InvariantCulture), limit, ref truncated);
            case var other:
                return Truncate(other.ToString() ?? string.Empty, limit, ref truncated);
        }
    }

    private static string Truncate(string value, int limit, ref bool truncated)
    {
        if (value.Length <= limit)
        {
            return value;
        }

        truncated = true;
        return value[..limit];
    }

    private static object? Unwrap(object? value) => value switch
    {
        null or DBNull => null,
        INullable { IsNull: true } => null,
        SqlString v => v.Value,
        SqlInt64 v => v.Value,
        SqlInt32 v => v.Value,
        SqlInt16 v => v.Value,
        SqlByte v => v.Value,
        SqlBoolean v => v.Value,
        SqlDecimal v => v.Value,
        SqlMoney v => v.Value,
        SqlDouble v => v.Value,
        SqlSingle v => v.Value,
        SqlDateTime v => v.Value,
        SqlGuid v => v.Value,
        SqlBinary v => v.Value,
        SqlBytes v => v.Value,
        SqlChars v => new string(v.Value),
        SqlXml v => v.Value,
        _ => value,
    };
}
