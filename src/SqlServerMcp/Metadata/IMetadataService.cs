using SqlServerMcp.Configuration;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Descubrimiento de metadatos para las herramientas de solo lectura (RF-MET-01..12, RF-CON-01).
/// Todos los métodos usan el login de lectura (RF-CON-07), respetan los esquemas permitidos y las exclusiones del
/// perfil en todas las respuestas (RF-MET-06), validan los argumentos del agente (RS-INJ-02) y nunca concatenan sus
/// valores al SQL (RS-INJ-01). El argumento opcional <c>database</c> debe estar en la lista permitida del perfil;
/// si no, se lanza <see cref="Infrastructure.ToolFailureException"/> con POLICY_BLOCKED. Los objetos inexistentes,
/// excluidos o fuera de los esquemas permitidos producen el mismo error NOT_FOUND (no se revela su existencia).
/// La paginación acota <c>limit</c> a 1..500 y <c>offset</c> a ≥ 0.
/// </summary>
public interface IMetadataService
{
    /// <summary>test_connection: versión, edición, plataforma, base actual, login y opciones de la base (RF-CON-01).</summary>
    Task<ConnectionTestResult> TestConnectionAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken);

    /// <summary>list_databases: intersección de la lista permitida con las bases accesibles por el login (RF-MET-01).</summary>
    Task<DatabaseListResult> ListDatabasesAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken);

    /// <summary>list_schemas: esquemas permitidos y no excluidos con el número de objetos visibles.</summary>
    Task<SchemaListResult> ListSchemasAsync(ProfileRuntime profile, string? database, string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// list_tables (RF-MET-02). <paramref name="namePattern"/> admite "*" y "?"; sin comodines se busca como subcadena.
    /// </summary>
    Task<MetadataPage<TableSummary>> ListTablesAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string? namePattern,
        bool includeViews,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>describe_table: estructura completa de una tabla o vista (RF-MET-03).</summary>
    Task<TableDescription> DescribeTableAsync(ProfileRuntime profile, string? database, string? schema, string name, string correlationId, CancellationToken cancellationToken);

    /// <summary>list_routines: procedimientos y funciones (RF-MET-04).</summary>
    Task<MetadataPage<RoutineSummary>> ListRoutinesAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string? namePattern,
        RoutineTypeFilter type,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>describe_routine: parámetros, tipo de retorno y columnas devueltas (RF-MET-04).</summary>
    Task<RoutineDescription> DescribeRoutineAsync(ProfileRuntime profile, string? database, string? schema, string name, string correlationId, CancellationToken cancellationToken);

    /// <summary>get_object_definition: código de vistas, procedimientos, funciones y triggers (RF-MET-05).</summary>
    Task<ObjectDefinitionResult> GetObjectDefinitionAsync(ProfileRuntime profile, string? database, string? schema, string name, string correlationId, CancellationToken cancellationToken);

    /// <summary>list_indexes: índices de una tabla o vista indexada (RF-MET-08).</summary>
    Task<IndexListResult> ListIndexesAsync(ProfileRuntime profile, string? database, string? schema, string table, string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// get_relationships: grafo de claves foráneas (RF-MET-07). Con <paramref name="table"/> devuelve las relaciones
    /// entrantes y salientes de esa tabla; con solo <paramref name="schema"/>, las que tienen algún extremo en el esquema.
    /// </summary>
    Task<RelationshipGraph> GetRelationshipsAsync(ProfileRuntime profile, string? database, string? schema, string? table, string correlationId, CancellationToken cancellationToken);

    /// <summary>get_dependencies: objetos que dependen del indicado y objetos de los que depende (RF-MET-12).</summary>
    Task<DependencyResult> GetDependenciesAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string name,
        DependencyDirection direction,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// search_objects (RF-MET-09): por nombre (LIKE con escape; "*"/"?" admitidos) o por texto de la definición
    /// (CHARINDEX parametrizado), con un fragmento alrededor de la primera coincidencia.
    /// </summary>
    Task<MetadataPage<ObjectSearchMatch>> SearchObjectsAsync(
        ProfileRuntime profile,
        string? database,
        string text,
        SearchScope searchIn,
        SearchObjectType type,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>get_table_stats: filas aproximadas y espacio (RF-MET-11), sin COUNT(*).</summary>
    Task<MetadataPage<TableStatsInfo>> GetTableStatsAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string? table,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>list_objects: triggers, secuencias, sinónimos y tipos de usuario (RF-MET-10).</summary>
    Task<MetadataPage<CatalogObjectInfo>> ListObjectsAsync(
        ProfileRuntime profile,
        string? database,
        CatalogObjectKind kind,
        string? schema,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken);
}
