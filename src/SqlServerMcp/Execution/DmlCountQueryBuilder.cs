using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Execution;

/// <summary>
/// Construye, a partir del AST de ScriptDom, la consulta de conteo que estima las filas afectadas por un
/// UPDATE o DELETE con el mismo origen y el mismo predicado (RF-DML-03, CA-13). Es puramente sintáctica:
/// no realiza I/O ni modifica el AST original (los nodos se reutilizan en un árbol nuevo).
/// </summary>
/// <remarks>
/// Forma generada: <c>[WITH …] SELECT COUNT_BIG(*) FROM &lt;destino&gt; WHERE &lt;predicado&gt; [OPTION (…)]</c>.
/// Con <c>TOP (n)</c> se envuelve en una tabla derivada: <c>SELECT COUNT_BIG(*) FROM (SELECT TOP (n) 1 AS x FROM … WHERE …) AS q</c>.
/// Devuelve null (no estimable por conteo; el ejecutor usa una ejecución de prueba revertida) cuando la sentencia
/// tiene cláusula FROM (las combinaciones pueden contar de más), usa <c>TOP … PERCENT</c>, <c>WHERE CURRENT OF</c>,
/// o el destino no es una tabla, vista o CTE con nombre. La consulta usa exactamente los mismos parámetros que la original.
/// </remarks>
public sealed class DmlCountQueryBuilder
{
    /// <summary>Nombre de la columna de la tabla derivada usada con TOP (n).</summary>
    private const string TopColumnAlias = "x";

    /// <summary>Alias de la tabla derivada usada con TOP (n).</summary>
    private const string DerivedTableAlias = "q";

    /// <summary>
    /// Genera el texto T-SQL de la consulta de conteo, o null si la sentencia no es estimable mediante conteo.
    /// </summary>
    /// <param name="statementFragment">
    /// <see cref="Policy.SqlStatementInfo.Fragment"/>: se esperan <see cref="UpdateStatement"/> o <see cref="DeleteStatement"/>.
    /// </param>
    public string? Build(object? statementFragment)
    {
        UpdateDeleteSpecificationBase? specification;
        DataModificationStatement statement;
        switch (statementFragment)
        {
            case UpdateStatement update:
                specification = update.UpdateSpecification;
                statement = update;
                break;
            case DeleteStatement delete:
                specification = delete.DeleteSpecification;
                statement = delete;
                break;
            default:
                return null;
        }

        if (specification is null
            || specification.FromClause is not null
            || specification.WhereClause is { Cursor: not null }
            || specification.WhereClause is { SearchCondition: null }
            || specification.TopRowFilter is { Percent: true }
            || specification.Target is not NamedTableReference target
            || target.SchemaObject is null)
        {
            return null;
        }

        // Destino sin sugerencias de tabla (WITH (UPDLOCK, …)): el conteo no debe tomar bloqueos de actualización.
        var source = new NamedTableReference { SchemaObject = target.SchemaObject, Alias = target.Alias };
        var whereClause = specification.WhereClause;

        QuerySpecification outer;
        if (specification.TopRowFilter is { } top)
        {
            if (top.Expression is null)
            {
                return null;
            }

            var inner = new QuerySpecification
            {
                TopRowFilter = new TopRowFilter { Expression = top.Expression },
                FromClause = CreateFrom(source),
                WhereClause = whereClause,
            };
            inner.SelectElements.Add(new SelectScalarExpression
            {
                Expression = new IntegerLiteral { Value = "1" },
                ColumnName = new IdentifierOrValueExpression { Identifier = new Identifier { Value = TopColumnAlias } },
            });

            outer = new QuerySpecification
            {
                FromClause = CreateFrom(new QueryDerivedTable
                {
                    QueryExpression = inner,
                    Alias = new Identifier { Value = DerivedTableAlias },
                }),
            };
        }
        else
        {
            outer = new QuerySpecification
            {
                FromClause = CreateFrom(source),
                WhereClause = whereClause,
            };
        }

        outer.SelectElements.Add(new SelectScalarExpression { Expression = CreateCountBig() });

        var select = new SelectStatement
        {
            QueryExpression = outer,
            WithCtesAndXmlNamespaces = statement.WithCtesAndXmlNamespaces,
        };
        foreach (var hint in statement.OptimizerHints)
        {
            select.OptimizerHints.Add(hint);
        }

        var text = Generate(select);
        return text is not null && IsSingleSelect(text) ? text : null;
    }

    private static FromClause CreateFrom(TableReference reference)
    {
        var from = new FromClause();
        from.TableReferences.Add(reference);
        return from;
    }

    private static FunctionCall CreateCountBig()
    {
        var call = new FunctionCall { FunctionName = new Identifier { Value = "COUNT_BIG" } };
        call.Parameters.Add(new ColumnReferenceExpression { ColumnType = ColumnType.Wildcard });
        return call;
    }

    private static string? Generate(TSqlFragment fragment)
    {
        // Cada generador con sus propias opciones: el constructor muta y comparte la instancia de opciones.
        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
        });
        generator.GenerateScript(fragment, out var script, out var errors);
        return errors.Count == 0 && !string.IsNullOrWhiteSpace(script) ? script.Trim() : null;
    }

    /// <summary>Comprobación defensiva: el texto generado debe ser exactamente un SELECT sin INTO.</summary>
    private static bool IsSingleSelect(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        return errors.Count == 0
            && fragment is TSqlScript { Batches.Count: 1 } script
            && script.Batches[0].Statements.Count == 1
            && script.Batches[0].Statements[0] is SelectStatement { Into: null };
    }
}
