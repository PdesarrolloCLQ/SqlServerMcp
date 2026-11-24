using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Obtiene los valores por defecto de los parámetros de un procedimiento o función T-SQL a partir de su
/// definición (RF-MET-04). SQL Server no los expone en el catálogo: sys.parameters.has_default_value solo
/// se mantiene para objetos CLR. Se usa únicamente el analizador léxico de ScriptDom (sin recursión, coste lineal),
/// por lo que no hay riesgo de desbordamiento de pila con módulos grandes o anidados.
/// </summary>
internal static class RoutineParameterDefaults
{
    private static readonly string[] TrailingModifiers = ["OUTPUT", "OUT", "READONLY", "VARYING"];

    /// <summary>
    /// Devuelve un diccionario nombre de parámetro (con "@", sin distinguir mayúsculas) → texto del valor por defecto.
    /// Solo incluye los parámetros que tienen valor por defecto. Devuelve un diccionario vacío si el texto no es
    /// un CREATE/ALTER PROCEDURE o FUNCTION reconocible.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Extract(string? definition)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return result;
        }

        IList<TSqlParserToken> tokens;
        try
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(definition);
            tokens = parser.GetTokenStream(reader, out var errors);
            if (tokens is null || errors.Count > 0)
            {
                return result;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return result;
        }

        var significant = tokens.Where(t => !IsTrivia(t.TokenType)).ToList();
        var index = FindRoutineKeyword(significant);
        if (index < 0)
        {
            return result;
        }

        // Saltar el nombre del objeto hasta el primer parámetro o el paréntesis de la lista.
        var parenthesized = false;
        while (index < significant.Count)
        {
            var type = significant[index].TokenType;
            if (type == TSqlTokenType.Variable)
            {
                break;
            }

            if (type == TSqlTokenType.LeftParenthesis)
            {
                parenthesized = true;
                index++;
                break;
            }

            if (type is TSqlTokenType.As or TSqlTokenType.With or TSqlTokenType.For or TSqlTokenType.EndOfFile)
            {
                return result;
            }

            index++;
        }

        var baseDepth = parenthesized ? 1 : 0;
        var depth = baseDepth;
        string? currentParameter = null;
        List<TSqlParserToken>? defaultTokens = null;

        for (; index < significant.Count; index++)
        {
            var token = significant[index];
            var type = token.TokenType;
            if (type == TSqlTokenType.EndOfFile)
            {
                break;
            }

            if (type == TSqlTokenType.LeftParenthesis)
            {
                depth++;
            }
            else if (type == TSqlTokenType.RightParenthesis)
            {
                depth--;
                if (parenthesized && depth < baseDepth)
                {
                    break;
                }
            }
            else if (depth == baseDepth && !parenthesized && type is TSqlTokenType.As or TSqlTokenType.With or TSqlTokenType.For)
            {
                break;
            }
            else if (depth == baseDepth && type == TSqlTokenType.Comma)
            {
                Complete(definition, currentParameter, defaultTokens, result);
                currentParameter = null;
                defaultTokens = null;
                continue;
            }
            else if (depth == baseDepth && type == TSqlTokenType.Variable && currentParameter is null)
            {
                currentParameter = token.Text;
                continue;
            }
            else if (depth == baseDepth && type == TSqlTokenType.EqualsSign && currentParameter is not null && defaultTokens is null)
            {
                defaultTokens = [];
                continue;
            }

            defaultTokens?.Add(token);
        }

        Complete(definition, currentParameter, defaultTokens, result);
        return result;
    }

    private static int FindRoutineKeyword(List<TSqlParserToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType is not (TSqlTokenType.Create or TSqlTokenType.Alter))
            {
                continue;
            }

            // CREATE [OR ALTER] PROC|PROCEDURE|FUNCTION
            for (var j = i + 1; j < Math.Min(tokens.Count, i + 4); j++)
            {
                if (tokens[j].TokenType is TSqlTokenType.Proc or TSqlTokenType.Procedure or TSqlTokenType.Function)
                {
                    return j + 1;
                }
            }

            return -1;
        }

        return -1;
    }

    private static void Complete(string definition, string? parameter, List<TSqlParserToken>? tokens, Dictionary<string, string> result)
    {
        if (parameter is null || tokens is null)
        {
            return;
        }

        var end = tokens.Count;
        while (end > 0
               && tokens[end - 1].TokenType == TSqlTokenType.Identifier
               && TrailingModifiers.Contains(tokens[end - 1].Text, StringComparer.OrdinalIgnoreCase))
        {
            end--;
        }

        if (end == 0)
        {
            return;
        }

        var first = tokens[0];
        var last = tokens[end - 1];
        var start = first.Offset;
        var length = last.Offset + last.Text.Length - start;
        if (start < 0 || length <= 0 || start + length > definition.Length)
        {
            return;
        }

        result[parameter] = definition.Substring(start, length).Trim();
    }

    private static bool IsTrivia(TSqlTokenType type) =>
        type is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
}
