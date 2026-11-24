using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Tipo SQL validado de un parámetro (RF-QRY-02, RF-DML-08). Se obtiene de una gramática estricta
/// (<see cref="Parse"/>) o del catálogo (<see cref="FromCatalog"/>); nunca del texto libre sin validar.
/// </summary>
/// <param name="TypeName">Nombre del tipo en minúsculas ("nvarchar", "decimal"…).</param>
/// <param name="DbType">Tipo del driver.</param>
/// <param name="Size">Longitud (caracteres o bytes); -1 = max; 0 = no aplica.</param>
/// <param name="Precision">Precisión de decimal/numeric.</param>
/// <param name="Scale">Escala de decimal/numeric o fracción de segundo de time/datetime2/datetimeoffset.</param>
/// <param name="SizeFromValue">True si la longitud no se indicó y se elige según el valor (4000/8000 o max).</param>
public sealed partial record SqlTypeSpec(string TypeName, SqlDbType DbType, int Size = 0, byte Precision = 0, byte Scale = 0, bool SizeFromValue = false)
{
    /// <summary>Valor de <see cref="Size"/> que representa "max".</summary>
    public const int Max = -1;

    /// <summary>Lista de tipos admitidos, para mensajes de error accionables.</summary>
    public const string SupportedTypes =
        "bigint, int, smallint, tinyint, bit, decimal(p,s), numeric(p,s), money, smallmoney, float, real, date, time(n), " +
        "datetime, datetime2(n), datetimeoffset(n), smalldatetime, char(n), varchar(n|max), nchar(n), nvarchar(n|max), " +
        "binary(n), varbinary(n|max), uniqueidentifier, xml";

    private const int MaxTypeTextLength = 64;

    /// <summary>Declaración T-SQL del tipo ("nvarchar(50)", "decimal(18,2)", "varbinary(max)"…).</summary>
    public string Declaration => DbType switch
    {
        SqlDbType.Decimal => $"{TypeName}({Precision.ToString(CultureInfo.InvariantCulture)},{Scale.ToString(CultureInfo.InvariantCulture)})",
        SqlDbType.Time or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset => $"{TypeName}({Scale.ToString(CultureInfo.InvariantCulture)})",
        SqlDbType.Char or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.NVarChar or SqlDbType.Binary or SqlDbType.VarBinary =>
            $"{TypeName}({(Size == Max ? "max" : Size.ToString(CultureInfo.InvariantCulture))})",
        _ => TypeName,
    };

    /// <summary>
    /// Interpreta un tipo indicado por el agente con una gramática estricta. Cualquier otro texto se rechaza con
    /// <see cref="ErrorCodes.InvalidArgument"/>.
    /// </summary>
    public static SqlTypeSpec Parse(string text, string parameterName)
    {
        if (TryParse(text, out var spec, out var error))
        {
            return spec!;
        }

        var shown = text is null ? string.Empty : (text.Length > MaxTypeTextLength ? text[..MaxTypeTextLength] + "…" : text);
        throw new ToolFailureException(
            ErrorCodes.InvalidArgument,
            $"El tipo '{shown}' del parámetro '{parameterName}' no es válido: {error}.",
            "Tipos admitidos: " + SupportedTypes + ".");
    }

    /// <summary>Versión sin excepciones de <see cref="Parse"/>.</summary>
    public static bool TryParse(string? text, out SqlTypeSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTypeTextLength)
        {
            error = "el texto del tipo está vacío o es demasiado largo";
            return false;
        }

        var match = TypeGrammar().Match(text);
        if (!match.Success)
        {
            error = "no cumple la sintaxis nombre, nombre(n), nombre(max) o nombre(p,s)";
            return false;
        }

        var name = match.Groups["name"].Value.ToLowerInvariant();
        var hasArgs = match.Groups["a"].Success;
        var argA = match.Groups["a"].Value;
        var isMax = hasArgs && argA.Equals("max", StringComparison.OrdinalIgnoreCase);
        var hasSecond = match.Groups["b"].Success;
        int? numA = hasArgs && !isMax ? int.Parse(argA, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        int? numB = hasSecond ? int.Parse(match.Groups["b"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : null;

        switch (name)
        {
            case "bigint":
            case "int":
            case "smallint":
            case "tinyint":
            case "bit":
            case "money":
            case "smallmoney":
            case "real":
            case "date":
            case "datetime":
            case "smalldatetime":
            case "uniqueidentifier":
            case "xml":
                if (hasArgs)
                {
                    error = $"el tipo {name} no admite argumentos";
                    return false;
                }

                spec = Simple(name);
                return true;

            case "float":
                if (isMax || hasSecond)
                {
                    error = "float solo admite float o float(n) con n entre 1 y 53";
                    return false;
                }

                if (numA is null)
                {
                    spec = Simple("float");
                    return true;
                }

                if (numA < 1 || numA > 53)
                {
                    error = "float(n) requiere n entre 1 y 53";
                    return false;
                }

                spec = numA <= 24 ? Simple("real") : Simple("float");
                return true;

            case "decimal":
            case "numeric":
            {
                if (isMax)
                {
                    error = $"{name} no admite max";
                    return false;
                }

                var precision = numA ?? 18;
                var scale = numB ?? 0;
                if (precision < 1 || precision > 38 || scale < 0 || scale > precision)
                {
                    error = $"{name}(p,s) requiere 1 <= p <= 38 y 0 <= s <= p";
                    return false;
                }

                spec = new SqlTypeSpec(name, SqlDbType.Decimal, 0, (byte)precision, (byte)scale);
                return true;
            }

            case "time":
            case "datetime2":
            case "datetimeoffset":
            {
                if (isMax || hasSecond)
                {
                    error = $"{name} solo admite {name} o {name}(n) con n entre 0 y 7";
                    return false;
                }

                var fraction = numA ?? 7;
                if (fraction > 7)
                {
                    error = $"{name}(n) requiere n entre 0 y 7";
                    return false;
                }

                var dbType = name switch
                {
                    "time" => SqlDbType.Time,
                    "datetime2" => SqlDbType.DateTime2,
                    _ => SqlDbType.DateTimeOffset,
                };
                spec = new SqlTypeSpec(name, dbType, 0, 0, (byte)fraction);
                return true;
            }

            case "char":
            case "nchar":
            case "binary":
            {
                if (isMax || hasSecond)
                {
                    error = $"{name} solo admite {name}(n)";
                    return false;
                }

                var limit = name == "nchar" ? 4000 : 8000;
                var size = numA ?? 1;
                if (size < 1 || size > limit)
                {
                    error = $"{name}(n) requiere n entre 1 y {limit.ToString(CultureInfo.InvariantCulture)}";
                    return false;
                }

                var dbType = name switch
                {
                    "char" => SqlDbType.Char,
                    "nchar" => SqlDbType.NChar,
                    _ => SqlDbType.Binary,
                };
                spec = new SqlTypeSpec(name, dbType, size);
                return true;
            }

            case "varchar":
            case "nvarchar":
            case "varbinary":
            {
                if (hasSecond)
                {
                    error = $"{name} solo admite {name}(n) o {name}(max)";
                    return false;
                }

                var dbType = name switch
                {
                    "varchar" => SqlDbType.VarChar,
                    "nvarchar" => SqlDbType.NVarChar,
                    _ => SqlDbType.VarBinary,
                };
                if (!hasArgs)
                {
                    spec = new SqlTypeSpec(name, dbType, 0, SizeFromValue: true);
                    return true;
                }

                if (isMax)
                {
                    spec = new SqlTypeSpec(name, dbType, Max);
                    return true;
                }

                var limit = name == "nvarchar" ? 4000 : 8000;
                if (numA < 1 || numA > limit)
                {
                    error = $"{name}(n) requiere n entre 1 y {limit.ToString(CultureInfo.InvariantCulture)} o max";
                    return false;
                }

                spec = new SqlTypeSpec(name, dbType, numA!.Value);
                return true;
            }

            default:
                error = "tipo no admitido";
                return false;
        }
    }

    /// <summary>
    /// Tipo de un parámetro a partir del catálogo (columna o parámetro de procedimiento). Los tipos alias usan
    /// <see cref="CatalogType.BaseTypeName"/>; <see cref="CatalogType.MaxLength"/> está en bytes.
    /// </summary>
    public static SqlTypeSpec FromCatalog(CatalogType type, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = (type.BaseTypeName ?? type.TypeName).Trim().ToLowerInvariant();
        switch (name)
        {
            case "bigint":
            case "int":
            case "smallint":
            case "tinyint":
            case "bit":
            case "money":
            case "smallmoney":
            case "real":
            case "date":
            case "datetime":
            case "smalldatetime":
            case "uniqueidentifier":
            case "xml":
                return Simple(name);
            case "float":
                return type.MaxLength == 4 ? Simple("real") : Simple("float");
            case "decimal":
            case "numeric":
            {
                var precision = type.Precision is >= 1 and <= 38 ? type.Precision : (byte)18;
                var scale = type.Scale <= precision ? type.Scale : (byte)0;
                return new SqlTypeSpec(name, SqlDbType.Decimal, 0, precision, scale);
            }

            case "time":
                return new SqlTypeSpec(name, SqlDbType.Time, 0, 0, Math.Min(type.Scale, (byte)7));
            case "datetime2":
                return new SqlTypeSpec(name, SqlDbType.DateTime2, 0, 0, Math.Min(type.Scale, (byte)7));
            case "datetimeoffset":
                return new SqlTypeSpec(name, SqlDbType.DateTimeOffset, 0, 0, Math.Min(type.Scale, (byte)7));
            case "char":
                return new SqlTypeSpec(name, SqlDbType.Char, Math.Max(1, (int)type.MaxLength));
            case "binary":
                return new SqlTypeSpec(name, SqlDbType.Binary, Math.Max(1, (int)type.MaxLength));
            case "varchar":
                return new SqlTypeSpec(name, SqlDbType.VarChar, type.MaxLength == -1 ? Max : Math.Max(1, (int)type.MaxLength));
            case "varbinary":
                return new SqlTypeSpec(name, SqlDbType.VarBinary, type.MaxLength == -1 ? Max : Math.Max(1, (int)type.MaxLength));
            case "nchar":
                return new SqlTypeSpec(name, SqlDbType.NChar, Math.Max(1, type.MaxLength / 2));
            case "nvarchar":
                return new SqlTypeSpec(name, SqlDbType.NVarChar, type.MaxLength == -1 ? Max : Math.Max(1, type.MaxLength / 2));
            case "sysname":
                return new SqlTypeSpec("nvarchar", SqlDbType.NVarChar, 128);
            case "text":
                return new SqlTypeSpec("varchar", SqlDbType.VarChar, Max);
            case "ntext":
            case "json":
                return new SqlTypeSpec("nvarchar", SqlDbType.NVarChar, Max);
            case "image":
                return new SqlTypeSpec("varbinary", SqlDbType.VarBinary, Max);
            case "timestamp":
            case "rowversion":
                return new SqlTypeSpec("binary", SqlDbType.Binary, 8);
            case "sql_variant":
                return new SqlTypeSpec("sql_variant", SqlDbType.Variant);
            default:
                throw new ToolFailureException(
                    ErrorCodes.InvalidArgument,
                    $"El tipo '{(type.TypeName.Length > MaxTypeTextLength ? type.TypeName[..MaxTypeTextLength] : type.TypeName)}' del parámetro '{parameterName}' no se admite como parámetro.",
                    "Solo se admiten tipos escalares del sistema (no CLR, no tablas).");
        }
    }

    private static SqlTypeSpec Simple(string name) => name switch
    {
        "bigint" => new SqlTypeSpec(name, SqlDbType.BigInt),
        "int" => new SqlTypeSpec(name, SqlDbType.Int),
        "smallint" => new SqlTypeSpec(name, SqlDbType.SmallInt),
        "tinyint" => new SqlTypeSpec(name, SqlDbType.TinyInt),
        "bit" => new SqlTypeSpec(name, SqlDbType.Bit),
        "money" => new SqlTypeSpec(name, SqlDbType.Money),
        "smallmoney" => new SqlTypeSpec(name, SqlDbType.SmallMoney),
        "real" => new SqlTypeSpec(name, SqlDbType.Real),
        "float" => new SqlTypeSpec(name, SqlDbType.Float),
        "date" => new SqlTypeSpec(name, SqlDbType.Date),
        "datetime" => new SqlTypeSpec(name, SqlDbType.DateTime),
        "smalldatetime" => new SqlTypeSpec(name, SqlDbType.SmallDateTime),
        "uniqueidentifier" => new SqlTypeSpec(name, SqlDbType.UniqueIdentifier),
        "xml" => new SqlTypeSpec(name, SqlDbType.Xml),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Tipo simple desconocido."),
    };

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z][A-Za-z0-9]*)\s*(?:\(\s*(?<a>[0-9]{1,5}|[Mm][Aa][Xx])\s*(?:,\s*(?<b>[0-9]{1,2})\s*)?\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeGrammar();
}
