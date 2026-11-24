using System.Data.SqlTypes;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlServerMcp.Execution;

/// <summary>
/// Número decimal exacto obtenido del texto JSON sin pasar por <see cref="double"/> (RF-QRY-05: sin pérdida de
/// precisión). Admite la sintaxis numérica de JSON con signo "+" opcional y exponente.
/// </summary>
/// <param name="Negative">Signo negativo (nunca true para el cero).</param>
/// <param name="IntegerDigits">Dígitos enteros sin ceros a la izquierda ("" = 0).</param>
/// <param name="FractionDigits">Dígitos fraccionarios sin ceros a la derecha.</param>
internal readonly partial record struct SqlExactNumber(bool Negative, string IntegerDigits, string FractionDigits)
{
    /// <summary>Precisión máxima de DECIMAL en SQL Server.</summary>
    public const int MaxPrecision = 38;

    private const int MaxDigits = 400;

    public bool IsZero => IntegerDigits.Length == 0 && FractionDigits.Length == 0;

    public bool IsInteger => FractionDigits.Length == 0;

    /// <summary>Escala mínima que representa el valor exactamente.</summary>
    public int Scale => FractionDigits.Length;

    /// <summary>Precisión mínima (al menos 1) que representa el valor exactamente.</summary>
    public int Precision => Math.Max(1, IntegerDigits.Length + FractionDigits.Length);

    public static bool TryParse(string? text, out SqlExactNumber number)
    {
        number = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxDigits)
        {
            return false;
        }

        var match = NumberGrammar().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var negative = match.Groups["sign"].Value == "-";
        var intPart = match.Groups["int"].Value;
        var fracPart = match.Groups["frac"].Value;
        var exponent = 0;
        if (match.Groups["exp"].Success
            && !int.TryParse(match.Groups["exp"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            return false;
        }

        var digits = intPart + fracPart;
        var pointPosition = (long)intPart.Length + exponent;
        if (pointPosition > MaxDigits || pointPosition < -MaxDigits)
        {
            // Fuera de cualquier rango representable (o solo ceros, que se tratan igual: se rechaza el exponente absurdo).
            if (digits.TrimStart('0').Length == 0)
            {
                number = new SqlExactNumber(false, string.Empty, string.Empty);
                return true;
            }

            return false;
        }

        string integerDigits;
        string fractionDigits;
        if (pointPosition <= 0)
        {
            integerDigits = string.Empty;
            fractionDigits = new string('0', (int)-pointPosition) + digits;
        }
        else if (pointPosition >= digits.Length)
        {
            integerDigits = digits + new string('0', (int)pointPosition - digits.Length);
            fractionDigits = string.Empty;
        }
        else
        {
            integerDigits = digits[..(int)pointPosition];
            fractionDigits = digits[(int)pointPosition..];
        }

        integerDigits = integerDigits.TrimStart('0');
        fractionDigits = fractionDigits.TrimEnd('0');
        var isZero = integerDigits.Length == 0 && fractionDigits.Length == 0;
        number = new SqlExactNumber(negative && !isZero, integerDigits, fractionDigits);
        return true;
    }

    /// <summary>True si cabe en decimal(precision, scale) sin redondeo.</summary>
    public bool FitsDecimal(int precision, int scale) =>
        FractionDigits.Length <= scale && IntegerDigits.Length <= precision - scale;

    /// <summary>Convierte a <see cref="SqlDecimal"/> con exactamente <paramref name="scale"/> decimales.</summary>
    public SqlDecimal ToSqlDecimal(int scale) => SqlDecimal.Parse(ToInvariantString(scale));

    /// <summary>Convierte a <see cref="decimal"/> (solo si cabe en 28 dígitos).</summary>
    public bool TryToDecimal(out decimal value) =>
        decimal.TryParse(ToInvariantString(FractionDigits.Length), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    /// <summary>Convierte a <see cref="long"/> si es entero y está en rango.</summary>
    public bool TryToInt64(out long value)
    {
        value = 0;
        return IsInteger
            && IntegerDigits.Length <= 19
            && long.TryParse(ToInvariantString(0), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Texto invariante con exactamente <paramref name="scale"/> decimales (sin redondeo; requiere scale ≥ <see cref="Scale"/>).</summary>
    public string ToInvariantString(int scale)
    {
        var integer = IntegerDigits.Length == 0 ? "0" : IntegerDigits;
        var sign = Negative ? "-" : string.Empty;
        return scale <= 0 ? sign + integer : sign + integer + "." + FractionDigits.PadRight(scale, '0');
    }

    public override string ToString() => ToInvariantString(Scale);

    [GeneratedRegex(@"^(?<sign>[+-])?(?<int>[0-9]+)(?:\.(?<frac>[0-9]+))?(?:[eE](?<exp>[+-]?[0-9]{1,5}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberGrammar();
}
