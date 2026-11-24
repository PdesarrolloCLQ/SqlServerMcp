using System.Text.Json.Serialization;

namespace SqlServerMcp.Metadata;

// DTO de las herramientas de metadatos. Están pensados para serializarse como structuredContent MCP
// (camelCase con las opciones JSON del SDK). Los textos que provienen de la base de datos (nombres,
// descripciones, definiciones) solo aparecen en campos de datos, nunca en descripciones de herramientas
// ni en mensajes (RF-TOOL-04).

/// <summary>Página de resultados de una herramienta de metadatos (RF-MET-02: paginación).</summary>
/// <typeparam name="T">Tipo de cada elemento.</typeparam>
/// <param name="Database">Base de datos consultada.</param>
/// <param name="Items">Elementos de la página.</param>
/// <param name="TotalCount">Total de elementos visibles que cumplen el filtro.</param>
/// <param name="Offset">Desplazamiento aplicado.</param>
/// <param name="Limit">Tamaño de página aplicado (1..500).</param>
/// <param name="HasMore">Indica si hay más elementos después de esta página.</param>
public sealed record MetadataPage<T>(string Database, IReadOnlyList<T> Items, int TotalCount, int Offset, int Limit, bool HasMore);

/// <summary>Resultado de test_connection (RF-CON-01).</summary>
/// <param name="ProductVersion">SERVERPROPERTY('ProductVersion'), por ejemplo "16.0.4315.2".</param>
/// <param name="ProductLevel">SERVERPROPERTY('ProductLevel') (RTM, SP1…).</param>
/// <param name="Edition">SERVERPROPERTY('Edition').</param>
/// <param name="EngineEdition">SERVERPROPERTY('EngineEdition').</param>
/// <param name="EngineEditionDescription">Plataforma: SQL Server, Azure SQL Database, Azure SQL Managed Instance…</param>
/// <param name="CurrentDatabase">Base de datos actual de la conexión.</param>
/// <param name="LoginName">Login con el que se conectó el perfil (lectura).</param>
/// <param name="ServerUtcTime">Hora UTC del servidor.</param>
/// <param name="CompatibilityLevel">Nivel de compatibilidad de la base actual.</param>
/// <param name="SnapshotIsolationAllowed">ALLOW_SNAPSHOT_ISOLATION activado en la base actual (RF-QRY-12).</param>
/// <param name="ReadCommittedSnapshot">READ_COMMITTED_SNAPSHOT activado en la base actual.</param>
/// <param name="IsReadOnly">La base es de solo lectura (DATABASEPROPERTYEX Updateability = READ_ONLY, por ejemplo réplica secundaria).</param>
/// <param name="Collation">Intercalación de la base actual.</param>
public sealed record ConnectionTestResult(
    string ProductVersion,
    string? ProductLevel,
    string? Edition,
    int EngineEdition,
    string EngineEditionDescription,
    string CurrentDatabase,
    string? LoginName,
    DateTimeOffset ServerUtcTime,
    int? CompatibilityLevel,
    bool SnapshotIsolationAllowed,
    bool ReadCommittedSnapshot,
    bool IsReadOnly,
    string? Collation);

/// <summary>Base de datos permitida y accesible (RF-MET-01).</summary>
public sealed record DatabaseInfo(
    string Name,
    string State,
    int CompatibilityLevel,
    bool IsReadOnly,
    string SnapshotIsolationState,
    bool ReadCommittedSnapshot,
    string? Collation,
    bool IsCurrent);

/// <summary>Lista de bases de datos (RF-MET-01).</summary>
public sealed record DatabaseListResult(IReadOnlyList<DatabaseInfo> Databases);

/// <summary>Esquema visible con el número de objetos visibles por tipo (list_schemas).</summary>
public sealed record SchemaInfo(string Name, int Tables, int Views, int Procedures, int Functions, int Sequences, int Synonyms);

/// <summary>Lista de esquemas visibles (list_schemas).</summary>
public sealed record SchemaListResult(string Database, IReadOnlyList<SchemaInfo> Schemas);

/// <summary>Tabla o vista en list_tables (RF-MET-02).</summary>
/// <param name="Schema">Esquema.</param>
/// <param name="Name">Nombre.</param>
/// <param name="Type">"TABLE" o "VIEW".</param>
/// <param name="ApproximateRows">Filas aproximadas (sys.partitions); null en vistas no indexadas.</param>
/// <param name="TemporalType">"SYSTEM_VERSIONED", "HISTORY" o null.</param>
/// <param name="Description">Propiedad extendida MS_Description.</param>
public sealed record TableSummary(string Schema, string Name, string Type, long? ApproximateRows, string? TemporalType, string? Description);

/// <summary>Columna de una tabla o vista (RF-MET-03).</summary>
/// <param name="Ordinal">column_id.</param>
/// <param name="Name">Nombre.</param>
/// <param name="DataType">Declaración T-SQL del tipo ("nvarchar(100)", "decimal(18,4)", "ventas.Telefono").</param>
/// <param name="BaseType">Declaración del tipo base cuando <paramref name="DataType"/> es un tipo de usuario.</param>
/// <param name="Length">Longitud en caracteres (texto) o bytes (binarios); -1 = max; null si no aplica.</param>
/// <param name="Precision">Precisión de decimal/numeric.</param>
/// <param name="Scale">Escala de decimal/numeric o fracción de segundo de datetime2/datetimeoffset/time.</param>
/// <param name="IsNullable">Admite NULL.</param>
/// <param name="DefaultConstraint">Nombre de la restricción DEFAULT.</param>
/// <param name="DefaultDefinition">Expresión DEFAULT.</param>
/// <param name="IsIdentity">Columna IDENTITY.</param>
/// <param name="IdentitySeed">Semilla IDENTITY.</param>
/// <param name="IdentityIncrement">Incremento IDENTITY.</param>
/// <param name="IsComputed">Columna calculada.</param>
/// <param name="ComputedDefinition">Expresión de la columna calculada (null si se oculta por exclusiones).</param>
/// <param name="IsPersisted">Columna calculada persistida.</param>
/// <param name="Collation">Intercalación (columnas de texto).</param>
/// <param name="IsSparse">Columna dispersa (SPARSE).</param>
/// <param name="IsRowGuidCol">ROWGUIDCOL.</param>
/// <param name="IsRowVersion">rowversion/timestamp.</param>
/// <param name="GeneratedAlways">Columna de periodo de tabla temporal ("AS_ROW_START"/"AS_ROW_END") o null.</param>
/// <param name="IsHidden">Columna oculta (HIDDEN).</param>
/// <param name="Description">MS_Description de la columna.</param>
/// <param name="IsSensitive">Sensible por configuración o por clasificación de SQL Server (RS-DAT-02).</param>
/// <param name="IsPii">Dato personal (correo, teléfono, documento…); se enmascara si la acción PII del perfil es "mask".</param>
public sealed record ColumnDescription(
    int Ordinal,
    string Name,
    string DataType,
    string? BaseType,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsNullable,
    string? DefaultConstraint,
    string? DefaultDefinition,
    bool IsIdentity,
    long? IdentitySeed,
    long? IdentityIncrement,
    bool IsComputed,
    string? ComputedDefinition,
    bool IsPersisted,
    string? Collation,
    bool IsSparse,
    bool IsRowGuidCol,
    bool IsRowVersion,
    string? GeneratedAlways,
    bool IsHidden,
    string? Description,
    bool IsSensitive,
    bool IsPii);

/// <summary>Clave primaria o restricción UNIQUE (RF-MET-03).</summary>
/// <param name="Name">Nombre de la restricción.</param>
/// <param name="Columns">Columnas en orden de clave.</param>
/// <param name="IsClustered">Índice subyacente agrupado.</param>
public sealed record KeyConstraintInfo(string Name, IReadOnlyList<string> Columns, bool IsClustered);

/// <summary>Clave foránea saliente de una tabla (RF-MET-03).</summary>
public sealed record ForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete,
    string OnUpdate,
    bool IsDisabled,
    bool IsTrusted);

/// <summary>Restricción CHECK (RF-MET-03).</summary>
/// <param name="Name">Nombre.</param>
/// <param name="Definition">Expresión.</param>
/// <param name="Column">Columna, si es una restricción de columna.</param>
/// <param name="IsDisabled">Deshabilitada.</param>
/// <param name="IsTrusted">Validada por el motor (WITH CHECK).</param>
public sealed record CheckConstraintInfo(string Name, string Definition, string? Column, bool IsDisabled, bool IsTrusted);

/// <summary>Columna clave de un índice.</summary>
/// <param name="Name">Columna.</param>
/// <param name="Ordinal">Posición en la clave (1..n).</param>
/// <param name="Direction">"ASC" o "DESC".</param>
public sealed record IndexKeyColumn(string Name, int Ordinal, string Direction);

/// <summary>Índice de una tabla o vista indexada (RF-MET-08).</summary>
/// <param name="Name">Nombre.</param>
/// <param name="Type">sys.indexes.type_desc (CLUSTERED, NONCLUSTERED, CLUSTERED COLUMNSTORE…).</param>
/// <param name="IsUnique">Único.</param>
/// <param name="IsPrimaryKey">Soporta la clave primaria.</param>
/// <param name="IsUniqueConstraint">Soporta una restricción UNIQUE.</param>
/// <param name="KeyColumns">Columnas clave en orden.</param>
/// <param name="IncludedColumns">Columnas incluidas (INCLUDE).</param>
/// <param name="FilterDefinition">Predicado de índice filtrado.</param>
/// <param name="IsDisabled">Deshabilitado.</param>
/// <param name="FillFactor">Factor de relleno (0 = predeterminado).</param>
/// <param name="SizeKb">Espacio usado en KB (sys.allocation_units).</param>
public sealed record IndexInfo(
    string Name,
    string Type,
    bool IsUnique,
    bool IsPrimaryKey,
    bool IsUniqueConstraint,
    IReadOnlyList<IndexKeyColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    string? FilterDefinition,
    bool IsDisabled,
    int FillFactor,
    long? SizeKb);

/// <summary>Información de tabla temporal versionada por el sistema.</summary>
/// <param name="Type">"SYSTEM_VERSIONED" o "HISTORY".</param>
/// <param name="PeriodStartColumn">Columna de inicio del periodo.</param>
/// <param name="PeriodEndColumn">Columna de fin del periodo.</param>
/// <param name="HistorySchema">Esquema de la tabla de historial (null si no es visible).</param>
/// <param name="HistoryTable">Tabla de historial (null si no es visible).</param>
public sealed record TemporalInfo(string Type, string? PeriodStartColumn, string? PeriodEndColumn, string? HistorySchema, string? HistoryTable);

/// <summary>Estructura completa de una tabla o vista (describe_table, RF-MET-03).</summary>
public sealed record TableDescription(
    string Database,
    string Schema,
    string Name,
    string Type,
    string? Description,
    long? ApproximateRows,
    DateTime CreateDate,
    DateTime ModifyDate,
    IReadOnlyList<ColumnDescription> Columns,
    KeyConstraintInfo? PrimaryKey,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<KeyConstraintInfo> UniqueConstraints,
    IReadOnlyList<CheckConstraintInfo> CheckConstraints,
    IReadOnlyList<IndexInfo> Indexes,
    TemporalInfo? Temporal);

/// <summary>Procedimiento o función en list_routines (RF-MET-04).</summary>
/// <param name="Schema">Esquema.</param>
/// <param name="Name">Nombre.</param>
/// <param name="Type">"procedure", "scalar_function" o "table_function".</param>
/// <param name="TypeDescription">sys.objects.type_desc.</param>
/// <param name="IsEncrypted">Módulo cifrado (WITH ENCRYPTION).</param>
/// <param name="CreateDate">Fecha de creación (hora del servidor).</param>
/// <param name="ModifyDate">Fecha de modificación (hora del servidor).</param>
/// <param name="Description">MS_Description.</param>
public sealed record RoutineSummary(string Schema, string Name, string Type, string TypeDescription, bool IsEncrypted, DateTime CreateDate, DateTime ModifyDate, string? Description);

/// <summary>Parámetro de una rutina (RF-MET-04).</summary>
/// <param name="Ordinal">parameter_id.</param>
/// <param name="Name">Nombre con "@".</param>
/// <param name="DataType">Declaración del tipo.</param>
/// <param name="Direction">"IN" o "OUT" (en T-SQL un parámetro OUTPUT es de entrada/salida).</param>
/// <param name="HasDefault">Tiene valor por defecto.</param>
/// <param name="DefaultValue">
/// Valor por defecto tal como aparece en la definición. SQL Server no expone los valores por defecto de rutinas T-SQL
/// en el catálogo (sys.parameters.has_default_value siempre es 0), por lo que se obtienen analizando léxicamente la
/// definición; null si el módulo está cifrado o no tiene valor por defecto.
/// </param>
/// <param name="IsReadOnly">Parámetro READONLY (tipos tabla).</param>
/// <param name="IsTableValued">El tipo es un tipo tabla.</param>
public sealed record RoutineParameterInfo(int Ordinal, string Name, string DataType, string Direction, bool HasDefault, string? DefaultValue, bool IsReadOnly, bool IsTableValued);

/// <summary>Columna devuelta por una función de tabla o columna de un tipo tabla.</summary>
public sealed record ResultColumnInfo(int Ordinal, string Name, string DataType, bool IsNullable);

/// <summary>Descripción de una rutina (describe_routine, RF-MET-04).</summary>
/// <param name="Database">Base de datos.</param>
/// <param name="Schema">Esquema.</param>
/// <param name="Name">Nombre.</param>
/// <param name="Type">"procedure", "scalar_function" o "table_function".</param>
/// <param name="TypeDescription">sys.objects.type_desc.</param>
/// <param name="Parameters">Parámetros en orden.</param>
/// <param name="ReturnType">Tipo devuelto por una función escalar.</param>
/// <param name="Columns">Columnas devueltas por una función de tabla.</param>
/// <param name="IsEncrypted">Módulo cifrado: sin valores por defecto de parámetros.</param>
/// <param name="CreateDate">Fecha de creación.</param>
/// <param name="ModifyDate">Fecha de modificación.</param>
/// <param name="Description">MS_Description.</param>
public sealed record RoutineDescription(
    string Database,
    string Schema,
    string Name,
    string Type,
    string TypeDescription,
    IReadOnlyList<RoutineParameterInfo> Parameters,
    string? ReturnType,
    IReadOnlyList<ResultColumnInfo> Columns,
    bool IsEncrypted,
    DateTime CreateDate,
    DateTime ModifyDate,
    string? Description);

/// <summary>Definición de un módulo (get_object_definition, RF-MET-05).</summary>
/// <param name="Database">Base de datos.</param>
/// <param name="Schema">Esquema.</param>
/// <param name="Name">Nombre.</param>
/// <param name="TypeDescription">sys.objects.type_desc.</param>
/// <param name="Definition">Código fuente; null si está cifrado u oculto.</param>
/// <param name="IsEncrypted">Módulo cifrado (WITH ENCRYPTION).</param>
/// <param name="IsWithheld">Definición ocultada porque menciona objetos o columnas excluidos (RF-MET-06).</param>
/// <param name="Truncated">La definición se recortó al tamaño máximo de respuesta.</param>
/// <param name="Note">Explicación cuando no se devuelve la definición.</param>
public sealed record ObjectDefinitionResult(
    string Database,
    string Schema,
    string Name,
    string TypeDescription,
    string? Definition,
    bool IsEncrypted,
    bool IsWithheld,
    bool Truncated,
    string? Note);

/// <summary>Índices de una tabla (list_indexes, RF-MET-08).</summary>
public sealed record IndexListResult(string Database, string Schema, string Table, IReadOnlyList<IndexInfo> Indexes);

/// <summary>Arista del grafo de claves foráneas (RF-MET-07).</summary>
public sealed record RelationshipEdge(
    string ConstraintName,
    string FromSchema,
    string FromTable,
    IReadOnlyList<string> FromColumns,
    string ToSchema,
    string ToTable,
    IReadOnlyList<string> ToColumns,
    string OnDelete,
    string OnUpdate,
    bool IsDisabled,
    bool IsTrusted);

/// <summary>Grafo de relaciones (get_relationships, RF-MET-07).</summary>
/// <param name="Database">Base de datos.</param>
/// <param name="Relationships">Aristas visibles.</param>
/// <param name="Truncated">Se alcanzó el máximo de aristas por respuesta.</param>
public sealed record RelationshipGraph(string Database, IReadOnlyList<RelationshipEdge> Relationships, bool Truncated);

/// <summary>Objeto relacionado por una dependencia (RF-MET-12).</summary>
/// <param name="Database">Base de datos del objeto (null = la misma).</param>
/// <param name="Schema">Esquema (null si la referencia no está calificada y no se resolvió).</param>
/// <param name="Name">Nombre.</param>
/// <param name="Type">type_desc si el objeto está resuelto.</param>
/// <param name="IsSchemaBound">Dependencia WITH SCHEMABINDING.</param>
/// <param name="IsResolved">El motor resolvió la referencia a un objeto existente.</param>
public sealed record DependencyInfo(string? Database, string? Schema, string Name, string? Type, bool IsSchemaBound, bool IsResolved);

/// <summary>Dependencias de un objeto (get_dependencies, RF-MET-12).</summary>
/// <param name="Database">Base de datos.</param>
/// <param name="Schema">Esquema del objeto.</param>
/// <param name="Name">Nombre del objeto.</param>
/// <param name="TypeDescription">Tipo del objeto.</param>
/// <param name="ReferencedBy">Objetos que hacen referencia al indicado (dependientes); null si no se pidió.</param>
/// <param name="References">Objetos a los que hace referencia el indicado; null si no se pidió.</param>
public sealed record DependencyResult(
    string Database,
    string Schema,
    string Name,
    string TypeDescription,
    IReadOnlyList<DependencyInfo>? ReferencedBy,
    IReadOnlyList<DependencyInfo>? References);

/// <summary>Coincidencia de search_objects (RF-MET-09).</summary>
/// <param name="Schema">Esquema.</param>
/// <param name="Name">Nombre.</param>
/// <param name="Type">sys.objects.type_desc.</param>
/// <param name="MatchedIn">"name", "definition" o "both".</param>
/// <param name="Snippet">Fragmento (±60 caracteres) alrededor de la primera coincidencia en la definición.</param>
/// <param name="ParentTable">Tabla padre (triggers), como "esquema.tabla".</param>
public sealed record ObjectSearchMatch(string Schema, string Name, string Type, string MatchedIn, string? Snippet, string? ParentTable);

/// <summary>Estadísticas de espacio de una tabla (get_table_stats, RF-MET-11). Sin COUNT(*).</summary>
public sealed record TableStatsInfo(
    string Schema,
    string Name,
    long? ApproximateRows,
    long ReservedKb,
    long UsedKb,
    long DataKb,
    long IndexKb,
    long UnusedKb,
    DateTime? LastStatsUpdate);

/// <summary>Detalle de un trigger DML (RF-MET-10).</summary>
public sealed record TriggerDetails(string ParentSchema, string ParentTable, string ParentType, IReadOnlyList<string> Events, bool IsDisabled, bool IsInsteadOf, bool IsEncrypted);

/// <summary>Detalle de una secuencia (RF-MET-10). Los valores se devuelven como texto para no perder precisión.</summary>
public sealed record SequenceDetails(string DataType, string? StartValue, string? Increment, string? MinimumValue, string? MaximumValue, string? CurrentValue, bool IsCycling);

/// <summary>Detalle de un sinónimo (RF-MET-10).</summary>
/// <param name="BaseObjectName">Objeto de destino tal como está definido.</param>
/// <param name="PointsOutsideAllowedDatabases">El destino está en una base no permitida o en otro servidor.</param>
/// <param name="IsFourPartName">El destino usa un nombre de cuatro partes (servidor vinculado).</param>
/// <param name="IsTargetAllowed">El destino está en una base y un esquema permitidos del perfil.</param>
public sealed record SynonymDetails(string BaseObjectName, bool PointsOutsideAllowedDatabases, bool IsFourPartName, bool IsTargetAllowed);

/// <summary>Detalle de un tipo definido por el usuario (RF-MET-10).</summary>
/// <param name="Kind">"alias", "table" o "clr".</param>
/// <param name="BaseType">Declaración del tipo base (tipos alias).</param>
/// <param name="IsNullable">El tipo admite NULL.</param>
/// <param name="Columns">Columnas (tipos tabla).</param>
public sealed record UserTypeDetails(string Kind, string? BaseType, bool IsNullable, IReadOnlyList<ResultColumnInfo> Columns);

/// <summary>Objeto de list_objects (RF-MET-10). Solo se rellena el detalle del tipo pedido.</summary>
public sealed record CatalogObjectInfo(
    string Schema,
    string Name,
    string Kind,
    TriggerDetails? Trigger,
    SequenceDetails? Sequence,
    SynonymDetails? Synonym,
    UserTypeDetails? UserType);

/// <summary>Filtro de tipo de rutina para list_routines.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutineTypeFilter>))]
public enum RoutineTypeFilter
{
    [JsonStringEnumMemberName("any")]
    Any = 0,

    [JsonStringEnumMemberName("procedure")]
    Procedure = 1,

    [JsonStringEnumMemberName("scalar_function")]
    ScalarFunction = 2,

    [JsonStringEnumMemberName("table_function")]
    TableFunction = 3,
}

/// <summary>Dónde buscar en search_objects (RF-MET-09).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SearchScope>))]
public enum SearchScope
{
    [JsonStringEnumMemberName("both")]
    Both = 0,

    [JsonStringEnumMemberName("name")]
    Name = 1,

    [JsonStringEnumMemberName("definition")]
    Definition = 2,
}

/// <summary>Tipo de objeto para search_objects.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SearchObjectType>))]
public enum SearchObjectType
{
    [JsonStringEnumMemberName("any")]
    Any = 0,

    [JsonStringEnumMemberName("table")]
    Table = 1,

    [JsonStringEnumMemberName("view")]
    View = 2,

    [JsonStringEnumMemberName("procedure")]
    Procedure = 3,

    [JsonStringEnumMemberName("function")]
    Function = 4,

    [JsonStringEnumMemberName("trigger")]
    Trigger = 5,

    [JsonStringEnumMemberName("sequence")]
    Sequence = 6,

    [JsonStringEnumMemberName("synonym")]
    Synonym = 7,
}

/// <summary>Sentido de las dependencias en get_dependencies (RF-MET-12).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DependencyDirection>))]
public enum DependencyDirection
{
    /// <summary>Ambos sentidos.</summary>
    [JsonStringEnumMemberName("both")]
    Both = 0,

    /// <summary>Objetos que hacen referencia al indicado (dependientes).</summary>
    [JsonStringEnumMemberName("referencing")]
    Referencing = 1,

    /// <summary>Objetos referenciados por el indicado.</summary>
    [JsonStringEnumMemberName("referenced")]
    Referenced = 2,
}

/// <summary>Clase de objeto para list_objects (RF-MET-10).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CatalogObjectKind>))]
public enum CatalogObjectKind
{
    [JsonStringEnumMemberName("trigger")]
    Trigger = 0,

    [JsonStringEnumMemberName("sequence")]
    Sequence = 1,

    [JsonStringEnumMemberName("synonym")]
    Synonym = 2,

    [JsonStringEnumMemberName("type")]
    Type = 3,
}
