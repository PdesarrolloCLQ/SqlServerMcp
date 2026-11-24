using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Execution;

/// <summary>Login con el que se abre la conexión (RF-CON-07: separación de privilegios).</summary>
public enum ConnectionRole
{
    Read = 0,
    Write = 1,
}

/// <summary>Formato del texto devuelto al agente (RF-QRY-11). El contenido estructurado siempre es JSON.</summary>
public enum OutputFormat
{
    Json = 0,
    Markdown = 1,
    Csv = 2,
}

/// <summary>Parámetro con nombre recibido del agente (RF-QRY-02). El valor nunca se concatena al SQL.</summary>
/// <param name="Name">Nombre con o sin "@".</param>
/// <param name="Type">Tipo SQL opcional ("int", "nvarchar(50)", "decimal(18,2)", "datetime2", "uniqueidentifier"…).</param>
/// <param name="Value">Valor JSON (string, número, booleano o null).</param>
/// <param name="Sensitive">Si es true, el valor no se audita (RS-DAT-05).</param>
public sealed record QueryParameter(string Name, string? Type, JsonElement Value, bool Sensitive = false);

/// <summary>Metadatos de una columna de resultado (RF-QRY-05).</summary>
public sealed record ResultColumn(string Name, string SqlType, bool Nullable, bool Masked = false);

/// <summary>
/// Resultado tabular ya serializado de forma estable: los valores son null, string, long, double o bool
/// (DECIMAL como texto, fechas ISO 8601, binarios Base64 truncados, uniqueidentifier como texto).
/// </summary>
public sealed class TabularResult
{
    public List<ResultColumn> Columns { get; init; } = [];

    public List<object?[]> Rows { get; init; } = [];

    public bool Truncated { get; set; }

    /// <summary>"maxRows", "maxResponseBytes" o null.</summary>
    public string? TruncationReason { get; set; }

    /// <summary>Celdas recortadas por <see cref="LimitsConfig.MaxCellChars"/>.</summary>
    public int TruncatedCells { get; set; }

    /// <summary>Tamaño aproximado (bytes UTF-8) de las filas serializadas.</summary>
    public long ApproximateBytes { get; set; }
}

/// <summary>Abre conexiones configuradas por perfil (RF-CON-01..11).</summary>
public interface ISqlConnectionFactory
{
    /// <summary>
    /// Abre una conexión con el login del rol indicado y la prepara: SESSION_CONTEXT con el identificador de
    /// correlación (RF-CON-08), SET LOCK_TIMEOUT, SET XACT_ABORT ON y nivel de aislamiento del perfil.
    /// Si no hay login de escritura separado, <see cref="ConnectionRole.Write"/> usa la conexión principal.
    /// </summary>
    Task<SqlConnection> OpenAsync(ProfileRuntime profile, ConnectionRole role, string correlationId, CancellationToken cancellationToken);

    /// <summary>Descripción segura del destino ("servidor/base"), sin credenciales.</summary>
    string DescribeTarget(ProfileRuntime profile);
}

/// <summary>Convierte valores de SqlDataReader en primitivos JSON estables y aplica límites de celda/tamaño.</summary>
public interface IResultReader
{
    /// <summary>
    /// Lee el conjunto de resultados actual del lector aplicando <paramref name="maxRows"/>, el tamaño
    /// máximo de respuesta, el largo máximo por celda y el enmascaramiento (ordinales + nombres sensibles).
    /// Debe detenerse al alcanzar el límite sin leer el resto (CA-20).
    /// </summary>
    Task<TabularResult> ReadAsync(
        SqlDataReader reader,
        ProfileRuntime profile,
        int maxRows,
        int skipRows,
        IReadOnlyCollection<int> maskOrdinals,
        CancellationToken cancellationToken);
}

/// <summary>Construye SqlParameter tipados a partir de <see cref="QueryParameter"/> (RF-QRY-02, RF-DML-08).</summary>
public interface IParameterBinder
{
    /// <summary>Lanza <see cref="ToolFailureException"/> con INVALID_ARGUMENT si el nombre, tipo o valor no son válidos.</summary>
    SqlParameter Bind(QueryParameter parameter);

    /// <summary>Parámetro tipado según el tipo de catálogo de una columna o parámetro de procedimiento.</summary>
    SqlParameter BindForCatalogType(string parameterName, CatalogType type, JsonElement value);
}

/// <summary>Tipo de dato tal como lo describe el catálogo (sys.types + longitud/precisión/escala).</summary>
public sealed record CatalogType(string TypeName, short MaxLength, byte Precision, byte Scale, bool IsUserDefined = false, string? BaseTypeName = null)
{
    /// <summary>Declaración T-SQL del tipo, por ejemplo "nvarchar(50)", "decimal(18,2)", "varbinary(max)".</summary>
    public string Declaration
    {
        get
        {
            var t = (BaseTypeName ?? TypeName).ToLowerInvariant();
            return t switch
            {
                "varchar" or "char" or "varbinary" or "binary" => $"{t}({(MaxLength == -1 ? "max" : MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture))})",
                "nvarchar" or "nchar" => $"{t}({(MaxLength == -1 ? "max" : (MaxLength / 2).ToString(System.Globalization.CultureInfo.InvariantCulture))})",
                "decimal" or "numeric" => $"{t}({Precision},{Scale})",
                "datetime2" or "datetimeoffset" or "time" => $"{t}({Scale})",
                _ => t,
            };
        }
    }
}

public sealed class QueryExecutionRequest
{
    public required ProfileRuntime Profile { get; init; }

    public required string Sql { get; init; }

    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    public required int MaxRows { get; init; }

    public int SkipRows { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Evaluación de política ya aprobada (aporta ordinales a enmascarar).</summary>
    public required PolicyEvaluation Evaluation { get; init; }
}

public sealed class QueryExecutionResult
{
    public required TabularResult Result { get; init; }

    public List<string> Warnings { get; init; } = [];
}

/// <summary>Ejecuta consultas de lectura con el login de lector (RF-QRY-01..13).</summary>
public interface IQueryExecutor
{
    Task<QueryExecutionResult> ExecuteQueryAsync(QueryExecutionRequest request, CancellationToken cancellationToken);
}

public sealed class ExecutionPlanResult
{
    /// <summary>Costo estimado del subárbol de la sentencia (StatementSubTreeCost).</summary>
    public double? EstimatedCost { get; init; }

    public double? EstimatedRows { get; init; }

    public List<PlanOperator> CostlyOperators { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public List<MissingIndexSuggestion> MissingIndexes { get; init; } = [];

    /// <summary>XML del plan (recortado al tamaño máximo de respuesta).</summary>
    public string? PlanXml { get; init; }

    public bool PlanXmlTruncated { get; init; }
}

public sealed record PlanOperator(int NodeId, string PhysicalOp, string LogicalOp, double EstimatedSubtreeCost, double CostPercent, double EstimatedRows, string? Object);

public sealed record MissingIndexSuggestion(double Impact, string Table, IReadOnlyList<string> EqualityColumns, IReadOnlyList<string> InequalityColumns, IReadOnlyList<string> IncludeColumns, string CreateStatement);

/// <summary>Plan de ejecución estimado mediante SET SHOWPLAN_XML ON (RF-QRY-09).</summary>
public interface IExecutionPlanService
{
    Task<ExecutionPlanResult> GetEstimatedPlanAsync(
        ProfileRuntime profile,
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        string correlationId,
        bool includeXml,
        CancellationToken cancellationToken);
}

public sealed class DmlExecutionRequest
{
    public required ProfileRuntime Profile { get; init; }

    public required string Sql { get; init; }

    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    public required PolicyEvaluation Evaluation { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Filas aprobadas por el usuario (null si no hubo confirmación).</summary>
    public long? ApprovedRows { get; init; }

    /// <summary>Vista previa: ejecuta y siempre revierte (RF-DML-09).</summary>
    public bool Preview { get; init; }
}

public sealed class DmlExecutionResult
{
    public long RowsAffected { get; init; }

    public bool Committed { get; init; }

    public bool RolledBack { get; init; }

    /// <summary>Motivo de la reversión: "preview", "LIMIT_EXCEEDED", "error".</summary>
    public string? RollbackReason { get; init; }

    /// <summary>Filas de la cláusula OUTPUT, si la sentencia la incluye (RF-DML-06).</summary>
    public TabularResult? Output { get; init; }

    public List<string> Warnings { get; init; } = [];
}

/// <summary>Ejecuta DML con transacción gestionada, estimación y umbrales (RF-DML-01..10).</summary>
public interface IDmlExecutor
{
    /// <summary>
    /// Estima las filas afectadas: COUNT_BIG con el mismo origen y predicado para UPDATE/DELETE (RF-DML-03);
    /// número de filas de VALUES para INSERT … VALUES; ejecución de prueba revertida para INSERT … SELECT y MERGE.
    /// </summary>
    Task<long?> EstimateAffectedRowsAsync(DmlExecutionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Ejecuta la sentencia en una transacción del MCP (o en un punto de guardado de la transacción explícita activa).
    /// Revierte y devuelve <c>RollbackReason = LIMIT_EXCEEDED</c> si las filas reales superan el máximo del perfil
    /// o las aprobadas más el margen.
    /// </summary>
    Task<DmlExecutionResult> ExecuteAsync(DmlExecutionRequest request, CancellationToken cancellationToken);

    /// <summary>Inserción estructurada (RF-DML-07). Valida columnas contra el catálogo y usa parámetros o SqlBulkCopy.</summary>
    Task<DmlExecutionResult> InsertRowsAsync(InsertRowsRequest request, CancellationToken cancellationToken);
}

public sealed class InsertRowsRequest
{
    public required ProfileRuntime Profile { get; init; }

    public required SqlObjectName Table { get; init; }

    public required IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Devolver las filas insertadas (OUTPUT inserted.*), limitado al máximo de filas.</summary>
    public bool ReturnInserted { get; init; }
}

public sealed class ProcedureArgument
{
    public JsonElement Value { get; init; }

    /// <summary>Marcar como parámetro de salida (OUTPUT).</summary>
    public bool Output { get; init; }

    /// <summary>No auditar el valor.</summary>
    public bool Sensitive { get; init; }
}

public sealed class ProcedureExecutionRequest
{
    public required ProfileRuntime Profile { get; init; }

    public required SqlObjectName Procedure { get; init; }

    public IReadOnlyDictionary<string, ProcedureArgument> Arguments { get; init; } = new Dictionary<string, ProcedureArgument>();

    public required PolicyEvaluation Evaluation { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Lector para procedimientos de solo lectura; escritor para los que tienen efectos.</summary>
    public ConnectionRole Role { get; init; } = ConnectionRole.Write;
}

public sealed class ProcedureExecutionResult
{
    public int? ReturnValue { get; init; }

    public Dictionary<string, object?> OutputParameters { get; init; } = [];

    public List<TabularResult> ResultSets { get; init; } = [];

    public long RowsAffected { get; init; }

    public bool Committed { get; init; }

    public List<string> Warnings { get; init; } = [];
}

/// <summary>Ejecuta procedimientos almacenados autorizados (RF-PRC-01..04).</summary>
public interface IProcedureExecutor
{
    Task<ProcedureExecutionResult> ExecuteAsync(ProcedureExecutionRequest request, CancellationToken cancellationToken);
}

public sealed class DdlImpact
{
    /// <summary>Si el objeto destino existe actualmente.</summary>
    public bool TargetExists { get; init; }

    public string? TargetObjectType { get; init; }

    /// <summary>Filas aproximadas (DROP TABLE, TRUNCATE, DROP COLUMN) desde sys.dm_db_partition_stats.</summary>
    public long? ApproximateRows { get; init; }

    /// <summary>Objetos que dependen del destino (RF-DDL-07).</summary>
    public List<string> Dependents { get; init; } = [];

    /// <summary>Definición actual del objeto (módulos: OBJECT_DEFINITION; tablas: descripción JSON).</summary>
    public string? CurrentDefinition { get; init; }
}

public sealed class DdlExecutionResult
{
    public bool Executed { get; init; }

    public bool Committed { get; init; }

    public DdlImpact? Impact { get; init; }

    /// <summary>Identificador del registro de cambio de esquema con la definición anterior (RF-DDL-05).</summary>
    public string? SchemaChangeId { get; init; }

    public List<string> Warnings { get; init; } = [];
}

/// <summary>Ejecuta DDL sobre objetos permitidos con respaldo previo de la definición (RF-DDL-01..10).</summary>
public interface IDdlExecutor
{
    /// <summary>Impacto estimado sin ejecutar: existencia, filas aproximadas, dependientes y definición actual.</summary>
    Task<DdlImpact> AnalyzeImpactAsync(ProfileRuntime profile, PolicyEvaluation evaluation, string correlationId, CancellationToken cancellationToken);

    /// <summary>Respalda la definición previa en el registro de cambios, ejecuta en transacción y confirma.</summary>
    Task<DdlExecutionResult> ExecuteAsync(ProfileRuntime profile, PolicyEvaluation evaluation, string sql, DdlImpact? impact, string correlationId, CancellationToken cancellationToken);
}

public sealed record TransactionInfo(string TransactionId, string Profile, DateTimeOffset StartedAtUtc, int StatementCount, int IdleTimeoutSeconds);

/// <summary>Una conexión y transacción explícita activa de la sesión (RF-DML-10).</summary>
public sealed class ActiveTransaction
{
    public required string TransactionId { get; init; }

    public required ProfileRuntime Profile { get; init; }

    public required SqlConnection Connection { get; init; }

    public required SqlTransaction Transaction { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int StatementCount { get; set; }

    private long _affectedRows;

    /// <summary>Filas afectadas acumuladas por las sentencias ejecutadas en la transacción (umbrales sobre el acumulado).</summary>
    public long AffectedRows => Interlocked.Read(ref _affectedRows);

    /// <summary>Suma filas afectadas y devuelve el nuevo acumulado.</summary>
    public long AddAffectedRows(long rows) => rows > 0 ? Interlocked.Add(ref _affectedRows, rows) : AffectedRows;

    /// <summary>Serializa el uso de la conexión (una sentencia a la vez).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Transacciones explícitas: una por sesión, reversión automática por inactividad y al cerrar (RS-LIM-03).
/// </summary>
public interface ITransactionManager
{
    /// <summary>Transacción activa para el perfil, o null.</summary>
    ActiveTransaction? GetActive(ProfileRuntime profile);

    Task<TransactionInfo> BeginAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken);

    Task<TransactionInfo> CommitAsync(ProfileRuntime profile, CancellationToken cancellationToken);

    Task<TransactionInfo> RollbackAsync(ProfileRuntime profile, string reason, CancellationToken cancellationToken);

    /// <summary>Marca actividad para reiniciar el temporizador de inactividad.</summary>
    void Touch(ProfileRuntime profile);

    /// <summary>Revierte todas las transacciones abiertas (cierre del proceso).</summary>
    Task RollbackAllAsync(string reason);
}
