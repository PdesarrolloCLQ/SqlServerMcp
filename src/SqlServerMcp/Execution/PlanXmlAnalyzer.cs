using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Resume un plan SHOWPLAN XML (RF-QRY-09): costo y filas estimadas de la sentencia, los operadores más costosos por
/// costo propio, advertencias del plan en español e índices faltantes sugeridos (con un CREATE INDEX que nunca se
/// ejecuta). El XML se lee con DTD prohibida y sin resolución de entidades externas.
/// </summary>
public static class PlanXmlAnalyzer
{
    /// <summary>Espacio de nombres de SHOWPLAN XML.</summary>
    public const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Operadores devueltos en el resumen.</summary>
    public const int MaxOperators = 10;

    private const int MaxExpressionLength = 300;
    private const int MaxIndexNameLength = 128;

    private static readonly XNamespace Ns = ShowplanNamespace;

    /// <summary>Analiza el XML del plan. Nunca lanza por XML inválido: devuelve una advertencia.</summary>
    public static ExecutionPlanResult Analyze(string planXml)
    {
        ArgumentNullException.ThrowIfNull(planXml);
        XDocument document;
        try
        {
            document = Load(planXml);
        }
        catch (XmlException)
        {
            return new ExecutionPlanResult { Warnings = ["No se pudo analizar el XML del plan de ejecución."] };
        }

        double? totalCost = null;
        double? estimatedRows = null;
        var operators = new List<(PlanOperator Operator, double SelfCost)>();
        var warnings = new List<string>();
        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);
        var missingIndexes = new List<MissingIndexSuggestion>();
        var seenIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddWarning(string text)
        {
            if (seenWarnings.Add(text))
            {
                warnings.Add(text);
            }
        }

        foreach (var statement in document.Descendants(Ns + "StmtSimple"))
        {
            var statementCost = ParseDouble(statement.Attribute("StatementSubTreeCost"));
            if (statementCost is not null)
            {
                totalCost = (totalCost ?? 0) + statementCost.Value;
            }

            var queryPlan = statement.Element(Ns + "QueryPlan");
            if (queryPlan is null)
            {
                continue;
            }

            estimatedRows = ParseDouble(statement.Attribute("StatementEstRows")) ?? estimatedRows;
            switch ((string?)statement.Attribute("StatementOptmEarlyAbortReason"))
            {
                case "TimeOut":
                    AddWarning("El optimizador agotó su tiempo de compilación; el plan elegido podría no ser óptimo.");
                    break;
                case "MemoryLimitExceeded":
                    AddWarning("El optimizador superó su límite de memoria; el plan elegido podría no ser óptimo.");
                    break;
            }

            var relOps = queryPlan.Descendants(Ns + "RelOp").ToList();
            var baseCost = statementCost ?? relOps.Select(r => ParseDouble(r.Attribute("EstimatedTotalSubtreeCost")) ?? 0).DefaultIfEmpty(0).Max();
            foreach (var relOp in relOps)
            {
                var subtree = ParseDouble(relOp.Attribute("EstimatedTotalSubtreeCost")) ?? 0;
                var childCost = ChildRelOps(relOp).Sum(c => ParseDouble(c.Attribute("EstimatedTotalSubtreeCost")) ?? 0);
                var self = Math.Max(0, subtree - childCost);
                var percent = baseCost > 0 ? Math.Round(self / baseCost * 100, 2) : 0;
                operators.Add((new PlanOperator(
                    ParseInt(relOp.Attribute("NodeId")),
                    (string?)relOp.Attribute("PhysicalOp") ?? string.Empty,
                    (string?)relOp.Attribute("LogicalOp") ?? string.Empty,
                    subtree,
                    percent,
                    ParseDouble(relOp.Attribute("EstimateRows")) ?? 0,
                    ObjectName(relOp)), self));
            }

            foreach (var warningElement in queryPlan.Descendants(Ns + "Warnings"))
            {
                var owner = warningElement.Parent?.Name == Ns + "RelOp" ? warningElement.Parent : null;
                foreach (var text in DescribeWarnings(warningElement, owner))
                {
                    AddWarning(text);
                }
            }

            foreach (var group in queryPlan.Elements(Ns + "MissingIndexes").Elements(Ns + "MissingIndexGroup"))
            {
                var impact = ParseDouble(group.Attribute("Impact")) ?? 0;
                foreach (var index in group.Elements(Ns + "MissingIndex"))
                {
                    var suggestion = BuildSuggestion(index, impact);
                    if (suggestion is not null && seenIndexes.Add(suggestion.CreateStatement))
                    {
                        missingIndexes.Add(suggestion);
                    }
                }
            }
        }

        if (document.Root?.Name != Ns + "ShowPlanXML")
        {
            AddWarning("El documento no tiene el formato SHOWPLAN XML esperado.");
        }

        return new ExecutionPlanResult
        {
            EstimatedCost = totalCost,
            EstimatedRows = estimatedRows,
            CostlyOperators = operators
                .OrderByDescending(o => o.SelfCost)
                .ThenBy(o => o.Operator.NodeId)
                .Take(MaxOperators)
                .Select(o => o.Operator)
                .ToList(),
            Warnings = warnings,
            MissingIndexes = missingIndexes.OrderByDescending(m => m.Impact).ToList(),
        };
    }

    /// <summary>Carga segura: DTD prohibida, sin resolvedor, sin comentarios ni instrucciones de proceso.</summary>
    internal static XDocument Load(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 1024,
            CloseInput = true,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>RelOp descendientes inmediatos (sin atravesar otro RelOp).</summary>
    private static IEnumerable<XElement> ChildRelOps(XElement relOp)
    {
        var pending = new Stack<XElement>(relOp.Elements().Reverse());
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.Name == Ns + "RelOp")
            {
                yield return element;
                continue;
            }

            foreach (var child in element.Elements().Reverse())
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>Objeto (esquema.tabla e índice) del propio operador, sin mirar dentro de operadores hijos.</summary>
    private static string? ObjectName(XElement relOp)
    {
        var pending = new Stack<XElement>(relOp.Elements().Reverse());
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.Name == Ns + "RelOp")
            {
                continue;
            }

            if (element.Name == Ns + "Object")
            {
                var schema = Clean((string?)element.Attribute("Schema"));
                var table = Clean((string?)element.Attribute("Table"));
                var index = Clean((string?)element.Attribute("Index"));
                if (table is null)
                {
                    continue;
                }

                var name = schema is null ? table : schema + "." + table;
                return index is null ? name : name + " (" + index + ")";
            }

            foreach (var child in element.Elements().Reverse())
            {
                pending.Push(child);
            }
        }

        return null;
    }

    private static IEnumerable<string> DescribeWarnings(XElement warnings, XElement? relOp)
    {
        var where = relOp is null
            ? string.Empty
            : $" (nodo {ParseInt(relOp.Attribute("NodeId")).ToString(CultureInfo.InvariantCulture)}: {(string?)relOp.Attribute("PhysicalOp")})";

        if (IsTrue(warnings.Attribute("NoJoinPredicate")))
        {
            yield return $"Combinación sin predicado de unión: posible producto cartesiano{where}.";
        }

        if (IsTrue(warnings.Attribute("UnmatchedIndexes")))
        {
            yield return "Hay índices filtrados que no se pudieron usar por la parametrización de la consulta.";
        }

        if (IsTrue(warnings.Attribute("SpatialGuess")))
        {
            yield return $"La estimación de un predicado espacial se basa en una suposición{where}.";
        }

        if (IsTrue(warnings.Attribute("FullUpdateForOnlineIndexBuild")))
        {
            yield return "La operación requiere una actualización completa por una reconstrucción de índice en línea.";
        }

        foreach (var child in warnings.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "ColumnsWithNoStatistics":
                    yield return $"Columnas sin estadísticas{where}: {ColumnList(child)}.";
                    break;
                case "ColumnsWithStaleStatistics":
                    yield return $"Columnas con estadísticas desactualizadas{where}: {ColumnList(child)}.";
                    break;
                case "PlanAffectingConvert":
                {
                    var expression = Shorten((string?)child.Attribute("Expression") ?? string.Empty);
                    yield return (string?)child.Attribute("ConvertIssue") switch
                    {
                        "Cardinality Estimate" => $"Una conversión de tipos puede afectar la estimación de cardinalidad: {expression}.",
                        "Seek Plan" => $"Una conversión de tipos puede impedir búsquedas (seek) en índices: {expression}.",
                        _ => $"Una conversión de tipos afecta al plan: {expression}.",
                    };
                    break;
                }

                case "SpillToTempDb":
                case "SpillOccurred":
                case "SortSpillDetails":
                case "HashSpillDetails":
                case "ExchangeSpillDetails":
                    yield return $"El operador puede derramar datos a tempdb{where}.";
                    break;
                case "MemoryGrantWarning":
                    yield return $"Advertencia de concesión de memoria: {(string?)child.Attribute("GrantWarningKind") ?? "sin detalle"}.";
                    break;
                case "Wait":
                    yield return $"Espera registrada en el plan ({(string?)child.Attribute("WaitType") ?? "sin tipo"}).";
                    break;
                default:
                    yield return $"Advertencia del plan{where}: {child.Name.LocalName}.";
                    break;
            }
        }
    }

    private static string ColumnList(XElement element)
    {
        var columns = element.Descendants(Ns + "ColumnReference")
            .Select(c =>
            {
                var parts = new[] { Clean((string?)c.Attribute("Schema")), Clean((string?)c.Attribute("Table")), Clean((string?)c.Attribute("Column")) };
                return string.Join(".", parts.Where(p => p is not null));
            })
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return columns.Count == 0 ? "(sin detalle)" : string.Join(", ", columns);
    }

    private static MissingIndexSuggestion? BuildSuggestion(XElement index, double impact)
    {
        var schema = Clean((string?)index.Attribute("Schema"));
        var table = Clean((string?)index.Attribute("Table"));
        if (schema is null || table is null)
        {
            return null;
        }

        List<string> Columns(string usage) => index.Elements(Ns + "ColumnGroup")
            .Where(g => string.Equals((string?)g.Attribute("Usage"), usage, StringComparison.OrdinalIgnoreCase))
            .Elements(Ns + "Column")
            .Select(c => Clean((string?)c.Attribute("Name")))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        var equality = Columns("EQUALITY");
        var inequality = Columns("INEQUALITY");
        var include = Columns("INCLUDE");
        var keys = equality.Concat(inequality).ToList();
        if (keys.Count == 0 || keys.Concat(include).Any(c => c.Length > SqlNames.MaxIdentifierLength)
            || schema.Length > SqlNames.MaxIdentifierLength || table.Length > SqlNames.MaxIdentifierLength)
        {
            return null;
        }

        var indexName = BuildIndexName(table, keys);
        var builder = new StringBuilder()
            .Append("CREATE NONCLUSTERED INDEX ").Append(SqlNames.Quote(indexName))
            .Append(" ON ").Append(SqlNames.Quote(schema)).Append('.').Append(SqlNames.Quote(table))
            .Append(" (").Append(string.Join(", ", keys.Select(SqlNames.Quote))).Append(')');
        if (include.Count > 0)
        {
            builder.Append(" INCLUDE (").Append(string.Join(", ", include.Select(SqlNames.Quote))).Append(')');
        }

        builder.Append(';');
        return new MissingIndexSuggestion(Math.Round(impact, 2), schema + "." + table, equality, inequality, include, builder.ToString());
    }

    private static string BuildIndexName(string table, IEnumerable<string> keys)
    {
        var raw = "IX_" + table + "_" + string.Join("_", keys);
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return builder.Length > MaxIndexNameLength ? builder.ToString(0, MaxIndexNameLength) : builder.ToString();
    }

    private static string? Clean(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var normalized = SqlNames.Normalize(identifier);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string Shorten(string text) =>
        text.Length <= MaxExpressionLength ? text : text[..MaxExpressionLength] + ResultReader.TruncationMarker;

    private static bool IsTrue(XAttribute? attribute) =>
        attribute is not null && (attribute.Value == "1" || attribute.Value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static double? ParseDouble(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;

    private static int ParseInt(XAttribute? attribute) =>
        attribute is not null && int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
}
