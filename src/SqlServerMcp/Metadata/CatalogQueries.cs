using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Execution;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Textos SQL estáticos de catálogo compartidos por <see cref="CatalogLookup"/> y <see cref="MetadataService"/>.
/// Compatibles con SQL Server 2016+, Azure SQL Database y Managed Instance: no usan STRING_AGG, TRIM, CONCAT_WS,
/// TRY_CONVERT ni sys.dm_db_partition_stats (que exige VIEW DATABASE STATE). Todos los valores son parámetros.
/// </summary>
internal static class CatalogQueries
{
    /// <summary>
    /// Verificación opcional de que el object_id corresponde al nombre esperado (protege frente a objetos
    /// resueltos en otra base). Con @name NULL (tablas temporales) no se verifica.
    /// </summary>
    public const string NameGuard = "(@name IS NULL OR EXISTS (SELECT 1 FROM sys.objects AS g WHERE g.object_id = @id AND g.name = @name))";

    /// <summary>Tipo base de un tipo alias ("sysname" → "nvarchar"); NULL para tipos del sistema, tabla y CLR.</summary>
    public const string BaseTypeExpression =
        "CASE WHEN t.is_assembly_type = 0 AND t.is_table_type = 0 AND t.user_type_id <> t.system_type_id THEN TYPE_NAME(t.system_type_id) END";

    /// <summary>Índices (sin montones ni hipotéticos) con espacio usado y, a continuación, sus columnas (RF-MET-08).</summary>
    public const string Indexes = """
        SELECT i.index_id, i.name, i.type_desc, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint,
               i.filter_definition, i.is_disabled, i.fill_factor, sz.used_kb
        FROM sys.indexes AS i
        OUTER APPLY (
            SELECT SUM(au.used_pages) * 8 AS used_kb
            FROM sys.partitions AS pa
            JOIN sys.allocation_units AS au ON au.container_id = CASE WHEN au.type IN (1, 3) THEN pa.hobt_id ELSE pa.partition_id END
            WHERE pa.object_id = i.object_id AND pa.index_id = i.index_id) AS sz
        WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
        ORDER BY i.index_id;
        SELECT ic.index_id, ic.key_ordinal, ic.index_column_id, c.name, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = @id
        ORDER BY ic.index_id, ic.key_ordinal, ic.index_column_id;
        """;

    /// <summary>Expresión de tipo base para un alias de sys.types distinto de "t".</summary>
    public static string BaseType(string typeAlias) =>
        $"CASE WHEN {typeAlias}.is_assembly_type = 0 AND {typeAlias}.is_table_type = 0 AND {typeAlias}.user_type_id <> {typeAlias}.system_type_id THEN TYPE_NAME({typeAlias}.system_type_id) END";

    /// <summary>Objeto por esquema y nombre exactos (sys.objects y, en su defecto, tipos de usuario de sys.types).</summary>
    public const string FindObject = """
        SELECT TOP (1) x.id, x.schema_name, x.object_name, x.type_code, x.type_desc
        FROM (
            SELECT o.object_id AS id, s.name AS schema_name, o.name AS object_name, RTRIM(o.type) AS type_code,
                   CAST(o.type_desc AS nvarchar(60)) AS type_desc, 0 AS priority
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE s.name = @schema AND o.name = @name
            UNION ALL
            SELECT t.user_type_id, s.name, t.name,
                   CASE WHEN t.is_table_type = 1 THEN N'TT' ELSE N'TY' END,
                   CASE WHEN t.is_table_type = 1 THEN N'TABLE_TYPE' ELSE N'USER_DEFINED_TYPE' END, 1
            FROM sys.types AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_user_defined = 1 AND s.name = @schema AND t.name = @name
        ) AS x
        ORDER BY x.priority;
        """;

    /// <summary>Tabla temporal de la sesión actual (#tabla o ##tabla).</summary>
    public const string FindTempObject = """
        SELECT o.object_id, RTRIM(o.type) AS type_code, o.type_desc
        FROM tempdb.sys.objects AS o
        WHERE o.object_id = OBJECT_ID(@temp);
        """;

    /// <summary>Columnas de un objeto (contrato <see cref="CatalogColumn"/>).</summary>
    public const string Columns = $"""
        SELECT c.column_id, c.name, t.name AS type_name, c.max_length, c.precision, c.scale, t.is_user_defined,
               {BaseTypeExpression} AS base_type,
               c.is_nullable, c.is_identity, c.is_computed,
               CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS has_default,
               c.is_rowguidcol, CASE WHEN c.system_type_id = 189 THEN 1 ELSE 0 END AS is_rowversion
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = @id AND {NameGuard}
        ORDER BY c.column_id;
        """;

    /// <summary>Parámetros (sin el valor de retorno, parameter_id 0) y definición del módulo.</summary>
    public const string Parameters = $"""
        SELECT p.parameter_id, p.name, t.name AS type_name, p.max_length, p.precision, p.scale, t.is_user_defined,
               {BaseTypeExpression} AS base_type,
               p.is_output, p.has_default_value, p.is_readonly
        FROM sys.parameters AS p
        JOIN sys.types AS t ON t.user_type_id = p.user_type_id
        WHERE p.object_id = @id AND p.parameter_id > 0 AND {NameGuard}
        ORDER BY p.parameter_id;
        SELECT m.definition FROM sys.sql_modules AS m WHERE m.object_id = @id AND {NameGuard};
        """;

    /// <summary>Filas aproximadas desde sys.partitions (montón o índice agrupado). Sin COUNT(*) (RF-MET-11).</summary>
    public const string ApproximateRows = $"""
        SELECT SUM(p.rows) FROM sys.partitions AS p
        WHERE p.object_id = @id AND p.index_id IN (0, 1) AND {NameGuard};
        """;

    /// <summary>Objetos que referencian al indicado (resueltos o pendientes de resolver por nombre).</summary>
    public const string Dependents = """
        SELECT DISTINCT s.name AS schema_name, o.name AS object_name, o.type_desc
        FROM sys.sql_expression_dependencies AS d
        JOIN sys.objects AS o ON o.object_id = d.referencing_id
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE d.referencing_id <> @id
          AND ((d.referenced_id = @id AND d.referenced_class = @class)
            OR (d.referenced_id IS NULL AND d.referenced_class = @class AND d.referenced_server_name IS NULL
                AND d.referenced_database_name IS NULL AND d.referenced_entity_name = @name
                AND (d.referenced_schema_name IS NULL OR d.referenced_schema_name = @schema)))
        ORDER BY s.name, o.name;
        """;

    /// <summary>Definición de un módulo (NULL si está cifrado).</summary>
    public const string ModuleDefinition = $"""
        SELECT m.definition FROM sys.sql_modules AS m WHERE m.object_id = @id AND {NameGuard};
        """;

    /// <summary>
    /// Columnas clasificadas (sys.sensitivity_classifications, SQL Server 2019+ / Azure). La vista se comprueba antes
    /// y se consulta con SQL dinámico estático para que el lote compile en SQL Server 2016/2017.
    /// </summary>
    public const string ClassifiedColumns = """
        IF OBJECT_ID(N'sys.sensitivity_classifications') IS NOT NULL
            EXEC sys.sp_executesql
                N'SELECT c.name FROM sys.sensitivity_classifications AS sc JOIN sys.columns AS c ON c.object_id = sc.major_id AND c.column_id = sc.minor_id WHERE sc.class = 1 AND sc.major_id = @oid ORDER BY c.column_id;',
                N'@oid int', @oid = @id;
        """;

    /// <summary>
    /// Declaración del tipo para los DTO: los tipos de usuario se muestran como "esquema.nombre"; los del sistema
    /// con longitud, precisión o escala ("nvarchar(100)", "decimal(18,4)").
    /// </summary>
    public static string FormatDataType(string typeName, string? typeSchema, bool isUserDefined, int maxLength, int precision, int scale)
    {
        if (isUserDefined)
        {
            return string.IsNullOrEmpty(typeSchema) ? typeName : typeSchema + "." + typeName;
        }

        return new CatalogType(typeName, (short)maxLength, (byte)precision, (byte)scale).Declaration;
    }

    /// <summary>Declaración del tipo base de un tipo alias, o null.</summary>
    public static string? FormatBaseType(string? baseType, int maxLength, int precision, int scale) =>
        baseType is null ? null : new CatalogType(baseType, (short)maxLength, (byte)precision, (byte)scale).Declaration;

    /// <summary>
    /// Longitud (caracteres para texto, bytes para binarios, -1 = max), precisión y escala relevantes para el tipo.
    /// </summary>
    public static (int? Length, int? Precision, int? Scale) Dimensions(string typeName, int maxLength, int precision, int scale)
    {
        switch (typeName.ToLower(CultureInfo.InvariantCulture))
        {
            case "varchar":
            case "char":
            case "varbinary":
            case "binary":
                return (maxLength, null, null);
            case "nvarchar":
            case "nchar":
                return (maxLength == -1 ? -1 : maxLength / 2, null, null);
            case "decimal":
            case "numeric":
                return (null, precision, scale);
            case "datetime2":
            case "datetimeoffset":
            case "time":
                return (null, null, scale);
            default:
                return (null, null, null);
        }
    }

    /// <summary>Clase de rutina para los DTO a partir de sys.objects.type.</summary>
    public static string? RoutineKind(string typeCode) => typeCode switch
    {
        "P" or "PC" => "procedure",
        "FN" or "FS" => "scalar_function",
        "IF" or "TF" or "FT" => "table_function",
        _ => null,
    };

    /// <summary>Lee el tipo de catálogo de las columnas 2..7 de <see cref="Columns"/> y <see cref="Parameters"/>.</summary>
    public static CatalogType ReadCatalogType(SqlDataReader reader, int typeNameOrdinal) => new(
        reader.GetStringOrEmpty(typeNameOrdinal),
        (short)reader.GetInt(typeNameOrdinal + 1),
        (byte)reader.GetInt(typeNameOrdinal + 2),
        (byte)reader.GetInt(typeNameOrdinal + 3),
        reader.GetBool(typeNameOrdinal + 4),
        reader.GetStringOrNull(typeNameOrdinal + 5));
}
