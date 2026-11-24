using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Construye <see cref="SqlParameter"/> tipados a partir de valores JSON del agente (RF-QRY-02, RF-DML-08, RS-INJ-01).
/// Los valores nunca se concatenan al SQL; los nombres se validan con una gramática estricta porque el driver
/// los incluye en la declaración de parámetros de sp_executesql. Las conversiones son estrictas e invariantes:
/// fechas ISO 8601, binarios en Base64, decimales exactos; cualquier error devuelve INVALID_ARGUMENT con el nombre
/// del parámetro.
/// </summary>
public sealed partial class ParameterBinder : IParameterBinder
{
    /// <summary>Máximo de parámetros por invocación; lo aplican los llamadores (ver <see cref="QueryParameterBinding"/>).</summary>
    public const int MaxParameters = 100;

    /// <summary>Longitud de nvarchar inferida para cadenas cortas (estabiliza la caché de planes).</summary>
    public const int DefaultNVarCharSize = 4000;

    /// <summary>Longitud de varchar/varbinary inferida para valores cortos.</summary>
    public const int DefaultVarCharSize = 8000;

    private const int MaxShownValueLength = 40;

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] DateTimeOffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mmzzz",
        "yyyy-MM-dd HH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
    ];

    private static readonly string[] TimeFormats = [@"hh\:mm", @"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF"];

    private static readonly DateTime MinDateTime = new(1753, 1, 1);
    private static readonly DateTime MinSmallDateTime = new(1900, 1, 1);
    private static readonly DateTime MaxSmallDateTime = new(2079, 6, 6, 23, 59, 0);

    /// <inheritdoc />
    public SqlParameter Bind(QueryParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var name = NormalizeName(parameter.Name);
        var spec = string.IsNullOrWhiteSpace(parameter.Type) ? null : SqlTypeSpec.Parse(parameter.Type, name);
        return Create(name, spec, parameter.Value);
    }

    /// <inheritdoc />
    public SqlParameter BindForCatalogType(string parameterName, CatalogType type, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = NormalizeCatalogName(parameterName);
        var spec = SqlTypeSpec.FromCatalog(type, name);
        return Create(name, spec, value);
    }

    /// <summary>
    /// Valida y normaliza un nombre de parámetro recibido del agente: <c>^@?[A-Za-z_][A-Za-z0-9_]*$</c>, con "@" y
    /// como máximo 128 caracteres en total (RS-INJ-02).
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (name is null || !AgentParameterName().IsMatch(name))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"El nombre de parámetro '{Shorten(name ?? string.Empty)}' no es válido.",
                "Use nombres como @cliente_id: letra o guion bajo inicial, luego letras, dígitos o guion bajo (máximo 128 caracteres con @).");
        }

        return name[0] == '@' ? name : "@" + name;
    }

    /// <summary>
    /// Declaración T-SQL del tipo de un parámetro creado por este enlazador ("int", "nvarchar(4000)", "decimal(29,9)"…).
    /// Solo usa el nombre (validado) y el tipo, nunca el valor (RS-INJ-01).
    /// </summary>
    public static string GetDeclaration(SqlParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var size = parameter.Size;
        string Len(int defaultSize) => size == -1 ? "max" : (size > 0 ? size : defaultSize).ToString(CultureInfo.InvariantCulture);
        var scale = parameter.Scale.ToString(CultureInfo.InvariantCulture);
        return parameter.SqlDbType switch
        {
            SqlDbType.BigInt => "bigint",
            SqlDbType.Int => "int",
            SqlDbType.SmallInt => "smallint",
            SqlDbType.TinyInt => "tinyint",
            SqlDbType.Bit => "bit",
            SqlDbType.Decimal => $"decimal({(parameter.Precision == 0 ? 38 : parameter.Precision).ToString(CultureInfo.InvariantCulture)},{scale})",
            SqlDbType.Money => "money",
            SqlDbType.SmallMoney => "smallmoney",
            SqlDbType.Float => "float",
            SqlDbType.Real => "real",
            SqlDbType.Date => "date",
            SqlDbType.Time => $"time({scale})",
            SqlDbType.DateTime => "datetime",
            SqlDbType.DateTime2 => $"datetime2({scale})",
            SqlDbType.DateTimeOffset => $"datetimeoffset({scale})",
            SqlDbType.SmallDateTime => "smalldatetime",
            SqlDbType.Char => $"char({Len(1)})",
            SqlDbType.VarChar => $"varchar({Len(DefaultVarCharSize)})",
            SqlDbType.NChar => $"nchar({Len(1)})",
            SqlDbType.NVarChar => $"nvarchar({Len(DefaultNVarCharSize)})",
            SqlDbType.Binary => $"binary({Len(1)})",
            SqlDbType.VarBinary => $"varbinary({Len(DefaultVarCharSize)})",
            SqlDbType.UniqueIdentifier => "uniqueidentifier",
            SqlDbType.Xml => "xml",
            SqlDbType.Variant => "sql_variant",
            _ => throw new InvalidOperationException("Tipo de parámetro no admitido para declaración."),
        };
    }

    /// <summary>Nombres de catálogo (sys.parameters / sys.columns): reglas de identificador de SQL Server, sin delimitadores.</summary>
    private static string NormalizeCatalogName(string? name)
    {
        if (name is null || !CatalogParameterName().IsMatch(name))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"El nombre de parámetro '{Shorten(name ?? string.Empty)}' no es válido.",
                "Los nombres de parámetro deben ser identificadores regulares de SQL Server.");
        }

        return name[0] == '@' ? name : "@" + name;
    }

    private static SqlParameter Create(string name, SqlTypeSpec? spec, JsonElement value)
    {
        var (effective, converted) = spec is null ? Infer(name, value) : Convert(name, spec, value);
        var parameter = new SqlParameter
        {
            ParameterName = name,
            SqlDbType = effective.DbType,
            Direction = ParameterDirection.Input,
            IsNullable = true,
        };

        switch (effective.DbType)
        {
            case SqlDbType.Char:
            case SqlDbType.VarChar:
            case SqlDbType.NChar:
            case SqlDbType.NVarChar:
            case SqlDbType.Binary:
            case SqlDbType.VarBinary:
                parameter.Size = effective.Size;
                break;
            case SqlDbType.Decimal:
                parameter.Precision = effective.Precision;
                parameter.Scale = effective.Scale;
                break;
            case SqlDbType.Time:
            case SqlDbType.DateTime2:
            case SqlDbType.DateTimeOffset:
                parameter.Scale = effective.Scale;
                break;
        }

        parameter.Value = converted;
        return parameter;
    }

    /// <summary>Inferencia sin tipo explícito: cadena → nvarchar(4000|max), entero → bigint, fracción → decimal exacto, booleano → bit.</summary>
    private static (SqlTypeSpec Spec, object Value) Infer(string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return (new SqlTypeSpec("nvarchar", SqlDbType.NVarChar, DefaultNVarCharSize), DBNull.Value);
            case JsonValueKind.True:
            case JsonValueKind.False:
                return (new SqlTypeSpec("bit", SqlDbType.Bit), value.ValueKind == JsonValueKind.True);
            case JsonValueKind.String:
            {
                var text = value.GetString() ?? string.Empty;
                var size = text.Length <= DefaultNVarCharSize ? DefaultNVarCharSize : SqlTypeSpec.Max;
                return (new SqlTypeSpec("nvarchar", SqlDbType.NVarChar, size), text);
            }

            case JsonValueKind.Number:
            {
                var raw = value.GetRawText();
                if (!SqlExactNumber.TryParse(raw, out var number))
                {
                    throw Invalid(name, $"el número '{Shorten(raw)}' no es válido");
                }

                if (number.TryToInt64(out var integer))
                {
                    return (new SqlTypeSpec("bigint", SqlDbType.BigInt), integer);
                }

                if (number.Precision > SqlExactNumber.MaxPrecision || number.Scale > SqlExactNumber.MaxPrecision)
                {
                    throw Invalid(name, "el número excede la precisión de decimal(38)", "Indique el tipo explícito float si acepta una aproximación.");
                }

                var precision = (byte)Math.Max(number.Precision, number.Scale);
                var scale = (byte)number.Scale;
                return (new SqlTypeSpec("decimal", SqlDbType.Decimal, 0, precision, scale), number.ToSqlDecimal(scale));
            }

            default:
                throw Invalid(name, "los valores deben ser escalares (texto, número, booleano o null)");
        }
    }

    private static (SqlTypeSpec Spec, object Value) Convert(string name, SqlTypeSpec spec, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            var nullSpec = spec.SizeFromValue ? spec with { Size = DefaultSizeFor(spec.DbType), SizeFromValue = false } : spec;
            return (nullSpec, DBNull.Value);
        }

        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            throw Invalid(name, "los valores deben ser escalares (texto, número, booleano o null)");
        }

        switch (spec.DbType)
        {
            case SqlDbType.BigInt:
                return (spec, ReadInteger(name, value, long.MinValue, long.MaxValue, "bigint"));
            case SqlDbType.Int:
                return (spec, (int)ReadInteger(name, value, int.MinValue, int.MaxValue, "int"));
            case SqlDbType.SmallInt:
                return (spec, (short)ReadInteger(name, value, short.MinValue, short.MaxValue, "smallint"));
            case SqlDbType.TinyInt:
                return (spec, (byte)ReadInteger(name, value, byte.MinValue, byte.MaxValue, "tinyint"));
            case SqlDbType.Bit:
                return (spec, ReadBoolean(name, value));
            case SqlDbType.Decimal:
            {
                var number = ReadExact(name, value);
                if (!number.FitsDecimal(spec.Precision, spec.Scale))
                {
                    throw Invalid(name, $"el valor no cabe en {spec.Declaration} sin redondeo");
                }

                return (spec, number.ToSqlDecimal(spec.Scale));
            }

            case SqlDbType.Money:
            case SqlDbType.SmallMoney:
                return (spec, ReadMoney(name, value, spec.DbType == SqlDbType.SmallMoney));
            case SqlDbType.Float:
            {
                var d = ReadDouble(name, value);
                return (spec, d);
            }

            case SqlDbType.Real:
            {
                var d = ReadDouble(name, value);
                if (Math.Abs(d) > float.MaxValue)
                {
                    throw Invalid(name, "el valor está fuera del rango de real");
                }

                return (spec, (float)d);
            }

            case SqlDbType.Date:
            {
                var text = RequireString(name, value, "una fecha ISO 8601 (yyyy-MM-dd)");
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    throw Invalid(name, $"'{Shorten(text)}' no es una fecha ISO 8601 (yyyy-MM-dd)");
                }

                return (spec, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));
            }

            case SqlDbType.DateTime:
            case SqlDbType.DateTime2:
            case SqlDbType.SmallDateTime:
            {
                var dt = ReadDateTime(name, value);
                if (spec.DbType == SqlDbType.DateTime && dt < MinDateTime)
                {
                    throw Invalid(name, "datetime admite fechas desde 1753-01-01; use datetime2");
                }

                if (spec.DbType == SqlDbType.SmallDateTime && (dt < MinSmallDateTime || dt > MaxSmallDateTime))
                {
                    throw Invalid(name, "smalldatetime admite fechas entre 1900-01-01 y 2079-06-06 23:59");
                }

                return (spec, dt);
            }

            case SqlDbType.DateTimeOffset:
                return (spec, ReadDateTimeOffset(name, value));
            case SqlDbType.Time:
            {
                var text = RequireString(name, value, "una hora ISO 8601 (HH:mm:ss[.fffffff])");
                if (!TimeSpan.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, TimeSpanStyles.None, out var time)
                    || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                {
                    throw Invalid(name, $"'{Shorten(text)}' no es una hora válida (HH:mm:ss[.fffffff])");
                }

                return (spec, time);
            }

            case SqlDbType.UniqueIdentifier:
            {
                var text = RequireString(name, value, "un uniqueidentifier");
                if (!Guid.TryParse(text, out var guid))
                {
                    throw Invalid(name, $"'{Shorten(text)}' no es un uniqueidentifier válido");
                }

                return (spec, guid);
            }

            case SqlDbType.Char:
            case SqlDbType.VarChar:
            case SqlDbType.NChar:
            case SqlDbType.NVarChar:
            {
                var text = ReadText(name, value);
                var effective = spec;
                if (spec.SizeFromValue)
                {
                    var defaultSize = DefaultSizeFor(spec.DbType);
                    effective = spec with { Size = text.Length <= defaultSize ? defaultSize : SqlTypeSpec.Max, SizeFromValue = false };
                }
                else if (spec.Size != SqlTypeSpec.Max && text.Length > spec.Size)
                {
                    throw Invalid(name, $"el texto tiene {text.Length.ToString(CultureInfo.InvariantCulture)} caracteres y {spec.Declaration} admite {spec.Size.ToString(CultureInfo.InvariantCulture)}");
                }

                return (effective, text);
            }

            case SqlDbType.Binary:
            case SqlDbType.VarBinary:
            {
                var text = RequireString(name, value, "un binario en Base64");
                byte[] bytes;
                try
                {
                    bytes = System.Convert.FromBase64String(text);
                }
                catch (FormatException)
                {
                    throw Invalid(name, "el valor binario debe estar codificado en Base64");
                }

                var effective = spec;
                if (spec.SizeFromValue)
                {
                    effective = spec with { Size = bytes.Length <= DefaultVarCharSize ? DefaultVarCharSize : SqlTypeSpec.Max, SizeFromValue = false };
                }
                else if (spec.Size != SqlTypeSpec.Max && bytes.Length > spec.Size)
                {
                    throw Invalid(name, $"el binario tiene {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes y {spec.Declaration} admite {spec.Size.ToString(CultureInfo.InvariantCulture)}");
                }

                return (effective, bytes);
            }

            case SqlDbType.Xml:
                return (spec, RequireString(name, value, "un documento XML como texto"));
            case SqlDbType.Variant:
                return (spec, Infer(name, value).Value);
            default:
                throw Invalid(name, $"el tipo {spec.TypeName} no se admite");
        }
    }

    private static int DefaultSizeFor(SqlDbType type) =>
        type is SqlDbType.NVarChar or SqlDbType.NChar ? DefaultNVarCharSize : DefaultVarCharSize;

    private static long ReadInteger(string name, JsonElement value, long min, long max, string typeName)
    {
        SqlExactNumber number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!SqlExactNumber.TryParse(value.GetRawText(), out number))
            {
                throw Invalid(name, $"no es un entero válido para {typeName}");
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (!SqlExactNumber.TryParse(text, out number))
            {
                throw Invalid(name, $"'{Shorten(text)}' no es un entero válido para {typeName}");
            }
        }
        else
        {
            throw Invalid(name, $"se esperaba un entero para {typeName}");
        }

        if (!number.TryToInt64(out var result) || result < min || result > max)
        {
            throw Invalid(name, $"el valor {Shorten(number.ToString())} no es un entero en el rango de {typeName} ({min.ToString(CultureInfo.InvariantCulture)} a {max.ToString(CultureInfo.InvariantCulture)})");
        }

        return result;
    }

    private static bool ReadBoolean(string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (SqlExactNumber.TryParse(value.GetRawText(), out var n) && n.TryToInt64(out var i) && i is 0 or 1)
                {
                    return i == 1;
                }

                break;
            case JsonValueKind.String:
                switch ((value.GetString() ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "true":
                    case "1":
                        return true;
                    case "false":
                    case "0":
                        return false;
                }

                break;
        }

        throw Invalid(name, "se esperaba un valor bit (true, false, 1 o 0)");
    }

    private static SqlExactNumber ReadExact(string name, JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw Invalid(name, "se esperaba un número"),
        };

        if (!SqlExactNumber.TryParse(text, out var number))
        {
            throw Invalid(name, $"'{Shorten(text)}' no es un número válido (use punto decimal, sin separadores de miles)");
        }

        return number;
    }

    private static decimal ReadMoney(string name, JsonElement value, bool small)
    {
        var number = ReadExact(name, value);
        if (number.Scale > 4 || number.IntegerDigits.Length > 15 || !number.TryToDecimal(out var amount))
        {
            throw Invalid(name, small ? "el valor no cabe en smallmoney (4 decimales)" : "el valor no cabe en money (4 decimales)");
        }

        var (min, max) = small ? (-214_748.3648m, 214_748.3647m) : (-922_337_203_685_477.5808m, 922_337_203_685_477.5807m);
        if (amount < min || amount > max)
        {
            throw Invalid(name, small ? "el valor está fuera del rango de smallmoney" : "el valor está fuera del rango de money");
        }

        return amount;
    }

    private static double ReadDouble(string name, JsonElement value)
    {
        double result;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out result))
            {
                throw Invalid(name, "el número está fuera del rango de float");
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (!SqlExactNumber.TryParse(text, out _)
                || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                throw Invalid(name, $"'{Shorten(text)}' no es un número válido");
            }
        }
        else
        {
            throw Invalid(name, "se esperaba un número");
        }

        if (!double.IsFinite(result))
        {
            throw Invalid(name, "SQL Server no admite NaN ni infinito");
        }

        return result;
    }

    private static DateTime ReadDateTime(string name, JsonElement value)
    {
        var text = RequireString(name, value, "una fecha y hora ISO 8601 (yyyy-MM-ddTHH:mm:ss[.fffffff])");
        if (DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            return DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
        }

        if (DateTimeOffset.TryParseExact(NormalizeZulu(text), DateTimeOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw Invalid(name, "el valor incluye zona horaria; use el tipo datetimeoffset o quite el desplazamiento");
        }

        throw Invalid(name, $"'{Shorten(text)}' no es una fecha y hora ISO 8601 (yyyy-MM-ddTHH:mm:ss[.fffffff])");
    }

    private static DateTimeOffset ReadDateTimeOffset(string name, JsonElement value)
    {
        var text = RequireString(name, value, "una fecha y hora ISO 8601 con desplazamiento");
        if (DateTimeOffset.TryParseExact(NormalizeZulu(text), DateTimeOffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            return result;
        }

        throw Invalid(name, $"'{Shorten(text)}' no es una fecha ISO 8601 con desplazamiento (yyyy-MM-ddTHH:mm:ss[.fffffff]+hh:mm o Z)");
    }

    /// <summary>"…Z" equivale a "…+00:00".</summary>
    private static string NormalizeZulu(string text) =>
        text.Length > 0 && (text[^1] == 'Z' || text[^1] == 'z') ? text[..^1] + "+00:00" : text;

    private static string ReadText(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        _ => throw Invalid(name, "se esperaba un texto"),
    };

    private static string RequireString(string name, JsonElement value, string expected) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : throw Invalid(name, $"se esperaba {expected} como texto");

    private static ToolFailureException Invalid(string name, string reason, string? suggestion = null) =>
        new(ErrorCodes.InvalidArgument, $"Valor inválido para el parámetro '{name}': {reason}.", suggestion ?? "Revise el tipo declarado y el formato del valor (fechas ISO 8601, binarios en Base64, punto decimal).");

    private static string Shorten(string text) =>
        text.Length <= MaxShownValueLength ? text : text[..MaxShownValueLength] + "…";

    [GeneratedRegex(@"^@?[A-Za-z_][A-Za-z0-9_]{0,126}$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentParameterName();

    [GeneratedRegex(@"^@?[\p{L}_#][\p{L}\p{Nd}_@#$]{0,126}$", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogParameterName();
}
