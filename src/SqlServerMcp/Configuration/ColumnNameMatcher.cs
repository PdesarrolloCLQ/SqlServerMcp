using System.Globalization;
using System.Text;

namespace SqlServerMcp.Configuration;

/// <summary>
/// Comparación de nombres de columna con palabras clave por <b>segmentos de palabra</b> (RS-DAT-02):
/// "CustomerSSN", "api_key", "Api-Key" y "APIKey" se dividen en segmentos ("customer"+"ssn", "api"+"key"…) y una
/// palabra clave coincide si es igual a uno o varios segmentos consecutivos. Así "pin" coincide con "PinCode" pero no con
/// "Shipping", y "token" coincide con "RefreshToken" pero no con "TokenizerName". Sin distinguir mayúsculas, separadores
/// (_ - espacio .) ni diacríticos ("Teléfono" = "telefono").
/// </summary>
public static class ColumnNameMatcher
{
    /// <summary>Máximo de segmentos consecutivos que se concatenan para comparar con una palabra clave.</summary>
    private const int MaxJoinedSegments = 5;

    /// <summary>
    /// True si el patrón es una palabra clave (sin comodines ni partes calificadas). Los patrones con "*" o "?" conservan
    /// la comparación por comodines sobre el nombre completo.
    /// </summary>
    public static bool IsKeyword(string pattern) => pattern.IndexOfAny(['*', '?', '.', '[', ']']) < 0;

    /// <summary>Palabra clave normalizada: minúsculas, sin diacríticos ni separadores.</summary>
    public static string NormalizeKeyword(string keyword)
    {
        var sb = new StringBuilder(keyword.Length);
        foreach (var c in Fold(keyword))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>Nombre sin separadores ni diacríticos (para comparar comodines como "*password*" con "pass_word").</summary>
    public static string Compact(string name) => NormalizeKeyword(name);

    /// <summary>Segmentos en minúsculas: separadores, cambios minúscula→mayúscula, fin de siglas y letra↔dígito.</summary>
    public static IReadOnlyList<string> Segments(string name)
    {
        var folded = Fold(name);
        var segments = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < folded.Length; i++)
        {
            var c = folded[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var prev = folded[i - 1];
                var next = i + 1 < folded.Length ? folded[i + 1] : '\0';
                var boundary =
                    (char.IsLower(prev) && char.IsUpper(c))
                    || (char.IsUpper(prev) && char.IsUpper(c) && char.IsLower(next))
                    || (char.IsDigit(prev) != char.IsDigit(c));
                if (boundary)
                {
                    Flush();
                }
            }

            current.Append(char.ToLowerInvariant(c));
        }

        Flush();
        return segments;

        void Flush()
        {
            if (current.Length > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>True si la palabra clave es igual a uno o varios segmentos consecutivos del nombre.</summary>
    public static bool MatchesKeyword(string keyword, string columnName)
    {
        var target = NormalizeKeyword(keyword);
        if (target.Length == 0)
        {
            return false;
        }

        var segments = Segments(columnName);
        for (var i = 0; i < segments.Count; i++)
        {
            var joined = string.Empty;
            for (var j = i; j < segments.Count && j < i + MaxJoinedSegments; j++)
            {
                joined += segments[j];
                if (joined.Length > target.Length)
                {
                    break;
                }

                if (string.Equals(joined, target, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Sin diacríticos (ñ → n, é → e), conservando mayúsculas para detectar los segmentos.</summary>
    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
