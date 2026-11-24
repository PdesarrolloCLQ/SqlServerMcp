using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Detección de predicados triviales (RS-POL-09, RF-DML-02, CA-11). Un predicado es trivial si es
/// trivialmente verdadero (1=1, 'a'='a', col = col, col LIKE '%', 1 IN (1), x IS NULL OR x IS NOT NULL,
/// NOT de algo trivialmente falso) o si no depende de las filas (no referencia columnas: 2&gt;1, @p = 1,
/// EXISTS (SELECT 1 FROM t)); en ambos casos puede afectar a todas las filas y se trata como ausencia de WHERE.
/// Las ramas OR con cualquier rama trivial hacen trivial todo el predicado.
/// </summary>
internal static class TrivialPredicateDetector
{
    /// <summary>True si el predicado no filtra filas de forma fiable (o si es null).</summary>
    public static bool IsTrivial(BooleanExpression? expression) => expression is null || IsTriviallyTrue(expression);

    private static bool IsTriviallyTrue(BooleanExpression e)
    {
        switch (e)
        {
            case BooleanParenthesisExpression p:
                return IsTriviallyTrue(p.Expression);
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or:
                return IsTriviallyTrue(or.FirstExpression) || IsTriviallyTrue(or.SecondExpression) || HasComplementaryNullChecks(or);
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                return IsTriviallyTrue(and.FirstExpression) && IsTriviallyTrue(and.SecondExpression);
        }

        if (!HasColumnReference(e))
        {
            // Independiente de las filas: si es verdadero en tiempo de ejecución afecta a toda la tabla.
            return true;
        }

        return e switch
        {
            BooleanNotExpression not => IsTriviallyFalse(not.Expression),
            BooleanComparisonExpression c => IsReflexive(c.ComparisonType) && SameExpression(c.FirstExpression, c.SecondExpression),
            LikePredicate l => !l.NotDefined && l.EscapeExpression is null && IsMatchAllPattern(l.SecondExpression),
            InPredicate i => !i.NotDefined && i.Subquery is null && i.Values.Any(v => SameExpression(i.Expression, v)),
            BooleanTernaryExpression t => t.TernaryExpressionType == BooleanTernaryExpressionType.Between
                && SameExpression(t.FirstExpression, t.SecondExpression)
                && SameExpression(t.FirstExpression, t.ThirdExpression),
            DistinctPredicate d => d.IsNot && SameExpression(d.FirstExpression, d.SecondExpression),
            _ => false,
        };
    }

    private static bool IsTriviallyFalse(BooleanExpression e) => e switch
    {
        BooleanParenthesisExpression p => IsTriviallyFalse(p.Expression),
        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and =>
            IsTriviallyFalse(and.FirstExpression) || IsTriviallyFalse(and.SecondExpression),
        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or =>
            IsTriviallyFalse(or.FirstExpression) && IsTriviallyFalse(or.SecondExpression),
        BooleanNotExpression not => IsTriviallyTrue(not.Expression),
        BooleanComparisonExpression c => IsIrreflexive(c.ComparisonType) && SameExpression(c.FirstExpression, c.SecondExpression),
        LikePredicate l => l.NotDefined && l.EscapeExpression is null && IsMatchAllPattern(l.SecondExpression),
        DistinctPredicate d => !d.IsNot && SameExpression(d.FirstExpression, d.SecondExpression),
        _ => false,
    };

    private static bool IsReflexive(BooleanComparisonType type) => type is BooleanComparisonType.Equals
        or BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.LessThanOrEqualTo
        or BooleanComparisonType.NotLessThan or BooleanComparisonType.NotGreaterThan or BooleanComparisonType.IsNotDistinctFrom;

    private static bool IsIrreflexive(BooleanComparisonType type) => type is BooleanComparisonType.NotEqualToBrackets
        or BooleanComparisonType.NotEqualToExclamation or BooleanComparisonType.GreaterThan or BooleanComparisonType.LessThan
        or BooleanComparisonType.IsDistinctFrom;

    private static bool IsMatchAllPattern(ScalarExpression? expression) =>
        Unwrap(expression) is StringLiteral { Value: { Length: > 0 } v } && v.All(ch => ch == '%');

    /// <summary>OR que contiene "x IS NULL" y "x IS NOT NULL" para la misma expresión.</summary>
    private static bool HasComplementaryNullChecks(BooleanBinaryExpression or)
    {
        var isNull = new HashSet<string>(StringComparer.Ordinal);
        var isNotNull = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<BooleanExpression>();
        pending.Push(or);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            switch (current)
            {
                case BooleanParenthesisExpression p:
                    pending.Push(p.Expression);
                    break;
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } b:
                    pending.Push(b.FirstExpression);
                    pending.Push(b.SecondExpression);
                    break;
                case BooleanIsNullExpression n:
                    (n.IsNot ? isNotNull : isNull).Add(Key(n.Expression));
                    break;
            }
        }

        return isNull.Overlaps(isNotNull);
    }

    /// <summary>Compara dos expresiones por sus tokens significativos (sin espacios, comentarios, paréntesis externos ni delimitadores).</summary>
    public static bool SameExpression(ScalarExpression? a, ScalarExpression? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var ka = Key(a);
        return ka.Length > 0 && string.Equals(ka, Key(b), StringComparison.Ordinal);
    }

    private static ScalarExpression? Unwrap(ScalarExpression? e)
    {
        while (e is ParenthesisExpression p)
        {
            e = p.Expression;
        }

        return e;
    }

    private static string Key(ScalarExpression? expression)
    {
        var e = Unwrap(expression);
        if (e?.ScriptTokenStream is not { } tokens || e.FirstTokenIndex < 0 || e.LastTokenIndex < e.FirstTokenIndex)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = e.FirstTokenIndex; i <= e.LastTokenIndex && i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.MultilineComment or TSqlTokenType.SingleLineComment)
            {
                continue;
            }

            var text = token.TokenType is TSqlTokenType.QuotedIdentifier or TSqlTokenType.AsciiStringOrQuotedIdentifier
                ? SqlNames.Normalize(token.Text)
                : token.Text;
            sb.Append(PolicyNames.Key(text)).Append('\u001F');
        }

        return sb.ToString();
    }

    /// <summary>True si el fragmento referencia alguna columna (excluido el comodín de COUNT(*)).</summary>
    public static bool HasColumnReference(TSqlFragment fragment)
    {
        var finder = new ColumnFinder();
        fragment.Accept(finder);
        return finder.Found;
    }

    private sealed class ColumnFinder : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType != ColumnType.Wildcard)
            {
                Found = true;
            }
        }
    }
}
