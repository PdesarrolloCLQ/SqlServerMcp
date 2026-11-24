using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Estado compartido de un análisis (una invocación de <see cref="PolicyEngine.Evaluate"/>): coincidencias,
/// objetos, columnas y unidades de sentencia (RS-POL-05, RS-POL-11). No se comparte entre hilos.
/// </summary>
internal sealed class AnalysisContext
{
    private readonly HashSet<string> _objectKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _columnKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _functionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warningKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Excluded, bool Sensitive, bool Pii)> _columnProtection = new(StringComparer.OrdinalIgnoreCase);

    public AnalysisContext(ProfileRuntime profile, ToolKind tool)
    {
        Profile = profile;
        Tool = tool;
    }

    public ProfileRuntime Profile { get; }

    public ToolKind Tool { get; }

    /// <summary>
    /// Análisis de la definición de un módulo existente antes de ejecutarlo: el CREATE raíz no se va a ejecutar, así que no
    /// se aplican las reglas de su destino DDL (esquema de DDL, acción de creación); el cuerpo se analiza completo.
    /// </summary>
    public bool ModuleInspection { get; init; }

    public HitSink Hits { get; } = new();

    public List<SqlObjectReference> Objects { get; } = [];

    public List<SqlColumnReference> Columns { get; } = [];

    public List<string> Functions { get; } = [];

    public List<string> NestedStatementTypes { get; } = [];

    public List<string> Warnings { get; } = [];

    public SortedSet<int> MaskOrdinals { get; } = [];

    /// <summary>Unidades (sentencia raíz y anidadas, incluido el SQL dinámico literal) en orden de visita.</summary>
    public List<StatementUnit> Units { get; } = [];

    public bool HasSelectStar { get; set; }

    public bool HasCartesianJoin { get; set; }

    public bool SensitivePatternsConfigured => Profile.Config.Sensitive.Patterns.Count > 0;

    /// <summary>PII configurada con acción "mask": se enmascara en el resultado, sin restringir filtros ni escrituras.</summary>
    public bool PiiMaskConfigured => Profile.MasksPii;

    public bool ExcludedColumnsConfigured => Profile.Config.Excluded.Columns.Count > 0;

    /// <summary>Registra un objeto referenciado en el análisis y en la unidad y sus ancestros (reglas configurables).</summary>
    public void AddObject(StatementUnit? unit, SqlObjectName name, ObjectRole role, string? objectType)
    {
        var reference = new SqlObjectReference(name, role, objectType);
        if (_objectKeys.Add($"{(int)role}|{name}"))
        {
            Objects.Add(reference);
        }

        for (var u = unit; u is not null; u = u.Parent)
        {
            u.Objects.Add(reference);
        }
    }

    public void AddColumn(StatementUnit? unit, SqlColumnReference column)
    {
        if (_columnKeys.Add($"{column.Schema}|{column.Table}|{column.Column}|{column.InSelectList}|{column.OutputOrdinal}"))
        {
            Columns.Add(column);
        }

        for (var u = unit; u is not null; u = u.Parent)
        {
            u.Columns.Add(column);
        }
    }

    /// <summary>
    /// Columna excluida (RS-DAT-01) y/o sensible (RS-DAT-02), memorizada por análisis: una sentencia suele repetir
    /// las mismas columnas muchas veces y la comparación con patrones es la parte costosa.
    /// </summary>
    public (bool Excluded, bool Sensitive, bool Pii) ColumnProtection(string? schema, string? table, string column)
    {
        var key = $"{schema}|{table}|{column}";
        if (!_columnProtection.TryGetValue(key, out var result))
        {
            result = (
                ExcludedColumnsConfigured && ReferenceRules.IsColumnExcluded(Profile, schema, table, column),
                SensitivePatternsConfigured && ReferenceRules.IsColumnSensitive(Profile, schema, table, column),
                PiiMaskConfigured && ReferenceRules.IsColumnPii(Profile, schema, table, column));
            _columnProtection[key] = result;
        }

        return result;
    }

    public void AddFunction(string name)
    {
        if (_functionKeys.Add(name))
        {
            Functions.Add(name);
        }
    }

    public void AddWarning(string warning)
    {
        if (_warningKeys.Add(warning))
        {
            Warnings.Add(warning);
        }
    }
}

/// <summary>Sentencia (raíz o anidada) con su clasificación y los datos necesarios para las reglas configurables.</summary>
internal sealed class StatementUnit
{
    public StatementUnit(TSqlStatement statement, StatementUnit? parent, StatementClass cls, bool isRoot, bool inModule)
    {
        Statement = statement;
        Parent = parent;
        Class = cls;
        IsRoot = isRoot;
        InModule = inModule;
        Depth = parent is null ? 0 : parent.Depth + 1;
        First = statement.FirstTokenIndex;
        Last = statement.LastTokenIndex;
        DefaultSchema = parent?.DefaultSchemaForChildren;
        TriggerTable = parent?.TriggerTable;
    }

    public TSqlStatement Statement { get; }

    public StatementUnit? Parent { get; }

    public StatementClass Class { get; set; }

    /// <summary>Sentencia raíz del texto recibido (aplica la allowlist de la herramienta y define la categoría).</summary>
    public bool IsRoot { get; }

    /// <summary>
    /// True si la sentencia está dentro del cuerpo de un procedimiento, función o trigger. Fuera de un módulo
    /// ("texto libre", incluido el SQL dinámico) se aplican las reglas de transacciones, SET y control de flujo.
    /// </summary>
    public bool InModule { get; }

    public bool IsFreeText => !InModule;

    public int Depth { get; }

    public int First { get; }

    public int Last { get; }

    /// <summary>Objeto destino principal (DDL, DML o EXEC), tal como se escribió (alias resueltos).</summary>
    public SqlObjectName? Target { get; set; }

    public int TargetCount { get; set; }

    public bool HasNonTempTarget { get; set; }

    /// <summary>Todos los destinos son tablas temporales de sesión (#tabla).</summary>
    public bool AllTargetsTemp => TargetCount > 0 && !HasNonTempTarget;

    /// <summary>UPDATE/DELETE sin WHERE.</summary>
    public bool Unfiltered { get; set; }

    /// <summary>UPDATE/DELETE con predicado trivial.</summary>
    public bool TrivialPredicate { get; set; }

    public bool HasWhere { get; set; }

    public bool HasTop { get; set; }

    public bool HasOutput { get; set; }

    /// <summary>Esquema por defecto de las sentencias anidadas (CREATE SCHEMA … CREATE TABLE t).</summary>
    public string? DefaultSchema { get; }

    public string? DefaultSchemaForChildren { get; set; }

    /// <summary>Tabla del trigger (para inserted/deleted en el cuerpo).</summary>
    public SqlObjectName? TriggerTable { get; set; }

    /// <summary>Tabla a la que pertenecen las columnas sin calificar de una sentencia DDL (CREATE INDEX, ALTER TABLE…).</summary>
    public SqlObjectName? DefaultSource { get; set; }

    /// <summary>CTE definidas por esta sentencia.</summary>
    public List<CteInfo> Ctes { get; } = [];

    /// <summary>Decisión de la lista de procedimientos (EXEC de un procedimiento permitido).</summary>
    public PolicyRule? ProcedureDecision { get; set; }

    public string? ProcedureDecisionDetail { get; set; }

    /// <summary>SQL dinámico literal analizado (habilita POL-DYN-002).</summary>
    public bool DynamicLiteralAnalyzed { get; set; }

    public List<SqlObjectReference> Objects { get; } = [];

    public List<SqlColumnReference> Columns { get; } = [];

    public bool Contains(TSqlFragment node) =>
        First < 0 || node.FirstTokenIndex < 0 || (First <= node.FirstTokenIndex && node.LastTokenIndex <= Last);

    /// <summary>Registra un destino (el primero es el principal) y si es una tabla temporal de sesión.</summary>
    public void RegisterTarget(SqlObjectName name)
    {
        Target ??= name;
        TargetCount++;
        if (!PolicyNames.IsLocalTemp(name.Name))
        {
            HasNonTempTarget = true;
        }
    }

    /// <summary>CTE visible con ese nombre (solo las de la propia sentencia: una CTE no se ve en otras sentencias).</summary>
    public CteInfo? FindCte(string name)
    {
        foreach (var cte in Ctes)
        {
            if (PolicyNames.EqualsIgnoreCase(cte.Name, name))
            {
                return cte;
            }
        }

        return null;
    }

    /// <summary>True si la sentencia es un trigger o está en el cuerpo de uno (inserted/deleted son pseudotablas).</summary>
    public bool IsInsideTrigger
    {
        get
        {
            for (var u = this; u is not null; u = u.Parent)
            {
                if (u.Statement is TriggerStatementBody)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>CTE visible en una sentencia.</summary>
/// <param name="Name">Nombre de la CTE.</param>
/// <param name="BaseObject">Tabla base cuando la CTE es una consulta simple sobre una sola tabla (destino real de un DML sobre la CTE).</param>
internal sealed record CteInfo(string Name, SqlObjectName? BaseObject);

/// <summary>Fuente de filas visible en una consulta o DML (tabla, alias, CTE, tabla derivada…).</summary>
/// <param name="ExposedName">Nombre con el que se califica (alias o nombre base).</param>
/// <param name="Object">Tabla o vista base resuelta (null si es derivada, CTE, función o desconocida).</param>
internal sealed record TableSource(string ExposedName, SqlObjectName? Object);

/// <summary>Ámbito de resolución de alias (QuerySpecification o especificación DML).</summary>
internal sealed class QueryScope
{
    public QueryScope(bool isDml)
    {
        IsDml = isDml;
    }

    public bool IsDml { get; }

    public List<TableSource> Sources { get; } = [];

    /// <summary>Destino de la especificación DML (para inserted./deleted. en OUTPUT y columnas SET).</summary>
    public TableSource? Target { get; set; }

    public TableSource? Find(string exposedName)
    {
        foreach (var s in Sources)
        {
            if (PolicyNames.EqualsIgnoreCase(s.ExposedName, exposedName))
            {
                return s;
            }
        }

        if (Target is not null && PolicyNames.EqualsIgnoreCase(Target.ExposedName, exposedName))
        {
            return Target;
        }

        return null;
    }

    /// <summary>Fuentes candidatas para una columna sin calificar (el destino DML cuenta una sola vez).</summary>
    public List<TableSource> Candidates()
    {
        var list = new List<TableSource>(Sources);
        if (Target is not null && !list.Any(s => SameObject(s.Object, Target.Object) || PolicyNames.EqualsIgnoreCase(s.ExposedName, Target.ExposedName)))
        {
            list.Add(Target);
        }

        return list;
    }

    private static bool SameObject(SqlObjectName? a, SqlObjectName? b) =>
        a is not null && b is not null
        && PolicyNames.EqualsIgnoreCase(a.Name, b.Name)
        && PolicyNames.EqualsIgnoreCase(PolicyNames.EffectiveSchema(a.Schema), PolicyNames.EffectiveSchema(b.Schema))
        && PolicyNames.EqualsIgnoreCase(a.Database, b.Database);
}

/// <summary>Columna de la lista SELECT principal (candidata a enmascarar, RS-DAT-02).</summary>
/// <param name="Ordinal">Posición 0-based en el resultado, si se puede determinar (sin '*' antes).</param>
/// <param name="Aliased">La columna tiene alias (el enmascaramiento por nombre no sería fiable).</param>
/// <param name="OutputName">Nombre de la columna en el resultado (alias o nombre de la columna).</param>
internal sealed record TopLevelColumn(int? Ordinal, bool Aliased, string OutputName);
