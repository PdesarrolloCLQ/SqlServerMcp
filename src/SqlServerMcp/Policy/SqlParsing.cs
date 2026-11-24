using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Policy;

/// <summary>
/// Análisis sintáctico con ScriptDom (RS-POL-01): TSql170Parser con QUOTED_IDENTIFIER activado, verificación
/// léxica previa de anidamiento, ejecución en un hilo con pila grande (protección contra desbordamiento de pila)
/// y normalización de la sentencia para auditoría y confirmación (RS-HIL-03, RS-AUD-01).
/// </summary>
internal static class SqlParsing
{
    /// <summary>Profundidad máxima de paréntesis permitida antes de invocar al parser (protección DoS).</summary>
    public const int MaxParenthesisDepth = 128;

    /// <summary>
    /// Profundidad máxima de cadenas de operadores (+, AND, OR, NOT, UNION, JOIN) que recorre el motor. Protege al
    /// visitante y al generador de ScriptDom (recursivos y de costo creciente con la profundidad) frente a entradas
    /// patológicas; ninguna sentencia legítima se acerca a este valor.
    /// </summary>
    public const int MaxTreeDepth = 500;

    /// <summary>Profundidad máxima de sentencias anidadas (IF dentro de IF, BEGIN…END, SQL dinámico).</summary>
    public const int MaxStatementNesting = 128;

    /// <summary>Longitud máxima del texto normalizado cuando no se puede generar desde el AST.</summary>
    public const int MaxRawNormalizedChars = 65_536;

    /// <summary>Pila del hilo de análisis: holgada para el tamaño máximo absoluto de sentencia.</summary>
    private const int AnalysisStackBytes = 256 * 1024 * 1024;

    /// <summary>Crea un parser nuevo por llamada (no se comparte estado entre hilos).</summary>
    public static TSql170Parser CreateParser() => new(initialQuotedIdentifiers: true);

    /// <summary>Profundidad máxima de paréntesis de una lista de tokens (recorrido lineal, sin recursión).</summary>
    public static int MaxParenthesisNesting(IEnumerable<TSqlParserToken> tokens)
    {
        var depth = 0;
        var max = 0;
        foreach (var token in tokens)
        {
            if (token.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
                if (depth > max)
                {
                    max = depth;
                }
            }
            else if (token.TokenType == TSqlTokenType.RightParenthesis && depth > 0)
            {
                depth--;
            }
        }

        return max;
    }

    /// <summary>
    /// Analiza el texto completo (RS-POL-01). Primero ejecuta solo el lexer (sin recursión) y rechaza el anidamiento
    /// excesivo de paréntesis lanzando <see cref="PolicyComplexityException"/> antes de invocar al parser recursivo;
    /// después analiza la misma lista de tokens (un único análisis léxico). Nunca lanza por SQL inválido: los errores
    /// vuelven en <paramref name="errors"/>.
    /// </summary>
    public static TSqlScript? Parse(string sql, out IList<ParseError> errors)
    {
        var parser = CreateParser();
        IList<TSqlParserToken> tokens;
        IList<ParseError> lexerErrors;
        using (var reader = new StringReader(sql))
        {
            tokens = parser.GetTokenStream(reader, out lexerErrors);
        }

        if (MaxParenthesisNesting(tokens) > MaxParenthesisDepth)
        {
            throw new PolicyComplexityException();
        }

        if (lexerErrors.Count > 0)
        {
            // El análisis desde tokens omite algunos errores léxicos (comentario sin cerrar): se informa con el
            // análisis completo, que además da la posición exacta. La profundidad ya está acotada.
            using var reader = new StringReader(sql);
            var withErrors = parser.Parse(reader, out errors);
            if (errors.Count == 0)
            {
                errors = lexerErrors;
            }

            return errors.Count > 0 ? null : withErrors as TSqlScript;
        }

        var fragment = parser.Parse(tokens, out errors);
        return fragment as TSqlScript;
    }

    /// <summary>
    /// Texto normalizado generado desde el AST (sin comentarios ni espacios irrelevantes, palabras clave en
    /// mayúsculas). Opciones nuevas en cada llamada porque el generador muta la instancia que recibe.
    /// </summary>
    public static string? Generate(TSqlFragment fragment)
    {
        try
        {
            var options = new SqlScriptGeneratorOptions
            {
                SqlVersion = SqlVersion.Sql170,
                KeywordCasing = KeywordCasing.Uppercase,
                IdentifierCasing = IdentifierCasing.Preserve,
                IdentifierBracketing = IdentifierBracketing.Preserve,
                NewLineBeforeFromClause = false,
                NewLineBeforeWhereClause = false,
                NewLineBeforeJoinClause = false,
                NewLineBeforeGroupByClause = false,
                NewLineBeforeOrderByClause = false,
                NewLineBeforeHavingClause = false,
                NewLineBeforeOutputClause = false,
                NewLineBeforeOffsetClause = false,
                NewLineBeforeWindowClause = false,
                NewLineAfterJoinKeyword = false,
                NewLineBeforeOnClause = false,
                AlignClauseBodies = false,
                MultilineSelectElementsList = false,
                MultilineWherePredicatesList = false,
                MultilineSetClauseItems = false,
                MultilineInsertSourcesList = false,
                MultilineInsertTargetsList = false,
                MultilineViewColumnsList = false,
                AsKeywordOnOwnLine = false,
                IndentViewBody = false,
                IndentationSize = 2,
            };
            var generator = new Sql170ScriptGenerator(options);
            generator.GenerateScript(fragment, out var script);
            return script.Trim();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Texto crudo recortado (cuando el análisis falla o no se pudo generar desde el AST).</summary>
    public static string RawNormalized(string sql)
    {
        var trimmed = (sql ?? string.Empty).Trim();
        return trimmed.Length <= MaxRawNormalizedChars ? trimmed : trimmed[..MaxRawNormalizedChars];
    }

    /// <summary>SHA-256 en hexadecimal minúsculas del texto UTF-8.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Ejecuta el trabajo en un hilo dedicado con pila grande. El parser, el visitor y el generador de ScriptDom
    /// son recursivos; con la pila por defecto, sentencias profundamente anidadas provocan un desbordamiento de
    /// pila que termina el proceso (no capturable). Las excepciones del trabajo se propagan al llamador.
    /// </summary>
    public static T RunWithLargeStack<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            AnalysisStackBytes)
        {
            IsBackground = true,
            Name = "SqlServerMcp.PolicyAnalysis",
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}

/// <summary>El árbol supera los límites de complejidad: el análisis se detiene y la sentencia se bloquea (POL-PARSE-003).</summary>
internal sealed class PolicyComplexityException : Exception
{
    public PolicyComplexityException()
        : base("La sentencia supera la complejidad máxima admitida.")
    {
    }

    public PolicyComplexityException(string message)
        : base(message)
    {
    }

    public PolicyComplexityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
