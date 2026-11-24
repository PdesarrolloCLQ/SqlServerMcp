using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>Objeto de catálogo resuelto (sys.objects / sys.types).</summary>
/// <param name="ObjectId">object_id.</param>
/// <param name="Schema">Esquema.</param>
/// <param name="Name">Nombre.</param>
/// <param name="TypeCode">sys.objects.type ("U", "V", "P", "FN", "IF", "TF", "TR", "SO", "SN"…).</param>
/// <param name="TypeDescription">sys.objects.type_desc.</param>
public sealed record CatalogObject(int ObjectId, string Schema, string Name, string TypeCode, string TypeDescription)
{
    public bool IsTable => TypeCode == "U";

    public bool IsView => TypeCode == "V";

    public bool IsProcedure => TypeCode is "P" or "PC";

    public string QualifiedName => $"{Schema}.{Name}";
}

/// <summary>Columna de tabla o vista según sys.columns.</summary>
public sealed record CatalogColumn(
    int ColumnId,
    string Name,
    CatalogType Type,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool HasDefault,
    bool IsRowGuidCol,
    bool IsRowVersion);

/// <summary>Parámetro de procedimiento o función según sys.parameters.</summary>
public sealed record CatalogParameter(
    int Ordinal,
    string Name,
    CatalogType Type,
    bool IsOutput,
    bool HasDefaultValue,
    bool IsReadOnly);

/// <summary>
/// Consultas de catálogo compartidas por los módulos (validación de identificadores contra
/// sys.objects / sys.columns, RS-INJ-01). Respeta exclusiones y esquemas permitidos del perfil.
/// Todas las consultas usan parámetros; nunca concatenan valores del agente.
/// </summary>
public interface ICatalogLookup
{
    /// <summary>Resuelve un objeto en la base inicial del perfil. Devuelve null si no existe, está excluido o fuera de los esquemas permitidos.</summary>
    Task<CatalogObject?> FindObjectAsync(ProfileRuntime profile, SqlObjectName name, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);

    /// <summary>Columnas visibles (sin las excluidas) de una tabla o vista.</summary>
    Task<IReadOnlyList<CatalogColumn>> GetColumnsAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);

    /// <summary>Parámetros de un procedimiento o función.</summary>
    Task<IReadOnlyList<CatalogParameter>> GetParametersAsync(ProfileRuntime profile, CatalogObject routine, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);

    /// <summary>Filas aproximadas (sys.dm_db_partition_stats), sin COUNT(*) (RF-MET-11).</summary>
    Task<long?> GetApproximateRowCountAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);

    /// <summary>Objetos que dependen del indicado (sys.sql_expression_dependencies, RF-MET-12, RF-DDL-07).</summary>
    Task<IReadOnlyList<string>> GetDependentsAsync(ProfileRuntime profile, CatalogObject target, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);

    /// <summary>OBJECT_DEFINITION de un módulo; null si está cifrado o no es un módulo.</summary>
    Task<string?> GetModuleDefinitionAsync(ProfileRuntime profile, CatalogObject module, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);

    /// <summary>Columnas con clasificación de sensibilidad (sys.sensitivity_classifications) de una tabla, si está disponible.</summary>
    Task<IReadOnlyList<string>> GetClassifiedColumnsAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null);
}
