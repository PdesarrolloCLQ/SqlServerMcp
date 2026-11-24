using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Sustituye en el árbol sintáctico las referencias a un sinónimo por su objeto base y regenera el texto, para evaluar la
/// sentencia exactamente como si usara el objeto base. Solo se usa para la evaluación: lo que se ejecuta y se audita es
/// siempre el texto original.
/// </summary>
internal static class SynonymRewriter
{
    /// <summary>Convierte base_object_name de sys.synonyms ("[db].[esquema].[objeto]", "[objeto]"…) en un nombre.</summary>
    public static SqlObjectName? ParseBaseObjectName(string? baseObjectName)
    {
        var parts = SqlNames.SplitMultipart(baseObjectName, 4);
        if (parts is null || parts.Count == 0 || string.IsNullOrWhiteSpace(parts[^1]))
        {
            return null;
        }

        static string? Part(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
        return parts.Count switch
        {
            1 => new SqlObjectName(null, null, null, parts[0]),
            2 => new SqlObjectName(null, null, Part(parts[0]), parts[1]),
            3 => new SqlObjectName(null, Part(parts[0]), Part(parts[1]), parts[2]),
            _ => new SqlObjectName(Part(parts[0]), Part(parts[1]), Part(parts[2]), parts[3]),
        };
    }

    /// <summary>
    /// Reescribe <paramref name="sql"/> reemplazando <paramref name="synonym"/> por <paramref name="target"/>.
    /// Devuelve null si el texto no se pudo analizar o regenerar; <paramref name="replacements"/> indica cuántas referencias
    /// se sustituyeron (0 = el sinónimo no aparece en una posición reescribible, por ejemplo dentro de un literal).
    /// </summary>
    public static string? Rewrite(string sql, SqlObjectName synonym, SqlObjectName target, out int replacements)
    {
        replacements = 0;
        var script = SqlParsing.Parse(sql, out var errors);
        if (errors.Count > 0 || script is null)
        {
            return null;
        }

        var visitor = new Visitor(synonym, target);
        script.Accept(visitor);
        replacements = visitor.Replacements;
        return SqlParsing.Generate(script);
    }

    private static bool SameName(SqlObjectName a, SqlObjectName b) =>
        string.Equals(a.Server ?? string.Empty, b.Server ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Database ?? string.Empty, b.Database ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Schema ?? string.Empty, b.Schema ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static Identifier Id(string value) => new() { Value = value, QuoteType = QuoteType.SquareBracket };

    /// <summary>Partes en el orden de ScriptDom (servidor, base, esquema, objeto); las intermedias vacías se conservan.</summary>
    private static List<string> Parts(SqlObjectName name)
    {
        var parts = new List<string>(4);
        if (name.Server is not null)
        {
            parts.Add(name.Server);
        }

        if (name.Server is not null || name.Database is not null)
        {
            parts.Add(name.Database ?? string.Empty);
        }

        if (name.Server is not null || name.Database is not null || name.Schema is not null)
        {
            parts.Add(name.Schema ?? PolicyNames.DefaultSchema);
        }

        parts.Add(name.Name);
        return parts;
    }

    private sealed class Visitor(SqlObjectName synonym, SqlObjectName target) : TSqlFragmentVisitor
    {
        public int Replacements { get; private set; }

        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (node is not ChildObjectName && SameName(PolicyNames.From(node), synonym))
            {
                node.Identifiers.Clear();
                foreach (var part in Parts(target))
                {
                    node.Identifiers.Add(Id(part));
                }

                Replacements++;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier: { } qualifier } && node.FunctionName is { } function)
            {
                var ids = qualifier.Identifiers;
                var called = ids.Count switch
                {
                    1 => new SqlObjectName(null, null, ids[0].Value, function.Value),
                    2 => new SqlObjectName(null, ids[0].Value, ids[1].Value, function.Value),
                    _ => null,
                };
                if (called is not null && SameName(called, synonym))
                {
                    var parts = Parts(target);
                    ids.Clear();
                    foreach (var part in parts.Take(parts.Count - 1))
                    {
                        ids.Add(Id(part));
                    }

                    if (ids.Count == 0)
                    {
                        node.CallTarget = null;
                    }

                    node.FunctionName = Id(parts[^1]);
                    Replacements++;
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
