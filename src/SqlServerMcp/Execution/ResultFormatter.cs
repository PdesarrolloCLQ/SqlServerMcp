using System.Globalization;
using System.Text;

namespace SqlServerMcp.Execution;

/// <summary>
/// Formatos de texto alternativos para un <see cref="TabularResult"/> (RF-QRY-11): tabla Markdown y CSV (RFC 4180).
/// El contenido estructurado JSON lo genera la capa de herramientas; aquí solo se representa el texto.
/// Los valores ya vienen serializados de forma estable por <see cref="ResultReader"/>.
/// </summary>
public static class ResultFormatter
{
    /// <summary>Representación de NULL en Markdown (en CSV, NULL es un campo vacío).</summary>
    public const string MarkdownNull = "NULL";

    /// <summary>
    /// Tabla Markdown (GFM). Escapa "|" y "\" y convierte los saltos de línea en &lt;br&gt; para no romper la tabla.
    /// </summary>
    public static string ToMarkdown(TabularResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Columns.Count == 0)
        {
            return "(sin columnas)";
        }

        var builder = new StringBuilder();
        builder.Append('|');
        foreach (var column in result.Columns)
        {
            builder.Append(' ').Append(EscapeMarkdown(column.Name.Length == 0 ? " " : column.Name)).Append(" |");
        }

        builder.Append('\n').Append('|');
        for (var i = 0; i < result.Columns.Count; i++)
        {
            builder.Append(" --- |");
        }

        foreach (var row in result.Rows)
        {
            builder.Append('\n').Append('|');
            for (var i = 0; i < result.Columns.Count; i++)
            {
                var value = i < row.Length ? row[i] : null;
                builder.Append(' ').Append(value is null ? MarkdownNull : EscapeMarkdown(FormatValue(value))).Append(" |");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// CSV según RFC 4180: separador coma, fin de línea CRLF, encabezado con los nombres de columna, campos entre
    /// comillas dobles cuando contienen coma, comillas, CR o LF (comillas internas duplicadas). NULL = campo vacío.
    /// </summary>
    public static string ToCsv(TabularResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendCsvField(builder, result.Columns[i].Name);
        }

        foreach (var row in result.Rows)
        {
            builder.Append("\r\n");
            for (var i = 0; i < result.Columns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var value = i < row.Length ? row[i] : null;
                if (value is not null)
                {
                    AppendCsvField(builder, FormatValue(value));
                }
            }
        }

        if (result.Columns.Count > 0)
        {
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>Texto invariante de un valor serializado (bool en minúsculas, double con ida y vuelta exacta).</summary>
    public static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string EscapeMarkdown(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '|':
                    builder.Append(@"\|");
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    builder.Append("<br>");
                    break;
                case '\n':
                    builder.Append("<br>");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AppendCsvField(StringBuilder builder, string text)
    {
        if (text.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            builder.Append(text);
            return;
        }

        builder.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}
