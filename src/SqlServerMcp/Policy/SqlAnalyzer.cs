using System.Collections.Frozen;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>Modo de un analizador: texto original, lote con varias sentencias (ya rechazado) o literal de SQL dinámico.</summary>
internal enum AnalyzerMode
{
    /// <summary>Sentencia única recibida: la primera sentencia de nivel superior es la raíz.</summary>
    Root = 0,

    /// <summary>Lote con varias sentencias: se analizan todas solo para auditoría y alertas (sin raíz).</summary>
    Batch = 1,

    /// <summary>Literal de SQL dinámico re-analizado: sus sentencias son "texto libre" anidadas en el EXEC.</summary>
    Dynamic = 2,
}

/// <summary>
/// Recorre el árbol completo de una sentencia con <see cref="TSqlFragmentVisitor"/> (RS-POL-05): sentencias anidadas
/// (cuerpos de módulos, IF/WHILE/BEGIN…END/TRY…CATCH, CREATE SCHEMA, SQL dinámico literal), referencias a objetos con
/// su rol, columnas con alias resueltos, funciones, orígenes de filas de todo tipo, EXEC, OUTPUT, opciones de módulos y
/// ámbito de triggers, y alimenta <see cref="AnalysisContext"/> con las coincidencias de las reglas inmutables
/// (RS-POL-06/07/08, RS-DCL-01, RS-DAT-01/02/06, RF-DDL-02/03/04, RF-DML-02, Anexo C).
/// </summary>
/// <remarks>
/// ScriptDom no ofrece puntero al padre: la sentencia actual se obtiene de una pila sincronizada por rangos de
/// tokens (un nodo pertenece a la sentencia cuyo rango lo contiene) y los ámbitos de consulta con una pila
/// mantenida en <c>ExplicitVisit</c>. Las referencias ya procesadas por un manejador tipado se marcan para que el
/// manejador general de <see cref="SchemaObjectName"/> (red de seguridad) no las vuelva a evaluar.
/// </remarks>
internal sealed class SqlAnalyzer : TSqlFragmentVisitor
{
    /// <summary>Profundidad máxima de SQL dinámico literal dentro de SQL dinámico.</summary>
    public const int MaxDynamicDepth = 3;

    private static readonly FrozenSet<Type> KnownTableReferences = new[]
    {
        typeof(AdHocTableReference), typeof(AIGenerateChunksTableReference), typeof(AIGenerateFixedChunksTableReference),
        typeof(BuiltInFunctionTableReference), typeof(BulkOpenRowset), typeof(ChangeTableChangesTableReference),
        typeof(ChangeTableVersionTableReference), typeof(DataModificationTableReference), typeof(FullTextTableReference),
        typeof(GlobalFunctionTableReference), typeof(InlineDerivedTable), typeof(InternalOpenRowset),
        typeof(JoinParenthesisTableReference), typeof(NamedTableReference), typeof(OdbcQualifiedJoinTableReference),
        typeof(OpenJsonTableReference), typeof(OpenQueryTableReference), typeof(OpenRowsetCosmos),
        typeof(OpenRowsetTableReference), typeof(OpenXmlTableReference), typeof(PivotedTableReference),
        typeof(PredictTableReference), typeof(QualifiedJoin), typeof(QueryDerivedTable),
        typeof(SchemaObjectFunctionTableReference), typeof(SemanticTableReference), typeof(UnpivotedTableReference),
        typeof(UnqualifiedJoin), typeof(VariableMethodCallTableReference), typeof(VariableTableReference),
        typeof(VectorSearchTableReference),
    }.ToFrozenSet();

    /// <summary>Funciones integradas que devuelven el código fuente de módulos: equivalen a consultar el catálogo.</summary>
    private static readonly FrozenSet<string> CatalogScalarFunctions = new[] { "OBJECT_DEFINITION" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly AnalysisContext _ctx;
    private readonly AnalyzerMode _mode;
    private readonly StatementUnit? _outerUnit;
    private readonly int _dynamicDepth;
    private readonly Stack<StatementUnit> _units = new();
    private readonly Stack<QueryScope> _scopes = new();
    private readonly HashSet<TSqlFragment> _handled = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TableReference, TableSource> _writeTargets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ColumnReferenceExpression, TopLevelColumn> _topLevel = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ColumnReferenceExpression> _outputColumns = new(ReferenceEqualityComparer.Instance);
    private readonly List<(int First, int Last)> _unsafeStarRegions = [];
    private bool _topLevelForJsonXml;
    private bool _rootSeen;
    private int _nesting;
    private SelectStatement? _rootSelect;

    public SqlAnalyzer(AnalysisContext ctx, AnalyzerMode mode, StatementUnit? outerUnit = null, int dynamicDepth = 0)
    {
        _ctx = ctx;
        _mode = mode;
        _outerUnit = outerUnit;
        _dynamicDepth = dynamicDepth;
    }

    private PolicyConfig Policy => _ctx.Profile.Policy;

    private HitSink Hits => _ctx.Hits;

    private bool ProtectedColumnsConfigured => _ctx.SensitivePatternsConfigured || _ctx.ExcludedColumnsConfigured || _ctx.PiiMaskConfigured;

    /// <summary>Analiza una sentencia de nivel superior (y todo su árbol).</summary>
    public void Analyze(TSqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var prePass = new PrePass();
        statement.Accept(prePass);
        _unsafeStarRegions.AddRange(prePass.UnsafeStarRegions);
        statement.Accept(this);
        CheckRootOrderBy();
    }

    // =====================================================================================================
    // Sentencias
    // =====================================================================================================

    /// <summary>Se invoca para cada sentencia (raíz y anidadas) antes de visitar sus hijos.</summary>
    public override void Visit(TSqlStatement node)
    {
        var parentInScript = SyncUnits(node);
        var parent = parentInScript ?? _outerUnit;
        var isRoot = _mode == AnalyzerMode.Root && parentInScript is null && !_rootSeen;
        if (isRoot)
        {
            _rootSeen = true;
        }

        var inModule = parentInScript is not null && (parentInScript.InModule || IsModuleBody(parentInScript.Statement));
        var unit = new StatementUnit(node, parent, StatementClassifier.Classify(node), isRoot, inModule);
        if (unit.Depth > SqlParsing.MaxStatementNesting)
        {
            throw new PolicyComplexityException();
        }

        _ctx.Units.Add(unit);
        if (!isRoot)
        {
            _ctx.NestedStatementTypes.Add(node.GetType().Name);
        }

        _units.Push(unit);
        AnalyzeStatement(unit);
    }

    private static bool IsModuleBody(TSqlStatement statement) => statement is ProcedureStatementBodyBase or TriggerStatementBody;

    /// <summary>Saca de la pila las sentencias que no contienen el nodo y devuelve la sentencia actual del script.</summary>
    private StatementUnit? SyncUnits(TSqlFragment node)
    {
        while (_units.Count > 0 && !_units.Peek().Contains(node))
        {
            _units.Pop();
        }

        return _units.Count > 0 ? _units.Peek() : null;
    }

    private StatementUnit? CurrentUnit(TSqlFragment node) => SyncUnits(node) ?? _outerUnit;

    private void AnalyzeStatement(StatementUnit unit)
    {
        var statement = unit.Statement;
        var cls = unit.Class;

        if (cls.ImmutableRule is { } immutable)
        {
            Hits.Add(immutable, DescribeStatement(unit));
        }

        if (cls.Category == StatementCategory.Unknown)
        {
            Hits.Add(PolicyRules.UnknownStatement, $"{statement.GetType().Name}");
        }

        if (statement is PredicateSetStatement set && (set.Options & SetOptions.QuotedIdentifier) != 0)
        {
            Hits.Add(PolicyRules.QuotedIdentifier);
        }

        switch (cls.Category)
        {
            case StatementCategory.TransactionControl:
                if (statement is BeginTransactionStatement { Distributed: true })
                {
                    Hits.Add(PolicyRules.DistributedTransaction);
                }
                else if (unit.IsFreeText)
                {
                    Hits.Add(PolicyRules.TransactionControl, cls.Operation);
                }

                break;
            case StatementCategory.SessionOption:
                if (unit.IsFreeText)
                {
                    Hits.Add(PolicyRules.SessionOption, cls.Operation);
                }

                break;
            case StatementCategory.ContextChange:
                Hits.Add(PolicyRules.UseDatabase);
                break;
            case StatementCategory.Wait:
                Hits.Add(PolicyRules.WaitFor);
                break;
            case StatementCategory.ProceduralControl or StatementCategory.Cursor:
                if (unit.IsFreeText)
                {
                    Hits.Add(PolicyRules.ControlFlowAtRoot, cls.Operation);
                }

                break;
        }

        if (statement is StatementWithCtesAndXmlNamespaces withCtes && withCtes.WithCtesAndXmlNamespaces?.CommonTableExpressions is { Count: > 0 } ctes)
        {
            foreach (var cte in ctes)
            {
                var name = PolicyNames.Value(cte.ExpressionName);
                if (!string.IsNullOrEmpty(name))
                {
                    unit.Ctes.Add(new CteInfo(name, SimpleBaseObject(cte.QueryExpression, unit)));
                }
            }
        }

        if (unit.IsRoot)
        {
            ApplyToolAllowlist(unit);
        }

        switch (statement)
        {
            case SelectStatement select:
                AnalyzeSelect(unit, select);
                break;
            case CreateSchemaStatement createSchema:
                AnalyzeCreateSchema(unit, createSchema);
                break;
        }

        ProcessDdlTargets(unit);
    }

    private static string DescribeStatement(StatementUnit unit) =>
        unit.Class.ObjectType is { } type ? $"{unit.Class.Operation} {type}" : unit.Class.Operation ?? unit.Statement.GetType().Name;

    /// <summary>Tabla base de una CTE cuando su consulta es un SELECT simple sobre una sola tabla (destino real de un DML).</summary>
    private static SqlObjectName? SimpleBaseObject(QueryExpression? query, StatementUnit unit)
    {
        if (query is QuerySpecification { FromClause.TableReferences: [NamedTableReference named] })
        {
            var name = PolicyNames.From(named.SchemaObject);
            if (PolicyNames.IsOnePart(name) && unit.FindCte(name.Name) is not null)
            {
                return null;
            }

            return name;
        }

        return null;
    }

    // ----------------------------------------------------------------------------------------------- allowlist (RS-POL-04)

    private void ApplyToolAllowlist(StatementUnit unit)
    {
        var category = unit.Class.Category;
        var statement = unit.Statement;
        switch (_ctx.Tool)
        {
            case ToolKind.Query or ToolKind.ExecutionPlan:
                if (category == StatementCategory.Query)
                {
                    return;
                }

                if (category == StatementCategory.SelectInto)
                {
                    Hits.Add(PolicyRules.SelectIntoInQuery);
                    return;
                }

                AddToolNotAllowed(unit, "solo admite una sentencia SELECT sin INTO");
                return;

            case ToolKind.Dml:
                if (category is StatementCategory.Insert or StatementCategory.Update or StatementCategory.Delete or StatementCategory.Merge)
                {
                    if (statement is InsertStatement { InsertSpecification.InsertSource: ExecuteInsertSource })
                    {
                        Hits.Add(PolicyRules.InsertExec);
                    }

                    return;
                }

                AddToolNotAllowed(unit, "solo admite INSERT, UPDATE, DELETE o MERGE");
                return;

            case ToolKind.Ddl:
                if (category is StatementCategory.DdlCreate or StatementCategory.DdlAlter or StatementCategory.DdlDrop
                    or StatementCategory.DdlTruncate or StatementCategory.DdlDestructiveAlter or StatementCategory.DdlTrigger
                    or StatementCategory.Statistics or StatementCategory.ExtendedProperty or StatementCategory.SelectInto)
                {
                    return;
                }

                AddToolNotAllowed(unit, "solo admite CREATE, ALTER y DROP de objetos permitidos, TRUNCATE, estadísticas, triggers DML, sp_rename y propiedades extendidas");
                return;

            default:
                return;
        }
    }

    private void AddToolNotAllowed(StatementUnit unit, string what)
    {
        var detail = $"se recibió {DescribeStatement(unit)}; la herramienta {what}";
        if (unit.Statement is ExecuteStatement && !StartsWithExecKeyword(unit.Statement)
            && TryGetProcedureName(unit.Statement) is { } proc)
        {
            detail += $". El texto se interpretó como la ejecución del procedimiento '{proc}' (¿error de escritura?)";
        }

        Hits.Add(PolicyRules.ToolNotAllowed, detail, suggestion: SuggestTool(unit.Class.Category));
    }

    private static string? TryGetProcedureName(TSqlStatement statement) =>
        statement is ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } name } }
            ? PolicyNames.Display(PolicyNames.From(name))
            : null;

    private static bool StartsWithExecKeyword(TSqlStatement statement)
    {
        var tokens = statement.ScriptTokenStream;
        if (tokens is null || statement.FirstTokenIndex < 0 || statement.FirstTokenIndex >= tokens.Count)
        {
            return true;
        }

        var text = tokens[statement.FirstTokenIndex].Text;
        return string.Equals(text, "EXEC", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "EXECUTE", StringComparison.OrdinalIgnoreCase);
    }

    private static string SuggestTool(StatementCategory category) => category switch
    {
        StatementCategory.Query => "Use run_query (o get_execution_plan) para consultas SELECT.",
        StatementCategory.SelectInto => "Para crear una tabla a partir de una consulta use execute_ddl; para leer datos use un SELECT sin INTO con run_query.",
        StatementCategory.Insert or StatementCategory.Update or StatementCategory.Delete or StatementCategory.Merge =>
            "Use execute_dml (o insert_rows para inserciones estructuradas).",
        StatementCategory.DdlCreate or StatementCategory.DdlAlter or StatementCategory.DdlDrop or StatementCategory.DdlTruncate
            or StatementCategory.DdlDestructiveAlter or StatementCategory.DdlTrigger or StatementCategory.Statistics
            or StatementCategory.ExtendedProperty => "Use execute_ddl para cambios de esquema.",
        StatementCategory.ExecuteProcedure => "Use execute_procedure para ejecutar procedimientos almacenados con parámetros estructurados.",
        StatementCategory.DynamicSql => "Escriba la sentencia directamente, sin SQL dinámico, en la herramienta que corresponda.",
        StatementCategory.TransactionControl => "Use begin_transaction, commit_transaction y rollback_transaction.",
        _ => "Use la herramienta adecuada para el tipo de sentencia (consulte get_server_policy).",
    };

    // ----------------------------------------------------------------------------------------------- SELECT

    private void AnalyzeSelect(StatementUnit unit, SelectStatement select)
    {
        if (!unit.IsRoot || select.Into is not null)
        {
            return;
        }

        _rootSelect = select;
        var query = select.QueryExpression;
        while (true)
        {
            if (query?.ForClause is JsonForClause or XmlForClause)
            {
                _topLevelForJsonXml = true;
            }

            if (query is QueryParenthesisExpression parenthesis)
            {
                query = parenthesis.QueryExpression;
            }
            else if (query is BinaryQueryExpression binary)
            {
                query = binary.FirstQueryExpression;
            }
            else
            {
                break;
            }
        }

        if (query is not QuerySpecification spec)
        {
            return;
        }

        unit.HasWhere = spec.WhereClause is not null;
        unit.TrivialPredicate = spec.WhereClause?.SearchCondition is { } condition && TrivialPredicateDetector.IsTrivial(condition);
        unit.HasTop = spec.TopRowFilter is not null;

        // Lista SELECT principal (RS-DAT-02): columnas sin transformar, con su posición si no hay '*' antes.
        var ordinal = 0;
        var starSeen = false;
        foreach (var element in spec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression:
                    starSeen = true;
                    break;
                case SelectScalarExpression scalar:
                    if (Unwrap(scalar.Expression) is ColumnReferenceExpression { ColumnType: ColumnType.Regular, MultiPartIdentifier.Count: > 0 } column)
                    {
                        var alias = scalar.ColumnName?.Value ?? scalar.ColumnName?.Identifier?.Value;
                        var outputName = alias ?? column.MultiPartIdentifier.Identifiers[^1].Value;
                        _topLevel[column] = new TopLevelColumn(starSeen ? null : ordinal, alias is not null, outputName);
                    }

                    ordinal++;
                    break;
            }
        }
    }

    private static ScalarExpression? Unwrap(ScalarExpression? expression)
    {
        while (expression is ParenthesisExpression parenthesis)
        {
            expression = parenthesis.Expression;
        }

        return expression;
    }

    /// <summary>ORDER BY por posición o por alias de una columna enmascarada permitiría inferir su valor (RS-DAT-02).</summary>
    private void CheckRootOrderBy()
    {
        if (_rootSelect?.QueryExpression?.OrderByClause is not { } orderBy || _ctx.MaskOrdinals.Count == 0)
        {
            return;
        }

        var maskedNames = _topLevel.Values.Where(t => t.Ordinal is { } o && _ctx.MaskOrdinals.Contains(o)).Select(t => t.OutputName).ToList();
        foreach (var element in orderBy.OrderByElements)
        {
            switch (Unwrap(element.Expression))
            {
                case IntegerLiteral literal when int.TryParse(literal.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var position)
                                                 && _ctx.MaskOrdinals.Contains(position - 1):
                    Hits.Add(PolicyRules.SensitiveColumnInference, $"ORDER BY {position} ordena por una columna enmascarada");
                    break;
                case ColumnReferenceExpression { MultiPartIdentifier.Count: 1 } column
                    when maskedNames.Any(n => PolicyNames.EqualsIgnoreCase(n, column.MultiPartIdentifier.Identifiers[0].Value)):
                    Hits.Add(PolicyRules.SensitiveColumnInference, $"ORDER BY '{column.MultiPartIdentifier.Identifiers[0].Value}' ordena por una columna enmascarada");
                    break;
            }
        }
    }

    // ----------------------------------------------------------------------------------------------- DDL (RF-DDL-02/03/04)

    private void AnalyzeCreateSchema(StatementUnit unit, CreateSchemaStatement statement)
    {
        var name = PolicyNames.Value(statement.Name);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var schemaTarget = new SqlObjectName(null, null, null, name);
        _ctx.AddObject(unit, schemaTarget, ObjectRole.DdlTarget, "SCHEMA");
        SetTarget(unit, schemaTarget);
        ReferenceRules.CheckDdlSchema(_ctx, name);
        unit.DefaultSchemaForChildren = name;

        if (statement.Owner is not null)
        {
            var owner = PolicyNames.Value(statement.Owner) ?? string.Empty;
            if (!Policy.AllowedSchemaOwners.Any(o => PolicyNames.EqualsIgnoreCase(o?.Trim(), owner)))
            {
                Hits.Add(PolicyRules.DdlSchemaOwner, $"AUTHORIZATION '{owner}'");
            }
        }
    }

    /// <summary>Destinos DDL por tipo exacto de sentencia (los nombres se marcan para no evaluarlos como lecturas).</summary>
    private void ProcessDdlTargets(StatementUnit unit)
    {
        if (unit.IsRoot && _ctx.ModuleInspection)
        {
            return;
        }

        switch (unit.Statement)
        {
            case SelectStatement { Into: { } into }:
                AddDdlTarget(unit, into);
                break;
            case CreateTableStatement create:
                unit.DefaultSource = AddDdlTarget(unit, create.SchemaObjectName);
                break;
            case AlterTableSwitchStatement switchStatement:
                unit.DefaultSource = AddDdlTarget(unit, switchStatement.SchemaObjectName);
                AddDdlTarget(unit, switchStatement.TargetTable);
                break;
            case AlterTableStatement alterTable:
                unit.DefaultSource = AddDdlTarget(unit, alterTable.SchemaObjectName);
                break;
            case DropTriggerStatement dropTrigger:
                foreach (var name in dropTrigger.Objects)
                {
                    if (dropTrigger.TriggerScope == TriggerScope.Normal)
                    {
                        AddDdlTarget(unit, name);
                    }
                    else
                    {
                        _handled.Add(name);
                    }
                }

                break;
            case DropObjectsStatement drop:
                foreach (var name in drop.Objects)
                {
                    AddDdlTarget(unit, name);
                }

                break;
            case DropChildObjectsStatement dropChildren:
                foreach (var child in dropChildren.Objects)
                {
                    _handled.Add(child);
                    AddDdlTarget(unit, PolicyNames.ParentOf(child));
                }

                break;
            case TruncateTableStatement truncate:
                AddDdlTarget(unit, truncate.TableName);
                break;
            case ViewStatementBody view:
                AddDdlTarget(unit, view.SchemaObjectName);
                break;
            case ProcedureStatementBody procedure:
                AddDdlTarget(unit, procedure.ProcedureReference?.Name);
                break;
            case FunctionStatementBody function:
                AddDdlTarget(unit, function.Name);
                break;
            case TriggerStatementBody trigger:
                ProcessTrigger(unit, trigger.TriggerObject, [trigger.Name]);
                break;
            case EnableDisableTriggerStatement enableDisable:
                ProcessTrigger(unit, enableDisable.TriggerObject, enableDisable.TriggerNames);
                break;
            case IndexStatement index:
                unit.DefaultSource = AddDdlTarget(unit, index.OnName);
                break;
            case CreateColumnStoreIndexStatement columnStore:
                unit.DefaultSource = AddDdlTarget(unit, columnStore.OnName);
                break;
            case CreateSpatialIndexStatement spatial:
                unit.DefaultSource = AddDdlTarget(unit, spatial.Object);
                break;
            case DropIndexStatement dropIndex:
                foreach (var clause in dropIndex.DropIndexClauses)
                {
                    switch (clause)
                    {
                        case DropIndexClause modern:
                            AddDdlTarget(unit, modern.Object);
                            break;
                        case BackwardsCompatibleDropIndexClause legacy when legacy.Index is not null:
                            _handled.Add(legacy.Index);
                            AddDdlTarget(unit, PolicyNames.ParentOf(legacy.Index));
                            break;
                    }
                }

                break;
            case CreateStatisticsStatement statistics:
                unit.DefaultSource = AddDdlTarget(unit, statistics.OnName);
                break;
            case UpdateStatisticsStatement updateStatistics:
                unit.DefaultSource = AddDdlTarget(unit, updateStatistics.SchemaObjectName);
                break;
            case SequenceStatement sequence:
                AddDdlTarget(unit, sequence.Name);
                break;
            case CreateSynonymStatement synonym:
                AddDdlTarget(unit, synonym.Name);
                if (synonym.ForName is { } forName)
                {
                    _handled.Add(forName);
                    var target = PolicyNames.From(forName);
                    _ctx.AddObject(unit, target, ObjectRole.Other, "SYNONYM TARGET");
                    SynonymRules.CheckTarget(_ctx, target);
                }

                break;
            case CreateTypeStatement createType:
                AddDdlTarget(unit, createType.Name);
                break;
            case DropTypeStatement dropType:
                AddDdlTarget(unit, dropType.Name);
                break;
            case CreateFullTextIndexStatement fullText:
                unit.DefaultSource = AddDdlTarget(unit, fullText.OnName);
                break;
            case AlterFullTextIndexStatement alterFullText:
                unit.DefaultSource = AddDdlTarget(unit, alterFullText.OnName);
                break;
            case DropFullTextIndexStatement dropFullText:
                AddDdlTarget(unit, dropFullText.TableName);
                break;
            case DropSchemaStatement dropSchema when dropSchema.Schema is not null:
                _handled.Add(dropSchema.Schema);
                var schemaName = PolicyNames.Value(dropSchema.Schema.BaseIdentifier) ?? string.Empty;
                var schemaTarget = new SqlObjectName(null, null, null, schemaName);
                _ctx.AddObject(unit, schemaTarget, ObjectRole.DdlTarget, "SCHEMA");
                SetTarget(unit, schemaTarget);
                ReferenceRules.CheckDdlSchema(_ctx, schemaName);
                break;
            case AlterSchemaStatement alterSchema:
                var destination = PolicyNames.Value(alterSchema.Name);
                if (!string.IsNullOrEmpty(destination))
                {
                    ReferenceRules.CheckDdlSchema(_ctx, destination, $"esquema destino '{destination}'");
                }

                AddDdlTarget(unit, alterSchema.ObjectName);
                break;
        }
    }

    /// <summary>Registra un destino DDL, aplica las reglas de esquema y devuelve el nombre resuelto.</summary>
    private SqlObjectName? AddDdlTarget(StatementUnit unit, SchemaObjectName? schemaObject)
    {
        if (schemaObject is null)
        {
            return null;
        }

        _handled.Add(schemaObject);
        return AddDdlTarget(unit, PolicyNames.From(schemaObject));
    }

    private SqlObjectName? AddDdlTarget(StatementUnit unit, SqlObjectName name)
    {
        if (string.IsNullOrEmpty(name.Name))
        {
            return null;
        }

        if (string.IsNullOrEmpty(name.Schema) && unit.DefaultSchema is { } defaultSchema && !name.IsTemp)
        {
            name = name with { Schema = defaultSchema };
        }

        _ctx.AddObject(unit, name, ObjectRole.DdlTarget, unit.Class.ObjectType);
        SetTarget(unit, name);
        ReferenceRules.CheckDdlTarget(_ctx, name);
        return name;
    }

    /// <summary>Trigger DML: la tabla y el trigger (que vive en el esquema de su tabla) deben estar en esquemas de DDL.</summary>
    private void ProcessTrigger(StatementUnit unit, TriggerObject? triggerObject, IEnumerable<SchemaObjectName?> names)
    {
        var scope = triggerObject?.TriggerScope ?? TriggerScope.Normal;
        if (scope != TriggerScope.Normal)
        {
            // Triggers DDL / LOGON: bloqueados por el clasificador (POL-EXT-005); solo se marcan los nombres.
            foreach (var name in names)
            {
                if (name is not null)
                {
                    _handled.Add(name);
                }
            }

            return;
        }

        // El trigger es el destino principal (respaldo de su definición); la tabla también debe estar en un esquema de DDL.
        SqlObjectName? table = null;
        if (triggerObject?.Name is { } tableName)
        {
            _handled.Add(tableName);
            table = PolicyNames.From(tableName);
            if (string.IsNullOrEmpty(table.Schema) && unit.DefaultSchema is { } defaultSchema && !table.IsTemp)
            {
                table = table with { Schema = defaultSchema };
            }
        }

        foreach (var schemaObject in names)
        {
            if (schemaObject is null)
            {
                continue;
            }

            _handled.Add(schemaObject);
            var name = PolicyNames.From(schemaObject);
            if (string.IsNullOrEmpty(name.Schema) && table?.Schema is { Length: > 0 } tableSchema)
            {
                name = name with { Schema = tableSchema };
            }

            AddDdlTarget(unit, name);
        }

        if (table is not null)
        {
            AddDdlTarget(unit, table);
            unit.TriggerTable = table;
            unit.DefaultSource = table;
        }
    }

    private static void SetTarget(StatementUnit unit, SqlObjectName name) => unit.RegisterTarget(name);

    // =====================================================================================================
    // Profundidad (POL-PARSE-003): cadenas de operadores sin paréntesis que el pre-chequeo léxico no detecta.
    // =====================================================================================================

    public override void ExplicitVisit(BinaryExpression node) => Nested(node, base.ExplicitVisit);

    public override void ExplicitVisit(BooleanBinaryExpression node) => Nested(node, base.ExplicitVisit);

    public override void ExplicitVisit(BooleanNotExpression node) => Nested(node, base.ExplicitVisit);

    public override void ExplicitVisit(UnaryExpression node) => Nested(node, base.ExplicitVisit);

    public override void ExplicitVisit(BinaryQueryExpression node) => Nested(node, base.ExplicitVisit);

    public override void ExplicitVisit(QualifiedJoin node) => Nested(node, base.ExplicitVisit);

    public override void ExplicitVisit(UnqualifiedJoin node) => Nested(node, base.ExplicitVisit);

    private void Nested<T>(T node, Action<T> visitBase)
    {
        if (++_nesting > SqlParsing.MaxTreeDepth)
        {
            throw new PolicyComplexityException();
        }

        try
        {
            visitBase(node);
        }
        finally
        {
            _nesting--;
        }
    }

    // =====================================================================================================
    // Ámbitos de consulta y DML (resolución de alias, destinos de escritura)
    // =====================================================================================================

    public override void ExplicitVisit(QuerySpecification node)
    {
        var unit = CurrentUnit(node);
        var scope = new QueryScope(isDml: false);
        if (node.FromClause is { } from)
        {
            foreach (var reference in from.TableReferences)
            {
                CollectSources(reference, scope, unit);
            }

            // Tablas separadas por coma sin predicado (RS-LIM-04).
            if (from.TableReferences.Count > 1 && (node.WhereClause?.SearchCondition is null || TrivialPredicateDetector.IsTrivial(node.WhereClause.SearchCondition)))
            {
                _ctx.HasCartesianJoin = true;
            }
        }

        _scopes.Push(scope);
        try
        {
            base.ExplicitVisit(node);
        }
        finally
        {
            _scopes.Pop();
        }
    }

    public override void ExplicitVisit(InsertSpecification node) => VisitDml(node, null, null, () => base.ExplicitVisit(node));

    public override void ExplicitVisit(UpdateSpecification node) => VisitDml(node, node.FromClause, null, () => base.ExplicitVisit(node));

    public override void ExplicitVisit(DeleteSpecification node) => VisitDml(node, node.FromClause, null, () => base.ExplicitVisit(node));

    public override void ExplicitVisit(MergeSpecification node) =>
        VisitDml(node, null, node, () => base.ExplicitVisit(node));

    private void VisitDml(DataModificationSpecification node, FromClause? from, MergeSpecification? merge, Action visitBase)
    {
        var unit = CurrentUnit(node);
        var scope = new QueryScope(isDml: true);
        if (from is not null)
        {
            foreach (var reference in from.TableReferences)
            {
                CollectSources(reference, scope, unit);
            }
        }

        if (merge?.TableReference is { } usingSource)
        {
            CollectSources(usingSource, scope, unit);
        }

        var target = ResolveDmlTarget(node.Target, scope, unit, PolicyNames.Value(merge?.TableAlias));
        scope.Target = target;
        if (node.Target is NamedTableReference targetReference)
        {
            _writeTargets[targetReference] = target;
        }

        if (node.OutputIntoClause?.IntoTable is NamedTableReference outputInto)
        {
            _handled.Add(outputInto.SchemaObject);
            var intoName = PolicyNames.From(outputInto.SchemaObject);
            _writeTargets[outputInto] = new TableSource(PolicyNames.Value(outputInto.Alias) ?? intoName.Name, intoName);
        }

        if (unit?.Statement is DataModificationStatement statement && ReferenceEquals(SpecificationOf(statement), node))
        {
            if (target.Object is { } written)
            {
                SetTarget(unit, written);
            }
            else if (node.Target is NamedTableReference named)
            {
                SetTarget(unit, PolicyNames.From(named.SchemaObject));
            }

            unit.HasTop = node.TopRowFilter is not null;
            unit.HasOutput = node.OutputClause is not null || node.OutputIntoClause is not null;
            if (node is UpdateDeleteSpecificationBase updateDelete)
            {
                AnalyzeFilter(unit, updateDelete.WhereClause);
                if (unit.Unfiltered && unit.IsInsideTrigger && JoinsTriggerPseudoTable(from))
                {
                    // En un trigger, la unión interna con inserted/deleted limita las filas a las afectadas.
                    unit.Unfiltered = false;
                }
            }
        }

        _scopes.Push(scope);
        try
        {
            visitBase();
        }
        finally
        {
            _scopes.Pop();
        }
    }

    private static DataModificationSpecification? SpecificationOf(DataModificationStatement statement) => statement switch
    {
        InsertStatement insert => insert.InsertSpecification,
        UpdateStatement update => update.UpdateSpecification,
        DeleteStatement delete => delete.DeleteSpecification,
        MergeStatement merge => merge.MergeSpecification,
        _ => null,
    };

    /// <summary>UPDATE/DELETE sin WHERE o con predicado trivial (RF-DML-02, RS-POL-09, CA-10, CA-11).</summary>
    private static void AnalyzeFilter(StatementUnit unit, WhereClause? where)
    {
        unit.HasWhere = where is not null;
        if (where is null)
        {
            unit.Unfiltered = true;
            return;
        }

        if (where.SearchCondition is null)
        {
            // WHERE CURRENT OF cursor: filtra una sola fila.
            unit.Unfiltered = where.Cursor is null;
            return;
        }

        unit.TrivialPredicate = TrivialPredicateDetector.IsTrivial(where.SearchCondition);
    }

    /// <summary>FROM con un INNER JOIN (con condición real) hacia las pseudotablas inserted o deleted.</summary>
    private static bool JoinsTriggerPseudoTable(FromClause? from)
    {
        if (from is null)
        {
            return false;
        }

        foreach (var reference in from.TableReferences)
        {
            if (HasPseudoTableJoin(reference))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPseudoTableJoin(TableReference? reference) => reference switch
    {
        QualifiedJoin { QualifiedJoinType: QualifiedJoinType.Inner } join
            when join.SearchCondition is not null && !TrivialPredicateDetector.IsTrivial(join.SearchCondition)
                 && (IsPseudoTableReference(join.FirstTableReference) || IsPseudoTableReference(join.SecondTableReference)) => true,
        JoinTableReference join => HasPseudoTableJoin(join.FirstTableReference) || HasPseudoTableJoin(join.SecondTableReference),
        JoinParenthesisTableReference parenthesis => HasPseudoTableJoin(parenthesis.Join),
        _ => false,
    };

    private static bool IsPseudoTableReference(TableReference? reference) =>
        reference is NamedTableReference named
        && PolicyNames.IsOnePart(PolicyNames.From(named.SchemaObject))
        && IsPseudoTable(PolicyNames.Value(named.SchemaObject.BaseIdentifier) ?? string.Empty);

    private TableSource ResolveDmlTarget(TableReference? target, QueryScope scope, StatementUnit? unit, string? mergeAlias)
    {
        switch (target)
        {
            case NamedTableReference named:
                _handled.Add(named.SchemaObject);
                var name = PolicyNames.From(named.SchemaObject);
                var exposed = PolicyNames.Value(named.Alias) ?? mergeAlias ?? name.Name;
                if (PolicyNames.IsOnePart(name))
                {
                    // UPDATE t … FROM ventas.T t: el destino es el alias de una tabla del FROM.
                    if (scope.Find(name.Name) is { } source)
                    {
                        return new TableSource(exposed, source.Object);
                    }

                    if (unit?.FindCte(name.Name) is { } cte)
                    {
                        return new TableSource(exposed, cte.BaseObject);
                    }

                    if (unit is not null && unit.IsInsideTrigger && IsPseudoTable(name.Name))
                    {
                        return new TableSource(exposed, unit.TriggerTable);
                    }
                }

                return new TableSource(exposed, name);
            case VariableTableReference variable:
                return new TableSource(PolicyNames.Value(variable.Alias) ?? variable.Variable?.Name ?? string.Empty, null);
            case TableReferenceWithAlias aliased:
                return new TableSource(PolicyNames.Value(aliased.Alias) ?? mergeAlias ?? string.Empty, null);
            default:
                return new TableSource(mergeAlias ?? string.Empty, null);
        }
    }

    private static bool IsPseudoTable(string name) =>
        PolicyNames.EqualsIgnoreCase(name, "inserted") || PolicyNames.EqualsIgnoreCase(name, "deleted");

    private void CollectSources(TableReference? reference, QueryScope scope, StatementUnit? unit)
    {
        switch (reference)
        {
            case null:
                return;
            case JoinTableReference join:
                CollectSources(join.FirstTableReference, scope, unit);
                CollectSources(join.SecondTableReference, scope, unit);
                return;
            case JoinParenthesisTableReference parenthesis:
                CollectSources(parenthesis.Join, scope, unit);
                return;
            case OdbcQualifiedJoinTableReference odbc:
                CollectSources(odbc.TableReference, scope, unit);
                return;
            case NamedTableReference named:
                var name = PolicyNames.From(named.SchemaObject);
                var exposed = PolicyNames.Value(named.Alias) ?? name.Name;
                SqlObjectName? resolved = name;
                if (PolicyNames.IsOnePart(name))
                {
                    if (unit?.FindCte(name.Name) is not null)
                    {
                        // Las columnas de una CTE pueden estar renombradas: se comparan solo por nombre (conservador).
                        resolved = null;
                    }
                    else if (unit is not null && unit.IsInsideTrigger && IsPseudoTable(name.Name))
                    {
                        resolved = unit.TriggerTable;
                    }
                }

                scope.Sources.Add(new TableSource(exposed, resolved));
                return;
            case ChangeTableChangesTableReference changes:
                scope.Sources.Add(new TableSource(PolicyNames.Value(changes.Alias) ?? string.Empty, changes.Target is null ? null : PolicyNames.From(changes.Target)));
                return;
            case ChangeTableVersionTableReference version:
                scope.Sources.Add(new TableSource(PolicyNames.Value(version.Alias) ?? string.Empty, version.Target is null ? null : PolicyNames.From(version.Target)));
                return;
            case VariableTableReference variable:
                scope.Sources.Add(new TableSource(PolicyNames.Value(variable.Alias) ?? variable.Variable?.Name ?? string.Empty, null));
                return;
            case TableReferenceWithAlias aliased:
                scope.Sources.Add(new TableSource(PolicyNames.Value(aliased.Alias) ?? string.Empty, null));
                return;
        }
    }

    // =====================================================================================================
    // Orígenes de filas (FROM, JOIN, APPLY, funciones de tabla, acceso externo)
    // =====================================================================================================

    public override void Visit(TableReference node)
    {
        if (!KnownTableReferences.Contains(node.GetType()))
        {
            Hits.Add(PolicyRules.UnknownStatement, $"origen de filas no admitido ({node.GetType().Name})");
        }
    }

    public override void Visit(NamedTableReference node)
    {
        var unit = CurrentUnit(node);
        _handled.Add(node.SchemaObject);
        if (_writeTargets.TryGetValue(node, out var target))
        {
            if (target.Object is { } written)
            {
                _ctx.AddObject(unit, written, ObjectRole.Write, "TABLE");
                ReferenceRules.CheckObject(_ctx, written, ObjectRole.Write);
            }

            return;
        }

        var name = PolicyNames.From(node.SchemaObject);
        if (string.IsNullOrEmpty(name.Name))
        {
            return;
        }

        if (PolicyNames.IsOnePart(name))
        {
            if (unit?.FindCte(name.Name) is not null)
            {
                return;
            }

            if (unit is not null && unit.IsInsideTrigger && IsPseudoTable(name.Name))
            {
                return;
            }
        }

        _ctx.AddObject(unit, name, ObjectRole.Read, "TABLE");
        ReferenceRules.CheckObject(_ctx, name, ObjectRole.Read);
    }

    public override void Visit(SchemaObjectFunctionTableReference node)
    {
        var unit = CurrentUnit(node);
        _handled.Add(node.SchemaObject);
        var name = PolicyNames.From(node.SchemaObject);
        _ctx.AddFunction(PolicyNames.Display(name));
        _ctx.AddObject(unit, name, ObjectRole.Function, "FUNCTION");
        ReferenceRules.CheckObject(_ctx, name, ObjectRole.Function, isFunction: true);
    }

    public override void Visit(BuiltInFunctionTableReference node)
    {
        var name = PolicyNames.Value(node.Name) ?? string.Empty;
        _ctx.AddFunction("::" + name);
        var asSystem = new SqlObjectName(null, null, "sys", name);
        _ctx.AddObject(CurrentUnit(node), asSystem, ObjectRole.Function, "FUNCTION");
        ReferenceRules.CheckObject(_ctx, asSystem, ObjectRole.Function, isFunction: true);
    }

    public override void Visit(GlobalFunctionTableReference node)
    {
        var name = PolicyNames.Value(node.Name) ?? string.Empty;
        _ctx.AddFunction(name);
        if (Denylist.IsAllowedGlobalTableFunction(name))
        {
            return;
        }

        var asSystem = new SqlObjectName(null, null, "sys", name);
        _ctx.AddObject(CurrentUnit(node), asSystem, ObjectRole.Function, "FUNCTION");
        ReferenceRules.CheckObject(_ctx, asSystem, ObjectRole.Function, isFunction: true);
    }

    public override void Visit(ChangeTableChangesTableReference node) => ReadTable(node, node.Target);

    public override void Visit(ChangeTableVersionTableReference node) => ReadTable(node, node.Target);

    public override void Visit(FullTextTableReference node) => ReadTable(node, node.TableName);

    public override void Visit(SemanticTableReference node) => ReadTable(node, node.TableName);

    public override void Visit(OpenXmlTableReference node) => ReadTable(node, node.TableName);

    private void ReadTable(TSqlFragment node, SchemaObjectName? table)
    {
        if (table is null)
        {
            return;
        }

        _handled.Add(table);
        var name = PolicyNames.From(table);
        var unit = CurrentUnit(node);
        _ctx.AddObject(unit, name, ObjectRole.Read, "TABLE");
        ReferenceRules.CheckObject(_ctx, name, ObjectRole.Read);
    }

    public override void Visit(OpenRowsetTableReference node)
    {
        if (node.Object is not null)
        {
            _handled.Add(node.Object);
        }

        Hits.Add(PolicyRules.ExternalData, "OPENROWSET");
    }

    public override void Visit(BulkOpenRowset node) => Hits.Add(PolicyRules.ExternalData, "OPENROWSET(BULK …)");

    public override void Visit(OpenRowsetCosmos node) => Hits.Add(PolicyRules.ExternalData, "OPENROWSET (Cosmos DB)");

    public override void Visit(InternalOpenRowset node) => Hits.Add(PolicyRules.ExternalData, "OPENROWSET interno");

    public override void Visit(AdHocTableReference node)
    {
        if (node.Object?.SchemaObjectName is { } name)
        {
            _handled.Add(name);
        }

        Hits.Add(PolicyRules.ExternalData, "OPENDATASOURCE");
    }

    public override void Visit(OpenQueryTableReference node) =>
        Hits.Add(PolicyRules.ExternalData, $"OPENQUERY({PolicyNames.Value(node.LinkedServer)}, …)");

    public override void Visit(DataModificationTableReference node) => Hits.Add(PolicyRules.ComposableDml);

    public override void Visit(UnqualifiedJoin node)
    {
        if (node.UnqualifiedJoinType == UnqualifiedJoinType.CrossJoin)
        {
            _ctx.HasCartesianJoin = true;
        }
    }

    public override void Visit(QualifiedJoin node)
    {
        if (node.SearchCondition is null || TrivialPredicateDetector.IsTrivial(node.SearchCondition))
        {
            _ctx.HasCartesianJoin = true;
        }
    }

    // =====================================================================================================
    // Funciones (denylist, catálogo, llamadas externas)
    // =====================================================================================================

    public override void Visit(FunctionCall node)
    {
        var unit = CurrentUnit(node);
        var name = PolicyNames.Value(node.FunctionName) ?? string.Empty;
        switch (node.CallTarget)
        {
            case MultiPartIdentifierCallTarget multipart:
                var ids = multipart.MultiPartIdentifier?.Identifiers ?? [];
                var schema = ids.Count >= 1 ? PolicyNames.Value(ids[^1]) : null;
                var database = ids.Count >= 2 ? PolicyNames.Value(ids[^2]) : null;
                var server = ids.Count >= 3 ? PolicyNames.Value(ids[^3]) : null;
                var function = new SqlObjectName(
                    string.IsNullOrEmpty(server) ? null : server,
                    string.IsNullOrEmpty(database) ? null : database,
                    schema,
                    name);
                _ctx.AddFunction(PolicyNames.FunctionDisplay(database, schema, name));
                _ctx.AddObject(unit, function, ObjectRole.Function, "FUNCTION");
                if (Denylist.IsSensitiveFunction(name))
                {
                    Hits.Add(PolicyRules.SensitiveCatalog, $"'{PolicyNames.Display(function)}'");
                }
                else
                {
                    ReferenceRules.CheckObject(_ctx, function, ObjectRole.Function, isFunction: true);
                }

                break;
            case UserDefinedTypeCallTarget typeTarget:
                // Método estático de un tipo (geography::Point, hierarchyid::GetRoot): no es una referencia a objeto.
                if (typeTarget.SchemaObjectName is { } typeName)
                {
                    _handled.Add(typeName);
                    _ctx.AddFunction($"{PolicyNames.Value(typeName.BaseIdentifier)}::{name}");
                }

                break;
            case ExpressionCallTarget:
                _ctx.AddFunction("." + name);
                break;
            default:
                _ctx.AddFunction(name);
                if (Denylist.IsSensitiveFunction(name))
                {
                    Hits.Add(PolicyRules.SensitiveCatalog, $"función {name.ToUpperInvariant()}");
                }
                else if (CatalogScalarFunctions.Contains(name) && !Policy.AllowSystemCatalogQueries)
                {
                    Hits.Add(PolicyRules.SystemCatalog, $"función {name.ToUpperInvariant()}");
                }

                break;
        }
    }

    public override void Visit(NextValueForExpression node)
    {
        if (node.SequenceName is null)
        {
            return;
        }

        _handled.Add(node.SequenceName);
        var name = PolicyNames.From(node.SequenceName);
        _ctx.AddObject(CurrentUnit(node), name, ObjectRole.Other, "SEQUENCE");
        ReferenceRules.CheckObject(_ctx, name, ObjectRole.Other);
    }

    public override void Visit(InvokeExternalApiFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "INVOKE_EXTERNAL_API");

    public override void Visit(AIGenerateResponseFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_GENERATE_RESPONSE");

    public override void Visit(AIGenerateEmbeddingsFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_GENERATE_EMBEDDINGS");

    public override void Visit(AIAnalyzeSentimentFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_ANALYZE_SENTIMENT");

    public override void Visit(AIClassifyFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_CLASSIFY");

    public override void Visit(AIExtractFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_EXTRACT");

    public override void Visit(AIFixGrammarFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_FIX_GRAMMAR");

    public override void Visit(AISummarizeFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_SUMMARIZE");

    public override void Visit(AITranslateFunctionCall node) => Hits.Add(PolicyRules.ExternalData, "AI_TRANSLATE");

    // =====================================================================================================
    // Nombres de objetos: tipos de datos excluidos y red de seguridad para cualquier otra referencia
    // =====================================================================================================

    public override void Visit(DataTypeReference node)
    {
        if (node.Name is not null)
        {
            _handled.Add(node.Name);
        }

        if (node is XmlDataTypeReference { XmlSchemaCollection: { } collection })
        {
            _handled.Add(collection);
        }
    }

    public override void Visit(TableHintsOptimizerHint node)
    {
        // OPTION (TABLE HINT(alias, …)): nombre expuesto de una tabla ya evaluada en el FROM.
        if (node.ObjectName is not null)
        {
            _handled.Add(node.ObjectName);
        }
    }

    /// <summary>Red de seguridad: cualquier nombre de objeto no tratado por un manejador tipado (FK, CHANGETABLE, RESULT SETS…).</summary>
    public override void Visit(SchemaObjectName node)
    {
        if (_handled.Contains(node))
        {
            return;
        }

        var name = PolicyNames.From(node);
        if (string.IsNullOrEmpty(name.Name))
        {
            return;
        }

        var unit = CurrentUnit(node);
        if (PolicyNames.IsOnePart(name) && unit?.FindCte(name.Name) is not null)
        {
            return;
        }

        _ctx.AddObject(unit, name, ObjectRole.Other, null);
        ReferenceRules.CheckObject(_ctx, name, ObjectRole.Other);
    }

    // =====================================================================================================
    // Columnas (RS-DAT-01, RS-DAT-02) y SELECT * (RS-DAT-06)
    // =====================================================================================================

    public override void Visit(OutputClause node)
    {
        CollectOutputColumns(node.SelectColumns);
    }

    public override void Visit(OutputIntoClause node)
    {
        CollectOutputColumns(node.SelectColumns);
    }

    private void CollectOutputColumns(IList<SelectElement> elements)
    {
        var collector = new ColumnCollector();
        foreach (var element in elements)
        {
            element.Accept(collector);
            if (element is SelectStarExpression && _ctx.SensitivePatternsConfigured)
            {
                _ctx.AddWarning("OUTPUT con '*' puede devolver columnas sensibles; enumere solo las columnas necesarias (RS-DAT-06).");
            }
        }

        foreach (var column in collector.Columns)
        {
            _outputColumns.Add(column);
        }
    }

    public override void Visit(SelectStarExpression node)
    {
        _ctx.HasSelectStar = true;
        if (!ProtectedColumnsConfigured)
        {
            return;
        }

        if (InUnsafeStarRegion(node))
        {
            Hits.Add(PolicyRules.SensitiveColumnNotMaskable, "SELECT * en una posición donde las columnas se renombran, se combinan o se serializan");
            return;
        }

        if (_ctx.SensitivePatternsConfigured)
        {
            _ctx.AddWarning(_ctx.Profile.Config.Sensitive.Action == SensitiveColumnAction.Block
                ? "SELECT * puede incluir columnas sensibles: si el resultado contiene alguna, la consulta se rechazará. Enumere solo las columnas necesarias (RS-DAT-06)."
                : "SELECT * puede incluir columnas sensibles: se enmascaran por nombre en el resultado. Enumere solo las columnas necesarias (RS-DAT-06).");
        }
    }

    private bool InUnsafeStarRegion(TSqlFragment node)
    {
        foreach (var (first, last) in _unsafeStarRegions)
        {
            if (first <= node.FirstTokenIndex && node.LastTokenIndex <= last)
            {
                return true;
            }
        }

        return false;
    }

    public override void Visit(ColumnReferenceExpression node)
    {
        if (node.ColumnType != ColumnType.Regular || node.MultiPartIdentifier is not { Count: > 0 } identifier)
        {
            return;
        }

        var ids = identifier.Identifiers;
        var column = PolicyNames.Value(ids[^1]) ?? string.Empty;
        if (column.Length == 0)
        {
            return;
        }

        var unit = CurrentUnit(node);
        var (schema, table) = ResolveColumnSource(ids, unit);
        _topLevel.TryGetValue(node, out var top);
        var reference = new SqlColumnReference(column, schema, table, top is not null, top?.Ordinal);
        _ctx.AddColumn(unit, reference);
        CheckColumn(node, reference, top);
    }

    private (string? Schema, string? Table) ResolveColumnSource(IList<Identifier> ids, StatementUnit? unit)
    {
        SqlObjectName? source;
        if (ids.Count == 1)
        {
            if (_scopes.Count == 1)
            {
                var candidates = _scopes.Peek().Candidates();
                source = candidates.Count == 1 ? candidates[0].Object : null;
            }
            else if (_scopes.Count == 0)
            {
                source = DefaultSourceOf(unit);
            }
            else
            {
                // Subconsulta: la columna puede ser de un ámbito exterior; se compara solo por nombre (conservador).
                source = null;
            }
        }
        else if (ids.Count == 2)
        {
            var qualifier = PolicyNames.Value(ids[0]) ?? string.Empty;
            source = ResolveQualifier(qualifier, unit);
        }
        else
        {
            var database = ids.Count >= 4 ? PolicyNames.Value(ids[^4]) : null;
            source = new SqlObjectName(null, string.IsNullOrEmpty(database) ? null : database, PolicyNames.Value(ids[^3]), PolicyNames.Value(ids[^2]) ?? string.Empty);
        }

        return source is null ? (null, null) : (PolicyNames.EffectiveSchema(source.Schema), source.Name);
    }

    private SqlObjectName? ResolveQualifier(string qualifier, StatementUnit? unit)
    {
        if (IsPseudoTable(qualifier))
        {
            foreach (var scope in _scopes)
            {
                if (scope.IsDml && scope.Target is not null)
                {
                    return scope.Target.Object;
                }
            }

            if (unit is not null && unit.IsInsideTrigger)
            {
                return unit.TriggerTable;
            }
        }

        foreach (var scope in _scopes)
        {
            if (scope.Find(qualifier) is { } source)
            {
                return source.Object;
            }
        }

        if (unit?.FindCte(qualifier) is not null)
        {
            return null;
        }

        return new SqlObjectName(null, null, null, qualifier);
    }

    private static SqlObjectName? DefaultSourceOf(StatementUnit? unit)
    {
        for (var u = unit; u is not null; u = u.Parent)
        {
            if (u.DefaultSource is not null)
            {
                return u.DefaultSource;
            }
        }

        return null;
    }

    private void CheckColumn(ColumnReferenceExpression node, SqlColumnReference reference, TopLevelColumn? top)
    {
        var profile = _ctx.Profile;
        var detail = reference.Table is null ? $"'{reference.Column}'" : $"'{reference.Schema}.{reference.Table}.{reference.Column}'";

        var (excluded, sensitive, pii) = _ctx.ColumnProtection(reference.Schema, reference.Table, reference.Column);
        if (excluded)
        {
            Hits.Add(PolicyRules.ExcludedColumn, detail);
        }

        if (!sensitive)
        {
            if (pii)
            {
                MaskPersonalData(detail, top);
            }

            return;
        }

        if (profile.Config.Sensitive.Action == SensitiveColumnAction.Block)
        {
            Hits.Add(PolicyRules.SensitiveColumnBlocked, detail);
        }
        else if (_outputColumns.Contains(node))
        {
            Hits.Add(PolicyRules.SensitiveColumnOutput, detail);
        }
        else if (top is null)
        {
            Hits.Add(PolicyRules.SensitiveColumnInference, detail);
        }
        else if (_topLevelForJsonXml)
        {
            Hits.Add(PolicyRules.SensitiveColumnNotMaskable, $"{detail} con FOR JSON/XML");
        }
        else if (top.Ordinal is { } ordinal)
        {
            _ctx.MaskOrdinals.Add(ordinal);
            _ctx.AddWarning($"La columna sensible {detail} se devolverá enmascarada (posición {ordinal + 1}).");
        }
        else if (top.Aliased)
        {
            Hits.Add(PolicyRules.SensitiveColumnNotMaskable, $"{detail} con alias '{top.OutputName}' después de '*'");
        }
    }

    /// <summary>
    /// Datos personales con acción "mask": se enmascaran en la lista SELECT principal. Pueden usarse en filtros, uniones y
    /// escrituras (buscar por correo es legítimo); solo se bloquea cuando no se puede garantizar el enmascaramiento.
    /// </summary>
    private void MaskPersonalData(string detail, TopLevelColumn? top)
    {
        if (top is null)
        {
            return;
        }

        if (_topLevelForJsonXml)
        {
            Hits.Add(PolicyRules.SensitiveColumnNotMaskable, $"dato personal {detail} con FOR JSON/XML");
        }
        else if (top.Ordinal is { } ordinal)
        {
            _ctx.MaskOrdinals.Add(ordinal);
            _ctx.AddWarning($"El dato personal {detail} se devolverá enmascarado (posición {ordinal + 1}).");
        }
        else if (top.Aliased)
        {
            Hits.Add(PolicyRules.SensitiveColumnNotMaskable, $"dato personal {detail} con alias '{top.OutputName}' después de '*'");
        }
    }

    // =====================================================================================================
    // Opciones de módulos (RF-DDL-03) y DCL embebido en definiciones de columnas
    // =====================================================================================================

    public override void Visit(ExecuteAsClause node)
    {
        if (node.ExecuteAsOption != ExecuteAsOption.Caller)
        {
            var who = node.ExecuteAsOption == ExecuteAsOption.String && node.Literal is StringLiteral literal
                ? $"'{literal.Value}'"
                : node.ExecuteAsOption.ToString().ToUpperInvariant();
            Hits.Add(PolicyRules.ImpersonationModule, $"WITH EXECUTE AS {who}");
        }
    }

    public override void Visit(ProcedureOption node)
    {
        if (node.OptionKind == ProcedureOptionKind.Encryption)
        {
            EncryptionOption("procedimiento");
        }
    }

    public override void Visit(FunctionOption node)
    {
        if (node.OptionKind == FunctionOptionKind.Encryption)
        {
            EncryptionOption("función");
        }
    }

    public override void Visit(TriggerOption node)
    {
        if (node.OptionKind == TriggerOptionKind.Encryption)
        {
            EncryptionOption("trigger");
        }
    }

    public override void Visit(ViewOption node)
    {
        if (node.OptionKind == ViewOptionKind.Encryption)
        {
            EncryptionOption("vista");
        }
    }

    private void EncryptionOption(string module)
    {
        if (!Policy.AllowEncryptedModules)
        {
            Hits.Add(PolicyRules.DdlEncryption, $"WITH ENCRYPTION en {module}");
        }
    }

    public override void Visit(ColumnDefinition node)
    {
        if (node.IsMasked || node.MaskingFunction is not null)
        {
            Hits.Add(PolicyRules.DclGovernance, $"MASKED WITH en la columna '{PolicyNames.Value(node.ColumnIdentifier)}'");
        }
    }

    // =====================================================================================================
    // EXEC: procedimientos, SQL dinámico, servidores vinculados, cambio de contexto (RS-DCL-01, sección 3)
    // =====================================================================================================

    public override void Visit(ExecuteSpecification node)
    {
        var unit = CurrentUnit(node);
        var isStatementSpec = unit?.Statement is ExecuteStatement statement && ReferenceEquals(statement.ExecuteSpecification, node);

        if (node.LinkedServer is not null)
        {
            Hits.Add(PolicyRules.ExternalLinkedServer, $"EXEC … AT {PolicyNames.Value(node.LinkedServer)}");
        }

        if (node.ExecuteContext is not null)
        {
            Hits.Add(PolicyRules.ImpersonationExecContext, $"EXEC … AS {node.ExecuteContext.Kind.ToString().ToUpperInvariant()}");
        }

        switch (node.ExecutableEntity)
        {
            case ExecutableStringList list:
                HandleDynamicSql(unit, isStatementSpec, "EXEC('…')", list.Strings.Cast<ScalarExpression>().ToList());
                break;
            case ExecutableProcedureReference procedure:
                HandleProcedureReference(unit, isStatementSpec, procedure);
                break;
        }
    }

    private void HandleProcedureReference(StatementUnit? unit, bool isStatementSpec, ExecutableProcedureReference procedure)
    {
        if (procedure.AdHocDataSource is not null)
        {
            Hits.Add(PolicyRules.ExternalData, "OPENDATASOURCE(…) en EXEC");
            return;
        }

        if (procedure.ProcedureReference?.ProcedureVariable is { } variable)
        {
            // EXEC @procedimiento: el nombre del procedimiento no se conoce hasta la ejecución.
            if (Policy.DynamicSql == PolicyAction.Block)
            {
                Hits.Add(PolicyRules.DynamicSqlBlocked, $"EXEC {variable.Name}");
            }
            else
            {
                Hits.Add(PolicyRules.DynamicSqlOpaque, $"EXEC {variable.Name}");
            }

            return;
        }

        if (procedure.ProcedureReference?.ProcedureReference?.Name is not { } schemaObject)
        {
            Hits.Add(PolicyRules.UnknownStatement, "EXEC sin nombre de procedimiento");
            return;
        }

        _handled.Add(schemaObject);
        var name = PolicyNames.From(schemaObject);
        _ctx.AddObject(unit, name, ObjectRole.Execute, "PROCEDURE");
        var special = isStatementSpec && unit is not null ? unit.Class.Special : SpecialProcedure.None;
        if (isStatementSpec && unit is not null && special is not (SpecialProcedure.Rename or SpecialProcedure.ExtendedProperty))
        {
            // En sp_rename y propiedades extendidas el destino es el objeto indicado en los argumentos.
            SetTarget(unit, name);
        }

        if (name.Server is not null)
        {
            Hits.Add(PolicyRules.ExternalLinkedServer, $"'{PolicyNames.Display(name)}'");
            return;
        }

        if (Denylist.IsExecuteSql(name.Name))
        {
            if (ReferenceRules.CheckDatabase(_ctx, name, $"'{PolicyNames.Display(name)}'"))
            {
                var statementText = procedure.Parameters
                    .Where(p => p.Variable is null || PolicyNames.EqualsIgnoreCase(p.Variable.Name, "@stmt"))
                    .Select(p => p.ParameterValue)
                    .FirstOrDefault();
                HandleDynamicSql(unit, isStatementSpec, "sp_executesql", statementText is null ? [] : [statementText]);
            }

            return;
        }

        switch (special)
        {
            case SpecialProcedure.Rename when unit is not null:
                SpecialProcedureRules.CheckRename(_ctx, unit, procedure);
                return;
            case SpecialProcedure.ExtendedProperty when unit is not null:
                SpecialProcedureRules.CheckExtendedProperty(_ctx, unit, procedure, name);
                return;
        }

        var decision = ProcedureRules.Check(_ctx, name, out var detail);
        if (isStatementSpec && unit is not null)
        {
            unit.ProcedureDecision = decision;
            unit.ProcedureDecisionDetail = detail;
        }
    }

    /// <summary>
    /// SQL dinámico (sección 3, CA-04): BLOQUEAR por defecto (POL-DYN-001). Con la política en CONFIRMAR solo se admite
    /// si todas las piezas son literales de texto: se concatenan, se analizan recursivamente con todas las reglas y la
    /// acción mínima es CONFIRMAR (POL-DYN-002). Cualquier parte no literal se bloquea siempre (POL-DYN-003).
    /// </summary>
    private void HandleDynamicSql(StatementUnit? unit, bool isStatementSpec, string source, IReadOnlyList<ScalarExpression?> pieces)
    {
        if (Policy.DynamicSql == PolicyAction.Block)
        {
            Hits.Add(PolicyRules.DynamicSqlBlocked, source);
            return;
        }

        if (pieces.Count == 0 || pieces.Any(p => p is not StringLiteral))
        {
            Hits.Add(PolicyRules.DynamicSqlOpaque, $"{source} con variables, expresiones o sin texto literal");
            return;
        }

        if (_dynamicDepth + 1 > MaxDynamicDepth)
        {
            Hits.Add(PolicyRules.DynamicSqlOpaque, $"{source}: SQL dinámico anidado en exceso");
            return;
        }

        var text = string.Concat(pieces.Cast<StringLiteral>().Select(p => p.Value));
        var script = SqlParsing.Parse(text, out var errors);
        if (errors.Count > 0 || script is null)
        {
            var first = errors.Count > 0 ? errors[0] : null;
            Hits.Add(PolicyRules.DynamicSqlOpaque, first is null
                ? $"{source}: el literal no es T-SQL válido"
                : $"{source}: el literal no es T-SQL válido (línea {first.Line}, columna {first.Column} del literal)");
            return;
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (script.Batches.Count != 1 || statements.Count == 0)
        {
            Hits.Add(PolicyRules.DynamicSqlOpaque, $"{source}: el literal está vacío o contiene separadores GO");
            return;
        }

        var inner = new SqlAnalyzer(_ctx, AnalyzerMode.Dynamic, unit, _dynamicDepth + 1);
        foreach (var statement in statements)
        {
            inner.Analyze(statement);
        }

        if (isStatementSpec && unit is not null)
        {
            unit.DynamicLiteralAnalyzed = true;
        }
        else
        {
            // SQL dinámico literal dentro de otra sentencia (INSERT … EXEC('…')): piso CONFIRMAR.
            Hits.Add(PolicyRules.DynamicSqlLiteral, source, PolicyDecision.Max(Policy.DynamicSql, PolicyAction.Confirm));
        }
    }

    // =====================================================================================================
    // Visitantes auxiliares
    // =====================================================================================================

    /// <summary>Recolecta las columnas de un fragmento (cláusula OUTPUT).</summary>
    private sealed class ColumnCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> Columns { get; } = [];

        public override void Visit(ColumnReferenceExpression node) => Columns.Add(node);
    }

    /// <summary>
    /// Pre-recorrido: regiones donde un SELECT * no conserva los nombres de columna en el resultado (y por tanto no se
    /// puede enmascarar o excluir por nombre en tiempo de ejecución, RS-DAT-02): ramas no iniciales de UNION/EXCEPT/
    /// INTERSECT, CTE y tablas derivadas con lista de columnas, INSERT … SELECT, subconsultas escalares o IN, cursores,
    /// vistas con lista de columnas, OUTPUT … INTO y consultas FOR JSON/XML. EXISTS no devuelve columnas.
    /// </summary>
    private sealed class PrePass : TSqlFragmentVisitor
    {
        private readonly HashSet<ScalarSubquery> _exists = new(ReferenceEqualityComparer.Instance);

        public List<(int First, int Last)> UnsafeStarRegions { get; } = [];

        public override void Visit(BinaryQueryExpression node) => Add(node.SecondQueryExpression);

        public override void Visit(QueryDerivedTable node)
        {
            if (node.Columns.Count > 0)
            {
                Add(node.QueryExpression);
            }
        }

        public override void Visit(CommonTableExpression node)
        {
            if (node.Columns.Count > 0)
            {
                Add(node.QueryExpression);
            }
        }

        public override void Visit(SelectInsertSource node) => Add(node.Select);

        public override void Visit(ExistsPredicate node)
        {
            if (node.Subquery is not null)
            {
                _exists.Add(node.Subquery);
            }
        }

        public override void Visit(ScalarSubquery node)
        {
            if (!_exists.Contains(node))
            {
                Add(node);
            }
        }

        public override void Visit(CursorDefinition node) => Add(node.Select);

        public override void Visit(ViewStatementBody node)
        {
            if (node.Columns.Count > 0)
            {
                Add(node.SelectStatement);
            }
        }

        public override void Visit(OutputIntoClause node)
        {
            foreach (var column in node.SelectColumns)
            {
                Add(column);
            }
        }

        public override void Visit(QueryExpression node)
        {
            if (node.ForClause is JsonForClause or XmlForClause)
            {
                Add(node);
            }
        }

        private void Add(TSqlFragment? fragment)
        {
            if (fragment is not null && fragment.FirstTokenIndex >= 0)
            {
                UnsafeStarRegions.Add((fragment.FirstTokenIndex, fragment.LastTokenIndex));
            }
        }
    }
}
