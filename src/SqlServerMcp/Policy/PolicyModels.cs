using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>Herramienta que va a ejecutar la sentencia; define la allowlist de tipos raíz (RS-POL-04).</summary>
public enum ToolKind
{
    /// <summary>run_query: solo SELECT sin INTO.</summary>
    Query = 0,

    /// <summary>get_execution_plan: igual que Query.</summary>
    ExecutionPlan = 1,

    /// <summary>execute_dml: INSERT, UPDATE, DELETE, MERGE.</summary>
    Dml = 2,

    /// <summary>execute_ddl: CREATE/ALTER/DROP de objetos permitidos, TRUNCATE, estadísticas, triggers DML, propiedades extendidas, sp_rename.</summary>
    Ddl = 3,

    /// <summary>validate_sql sin herramienta destino: se clasifica y se aplica la política global, sin allowlist de herramienta.</summary>
    Any = 4,
}

/// <summary>Categoría de la sentencia (sección 3 del ERS).</summary>
public enum StatementCategory
{
    Unknown = 0,
    Query,
    SelectInto,
    Insert,
    Update,
    Delete,
    Merge,
    DdlCreate,
    DdlAlter,
    DdlDrop,
    DdlTruncate,
    DdlDestructiveAlter,
    DdlTrigger,
    Statistics,
    ExtendedProperty,
    ExecuteProcedure,
    DynamicSql,
    TransactionControl,
    Dcl,
    Impersonation,
    Administration,
    ExternalAccess,
    ContextChange,
    Wait,
    ProceduralControl,
    SessionOption,
    Cursor,
    Other,
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public enum ObjectRole
{
    /// <summary>Objeto leído (FROM, JOIN, subconsulta, MERGE USING…).</summary>
    Read = 0,

    /// <summary>Destino de escritura (INSERT/UPDATE/DELETE/MERGE, OUTPUT INTO).</summary>
    Write = 1,

    /// <summary>Objeto creado, modificado o eliminado por DDL.</summary>
    DdlTarget = 2,

    /// <summary>Procedimiento ejecutado.</summary>
    Execute = 3,

    /// <summary>Función de usuario o de sistema invocada.</summary>
    Function = 4,

    /// <summary>Otra referencia (por ejemplo, destino de un sinónimo, tabla referenciada por una FK).</summary>
    Other = 5,
}

public sealed record SqlParseError(int Number, int Line, int Column, string Message);

public sealed record SqlObjectReference(SqlObjectName Name, ObjectRole Role, string? ObjectType = null);

/// <summary>Referencia a columna encontrada en el AST.</summary>
/// <param name="Column">Nombre de la columna (normalizado).</param>
/// <param name="Schema">Esquema de la tabla de origen si se pudo resolver.</param>
/// <param name="Table">Tabla de origen si se pudo resolver (alias resuelto).</param>
/// <param name="InSelectList">True si aparece directamente como elemento del SELECT (posiblemente con alias).</param>
/// <param name="OutputOrdinal">Posición de la columna en el resultado (0-based) cuando se puede determinar.</param>
public sealed record SqlColumnReference(string Column, string? Schema, string? Table, bool InSelectList, int? OutputOrdinal);

/// <summary>Una regla evaluada que coincidió con la sentencia (explicabilidad, RS-POL-11).</summary>
public sealed record RuleHit(
    string RuleId,
    PolicyAction Action,
    string Reason,
    string? Suggestion = null,
    AlertSeverity? Alert = null,
    bool Immutable = false);

/// <summary>Resultado del análisis sintáctico y semántico de una sentencia.</summary>
public sealed class SqlStatementInfo
{
    public bool Parsed { get; init; }

    public IReadOnlyList<SqlParseError> ParseErrors { get; init; } = [];

    public int BatchCount { get; init; }

    public int StatementCount { get; init; }

    /// <summary>Nombre del tipo ScriptDom de la sentencia raíz (por ejemplo "UpdateStatement").</summary>
    public string? StatementType { get; init; }

    public StatementCategory Category { get; init; }

    /// <summary>Operación legible: SELECT, INSERT, CREATE, ALTER, DROP, TRUNCATE, EXECUTE…</summary>
    public string? Operation { get; init; }

    /// <summary>Tipo de objeto para DDL: TABLE, VIEW, PROCEDURE, FUNCTION, TRIGGER, INDEX, SCHEMA, SEQUENCE, SYNONYM, TYPE, STATISTICS…</summary>
    public string? ObjectType { get; init; }

    /// <summary>Objeto destino principal (DML/DDL/EXEC), si aplica.</summary>
    public SqlObjectName? TargetObject { get; init; }

    public IReadOnlyList<SqlObjectReference> Objects { get; init; } = [];

    public IReadOnlyList<SqlColumnReference> Columns { get; init; } = [];

    /// <summary>Funciones invocadas (nombres normalizados, con prefijo de esquema si lo tenían).</summary>
    public IReadOnlyList<string> Functions { get; init; } = [];

    /// <summary>Tipos de sentencias anidadas (cuerpos de módulos, bloques, SQL dinámico literal analizado).</summary>
    public IReadOnlyList<string> NestedStatementTypes { get; init; } = [];

    public bool HasSelectStar { get; init; }

    public bool HasWhereClause { get; init; }

    public bool HasTrivialPredicate { get; init; }

    public bool HasTopClause { get; init; }

    public bool HasOutputClause { get; init; }

    /// <summary>JOIN sin condición o tablas separadas por coma sin predicado (RS-LIM-04).</summary>
    public bool HasCartesianJoin { get; init; }

    /// <summary>Sentencia normalizada (generada desde el AST) usada para auditoría y para el hash de confirmación.</summary>
    public string NormalizedSql { get; init; } = string.Empty;

    /// <summary>SHA-256 hexadecimal de <see cref="NormalizedSql"/>.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Fragmento AST de la sentencia raíz (Microsoft.SqlServer.TransactSql.ScriptDom.TSqlStatement).</summary>
    public object? Fragment { get; init; }
}

/// <summary>Decisión del motor de políticas para una sentencia en un perfil y una herramienta.</summary>
public sealed class PolicyEvaluation
{
    public required SqlStatementInfo Statement { get; init; }

    public required ToolKind Tool { get; init; }

    /// <summary>Acción final (la más restrictiva entre las reglas que coincidieron).</summary>
    public required PolicyAction Action { get; set; }

    /// <summary>Regla que determinó la acción final.</summary>
    public required string RuleId { get; set; }

    public required string Reason { get; set; }

    public string? Suggestion { get; set; }

    public RiskLevel Risk { get; set; }

    /// <summary>Severidad de alerta a emitir (null = ninguna).</summary>
    public AlertSeverity? Alert { get; set; }

    /// <summary>Todas las reglas que coincidieron, en orden de evaluación.</summary>
    public List<RuleHit> Hits { get; init; } = [];

    /// <summary>Advertencias no bloqueantes (por ejemplo, SELECT * sobre tabla con columnas sensibles).</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>Posiciones (0-based) de columnas del resultado que deben enmascararse.</summary>
    public List<int> MaskOrdinals { get; init; } = [];

    /// <summary>Código de error a devolver si se bloquea (null = POLICY_BLOCKED; por ejemplo LIMIT_EXCEEDED).</summary>
    public string? ErrorCode { get; set; }

    public bool IsBlocked => Action == PolicyAction.Block;

    public bool RequiresConfirmation => Action == PolicyAction.Confirm;
}

/// <summary>Hechos de ejecución disponibles después del análisis estático (filas y costo estimados).</summary>
/// <param name="TransactionRows">Filas ya afectadas en la transacción explícita abierta (null = fuera de transacción).</param>
public sealed record RuntimeFacts(long? EstimatedRows, double? EstimatedCost, long? TableRows = null, long? TransactionRows = null);

/// <summary>Motor de políticas (sección 3 y RS-POL).</summary>
public interface IPolicyEngine
{
    /// <summary>
    /// Analiza y evalúa una sentencia en texto libre para una herramienta y un perfil.
    /// Nunca lanza excepciones por SQL inválido: devuelve BLOCK (fail-closed, RS-POL-02).
    /// </summary>
    PolicyEvaluation Evaluate(string sql, ToolKind tool, ProfileRuntime profile);

    /// <summary>
    /// Reevalúa con hechos de ejecución (filas/costo estimados): umbral de confirmación de DML,
    /// máximo de filas afectadas, reglas configurables con MinEstimatedRows / MinEstimatedCost.
    /// Nunca relaja la acción previa.
    /// </summary>
    PolicyEvaluation ApplyRuntimeFacts(PolicyEvaluation evaluation, RuntimeFacts facts, ProfileRuntime profile);

    /// <summary>
    /// Evalúa la ejecución estructurada de un procedimiento almacenado (execute_procedure, RF-PRC-02/03).
    /// </summary>
    PolicyEvaluation EvaluateProcedure(SqlObjectName procedure, ProfileRuntime profile);

    /// <summary>
    /// Analiza la definición (CREATE PROCEDURE …) de un procedimiento existente antes de ejecutarlo, con las mismas reglas
    /// que execute_ddl aplica al cuerpo. No aplica las reglas del destino DDL porque el CREATE no se ejecuta.
    /// </summary>
    PolicyEvaluation EvaluateModule(string definition, ProfileRuntime profile);
}
