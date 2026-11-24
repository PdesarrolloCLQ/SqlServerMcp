using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Implementación de <see cref="IMetadataService"/> sobre las vistas de catálogo de SQL Server (RF-MET-01..12).
/// Cada llamada abre una conexión con el login de lectura (RF-CON-07) mediante <see cref="ISqlConnectionFactory"/>,
/// ejecuta lotes estáticos y parametrizados (RS-INJ-01) y aplica la visibilidad del perfil en SQL (para que la
/// paginación sea coherente) y de nuevo en C# (RF-MET-06). Las consultas sobre otra base permitida se ejecutan con
/// <c>[base].sys.sp_executesql</c>. Se evitan COUNT(*) y vistas que exigen VIEW DATABASE STATE (RF-MET-11).
/// </summary>
public sealed partial class MetadataService : IMetadataService
{
    private const string Ci = " COLLATE " + CatalogSql.CiCollation;

    private readonly ISqlConnectionFactory _connections;
    private readonly ICatalogLookup _catalog;

    public MetadataService(ISqlConnectionFactory connections, ICatalogLookup catalog)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    /// <remarks>RF-CON-01, test_connection.</remarks>
    public Task<ConnectionTestResult> TestConnectionAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        const string sql = """
            SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
                   CONVERT(int, SERVERPROPERTY('EngineEdition')),
                   DB_NAME(),
                   SUSER_SNAME(),
                   SYSUTCDATETIME(),
                   d.compatibility_level,
                   d.snapshot_isolation_state,
                   d.is_read_committed_snapshot_on,
                   CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Updateability')),
                   CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))
            FROM (SELECT 1 AS one) AS x
            LEFT JOIN sys.databases AS d ON d.database_id = DB_ID();
            """;

        return RunAsync(profile, profile.InitialDatabase, correlationId, connection => QueryAsync(
            connection,
            profile,
            null,
            sql,
            new CatalogParameters(),
            async reader =>
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new ToolFailureException(ErrorCodes.InternalError, "No se pudo obtener la información del servidor.");
                }

                var engineEdition = reader.GetInt(3);
                return new ConnectionTestResult(
                    reader.GetStringOrEmpty(0),
                    reader.GetStringOrNull(1),
                    reader.GetStringOrNull(2),
                    engineEdition,
                    DescribeEngineEdition(engineEdition),
                    reader.GetStringOrEmpty(4),
                    reader.GetStringOrNull(5),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                    reader.GetIntOrNull(7),
                    reader.GetInt(8) == 1,
                    reader.GetBool(9),
                    string.Equals(reader.GetStringOrNull(10), "READ_ONLY", StringComparison.OrdinalIgnoreCase),
                    reader.GetStringOrNull(11));
            },
            cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>RF-MET-01: lista permitida ∩ sys.databases con HAS_DBACCESS = 1; nunca bases del sistema.</remarks>
    public Task<DatabaseListResult> ListDatabasesAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var parameters = new CatalogParameters();
        var allowed = profile.AllowedDatabases.Where(d => profile.IsDatabaseAllowed(d)).Select(d => parameters.NextNVarChar(d)).ToList();
        if (allowed.Count == 0)
        {
            return Task.FromResult(new DatabaseListResult([]));
        }

        var sql = $"""
            SELECT d.name, d.state_desc, d.compatibility_level, d.is_read_only, d.snapshot_isolation_state_desc,
                   d.is_read_committed_snapshot_on, d.collation_name
            FROM sys.databases AS d
            WHERE d.name{Ci} IN ({string.Join(", ", allowed)}) AND HAS_DBACCESS(d.name) = 1
            ORDER BY d.name;
            """;

        return RunAsync(profile, profile.InitialDatabase, correlationId, connection => QueryAsync(
            connection,
            profile,
            null,
            sql,
            parameters,
            async reader =>
            {
                var list = new List<DatabaseInfo>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetStringOrEmpty(0);
                    if (!profile.IsDatabaseAllowed(name))
                    {
                        continue;
                    }

                    list.Add(new DatabaseInfo(
                        name,
                        reader.GetStringOrEmpty(1),
                        reader.GetInt(2),
                        reader.GetBool(3),
                        reader.GetStringOrEmpty(4),
                        reader.GetBool(5),
                        reader.GetStringOrNull(6),
                        string.Equals(name, profile.InitialDatabase, StringComparison.OrdinalIgnoreCase)));
                }

                return new DatabaseListResult(list);
            },
            cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>list_schemas, RF-MET-06: solo esquemas permitidos y no excluidos; los objetos excluidos no se cuentan.</remarks>
    public Task<SchemaListResult> ListSchemasAsync(ProfileRuntime profile, string? database, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var parameters = new CatalogParameters();
        var filter = new VisibilityFilter(profile, parameters);
        var sql = $"""
            SELECT s.name,
                   SUM(CASE WHEN o.type = 'U' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.type = 'V' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.type IN ('P', 'PC') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.type IN ('FN', 'IF', 'TF', 'FS', 'FT') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.type = 'SO' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.type = 'SN' THEN 1 ELSE 0 END)
            FROM sys.schemas AS s
            LEFT JOIN sys.objects AS o ON o.schema_id = s.schema_id AND o.is_ms_shipped = 0
                 AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'SO', 'SN')
                 AND NOT {filter.ObjectExcluded("s.name", "o.name")}
                 AND (o.type <> 'SN' OR EXISTS (SELECT 1 FROM sys.synonyms AS sn WHERE sn.object_id = o.object_id AND {filter.TextClean("sn.base_object_name")}))
            WHERE {filter.SchemaAllowed("s.name")}
            GROUP BY s.name
            ORDER BY s.name;
            """;

        return RunAsync(profile, db, correlationId, connection => QueryAsync(
            connection,
            profile,
            db,
            sql,
            parameters,
            async reader =>
            {
                var list = new List<SchemaInfo>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetStringOrEmpty(0);
                    if (profile.IsSchemaAllowed(name))
                    {
                        list.Add(new SchemaInfo(name, reader.GetInt(1), reader.GetInt(2), reader.GetInt(3), reader.GetInt(4), reader.GetInt(5), reader.GetInt(6)));
                    }
                }

                return new SchemaListResult(db, list);
            },
            cancellationToken), cancellationToken);
    }

    /// <summary>Plataforma según SERVERPROPERTY('EngineEdition') (RF-CON-01).</summary>
    internal static string DescribeEngineEdition(int engineEdition) => engineEdition switch
    {
        1 or 2 or 3 or 4 => "SQL Server",
        5 => "Azure SQL Database",
        6 => "Azure Synapse Analytics",
        8 => "Azure SQL Managed Instance",
        9 => "Azure SQL Edge",
        11 => "Azure Synapse serverless SQL pool",
        12 => "Microsoft Fabric SQL database",
        _ => "Desconocida",
    };

    /// <summary>Error NOT_FOUND uniforme para objetos inexistentes, excluidos o no permitidos (RF-MET-06).</summary>
    internal static ToolFailureException NotFound(string schema, string name) => new(
        ErrorCodes.NotFound,
        $"No se encontró el objeto '{schema}.{name}' o no está disponible en este perfil.",
        "Use list_tables, list_routines o search_objects para ver los objetos disponibles.");

    /// <summary>Abre la conexión de lectura, ejecuta el trabajo y traduce los errores de catálogo esperables.</summary>
    private async Task<T> RunAsync<T>(ProfileRuntime profile, string database, string correlationId, Func<SqlConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connections.OpenAsync(profile, ConnectionRole.Read, correlationId, cancellationToken).ConfigureAwait(false);
            return await work(connection).ConfigureAwait(false);
        }
        catch (SqlException ex) when (CatalogSql.Translate(ex, profile, database) is { } failure)
        {
            throw failure;
        }
    }

    private static async Task<T> QueryAsync<T>(
        SqlConnection connection,
        ProfileRuntime profile,
        string? database,
        string sql,
        CatalogParameters parameters,
        Func<SqlDataReader, Task<T>> read,
        CancellationToken cancellationToken)
    {
        await using var command = CatalogSql.CreateCommand(connection, null, profile, database, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await read(reader).ConfigureAwait(false);
    }

    /// <summary>
    /// Resuelve un objeto visible del perfil con <see cref="ICatalogLookup"/> o lanza NOT_FOUND uniforme.
    /// Los nombres temporales (#) nunca se resuelven aquí: pertenecen a otras sesiones.
    /// </summary>
    private async Task<CatalogObject> FindVisibleAsync(SqlConnection connection, ProfileRuntime profile, string database, string schema, string name, CancellationToken cancellationToken)
    {
        if (name.StartsWith('#'))
        {
            throw NotFound(schema, name);
        }

        var databasePart = string.Equals(database, profile.InitialDatabase, StringComparison.OrdinalIgnoreCase) ? null : database;
        var found = await _catalog.FindObjectAsync(profile, new SqlObjectName(null, databasePart, schema, name), cancellationToken, connection).ConfigureAwait(false);
        return found ?? throw NotFound(schema, name);
    }

    /// <summary>Parámetros @id/@name de un objeto resuelto.</summary>
    private static CatalogParameters ObjectParameters(CatalogObject catalogObject)
    {
        var parameters = new CatalogParameters();
        parameters.Int("@id", catalogObject.ObjectId);
        parameters.NVarChar("@name", catalogObject.Name);
        return parameters;
    }

    /// <summary>Texto de la base de datos apto para el agente: sin elementos excluidos y recortado (RF-MET-06, RF-QRY-06).</summary>
    private static string? SafeText(ProfileRuntime profile, IReadOnlyList<string> patterns, string? text) =>
        CatalogSql.Clip(ExclusionText.Sanitize(patterns, text), profile.Limits.MaxCellChars);

    private static bool IsVisible(ProfileRuntime profile, string schema, string name) =>
        profile.IsSchemaAllowed(schema) && !profile.IsObjectExcluded(schema, name);
}
