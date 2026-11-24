using System.Text;
using System.Text.RegularExpressions;

namespace SqlServerMcp.Infrastructure;

/// <summary>Nombre de objeto de hasta cuatro partes, ya normalizado (sin delimitadores).</summary>
public sealed record SqlObjectName(string? Server, string? Database, string? Schema, string Name)
{
    public bool IsTemp => Name.StartsWith('#');

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Server is not null) sb.Append(Server).Append('.');
        if (Database is not null || Server is not null) sb.Append(Database).Append('.');
        if (Schema is not null || Database is not null || Server is not null) sb.Append(Schema).Append('.');
        sb.Append(Name);
        return sb.ToString();
    }

    /// <summary>"esquema.nombre" con el esquema por defecto si no se indicó.</summary>
    public string SchemaQualified(string defaultSchema = "dbo") => $"{Schema ?? defaultSchema}.{Name}";
}

/// <summary>Utilidades para identificadores de SQL Server (RS-INJ-01, RS-INJ-02, RS-POL-06).</summary>
public static partial class SqlNames
{
    public const int MaxIdentifierLength = 128;

    /// <summary>
    /// Normaliza un identificador: quita corchetes o comillas dobles delimitadores y resuelve el escape ("]]" → "]").
    /// No cambia mayúsculas; las comparaciones se hacen con <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    public static string Normalize(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var s = identifier.Trim();
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
        {
            return s[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }

        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            return s[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return s;
    }

    /// <summary>Equivalente a QUOTENAME(): delimita con corchetes y escapa "]".</summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (identifier.Length > MaxIdentifierLength)
        {
            throw new ArgumentException("Identificador demasiado largo.", nameof(identifier));
        }

        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>Valida longitud y caracteres de un identificador recibido del agente (RS-INJ-02).</summary>
    public static bool IsValidIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxIdentifierLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Separa un nombre de varias partes ("db.esquema.objeto", "[a.b].[c]") respetando delimitadores.
    /// Devuelve null si el formato es inválido o alguna parte no es un identificador válido.
    /// </summary>
    public static IReadOnlyList<string>? SplitMultipart(string? name, int maxParts = 4)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 4 * (MaxIdentifierLength + 3))
        {
            return null;
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        var s = name.Trim();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '[')
            {
                var end = i + 1;
                var sb = new StringBuilder();
                while (end < s.Length)
                {
                    if (s[end] == ']')
                    {
                        if (end + 1 < s.Length && s[end + 1] == ']')
                        {
                            sb.Append(']');
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    sb.Append(s[end]);
                    end++;
                }

                if (end >= s.Length)
                {
                    return null;
                }

                current.Append(sb);
                i = end + 1;
            }
            else if (c == '"')
            {
                var end = s.IndexOf('"', i + 1);
                if (end < 0)
                {
                    return null;
                }

                current.Append(s, i + 1, end - i - 1);
                i = end + 1;
            }
            else if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        parts.Add(current.ToString());
        if (parts.Count > maxParts)
        {
            return null;
        }

        for (var p = 0; p < parts.Count; p++)
        {
            parts[p] = parts[p].Trim();
            if (!IsValidIdentifier(parts[p]))
            {
                return null;
            }
        }

        return parts;
    }

    /// <summary>
    /// Interpreta "objeto" o "esquema.objeto" (máximo dos partes) recibido de una herramienta estructurada.
    /// </summary>
    public static bool TryParseSchemaObject(string? name, out string? schema, out string objectName)
    {
        schema = null;
        objectName = string.Empty;
        var parts = SplitMultipart(name, maxParts: 2);
        if (parts is null)
        {
            return false;
        }

        if (parts.Count == 2)
        {
            schema = parts[0];
            objectName = parts[1];
        }
        else
        {
            objectName = parts[0];
        }

        return true;
    }

    /// <summary>Coincidencia con comodines "*" y "?" sin distinguir mayúsculas.</summary>
    public static bool WildcardMatch(string pattern, string? value)
    {
        if (value is null)
        {
            return false;
        }

        if (pattern == "*")
        {
            return true;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static bool EqualsIgnoreCase(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
