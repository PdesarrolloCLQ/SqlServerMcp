using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>
/// Consultas de catálogo compartidas por los módulos de ejecución y metadatos (RS-INJ-01, RF-MET-06, RF-MET-11,
/// RF-MET-12, RF-DDL-07). Cada método usa la conexión y transacción recibidas (por ejemplo, la de una transacción
/// explícita, necesaria para ver tablas temporales #) o abre una conexión de lectura propia con el identificador de
/// correlación "catalog". Todas las consultas son estáticas y parametrizadas.
/// </summary>
/// <remarks>
/// <see cref="CatalogObject"/> no incluye la base de datos. Cuando <see cref="FindObjectAsync"/> resuelve un objeto en
/// otra base permitida, se recuerda esa base para la misma instancia (tabla débil); los demás métodos además verifican
/// que el object_id corresponda al nombre esperado, de modo que nunca devuelven datos de otro objeto.
/// Los objetos cuyo nombre empieza por "#" se consultan en tempdb y solo son visibles en la sesión que los creó.
/// </remarks>
public sealed class CatalogLookup : ICatalogLookup
{
    private const string CatalogCorrelationId = "catalog";
    private const string TempDatabase = "tempdb";

    private readonly ISqlConnectionFactory _connections;
    private readonly ConditionalWeakTable<CatalogObject, string> _objectDatabases = new();

    public CatalogLookup(ISqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <inheritdoc />
    /// <remarks>
    /// RS-INJ-01, RF-MET-06: el nombre de servidor debe ser null; la base debe ser null, la inicial u otra permitida;
    /// el esquema por defecto es "dbo". Devuelve null si el objeto no existe, está excluido o su esquema no está permitido.
    /// Las tablas temporales (#x, ##x) se resuelven con OBJECT_ID(N'tempdb..#x') en la sesión indicada y no se someten a
    /// las listas de esquemas (pertenecen a la sesión).
    /// </remarks>
    public async Task<CatalogObject?> FindObjectAsync(ProfileRuntime profile, SqlObjectName name, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(name);
        if (name.Server is not null || !SqlNames.IsValidIdentifier(name.Name))
        {
            return null;
        }

        if (name.IsTemp)
        {
            return await FindTempObjectAsync(profile, name, connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        string? database = null;
        if (!string.IsNullOrEmpty(name.Database))
        {
            if (!SqlNames.IsValidIdentifier(name.Database) || !profile.IsDatabaseAllowed(name.Database))
            {
                return null;
            }

            if (!string.Equals(name.Database, profile.InitialDatabase, StringComparison.OrdinalIgnoreCase))
            {
                database = profile.AllowedDatabases.First(d => string.Equals(d, name.Database, StringComparison.OrdinalIgnoreCase));
            }
        }

        var schema = string.IsNullOrEmpty(name.Schema) ? "dbo" : name.Schema;
        if (!SqlNames.IsValidIdentifier(schema) || !profile.IsSchemaAllowed(schema) || profile.IsObjectExcluded(schema, name.Name))
        {
            return null;
        }

        var parameters = new CatalogParameters();
        parameters.NVarChar("@schema", schema);
        parameters.NVarChar("@name", name.Name);
        var found = await ExecuteAsync(
            profile,
            database,
            connection,
            transaction,
            CatalogQueries.FindObject,
            parameters,
            async (reader, ct) => await reader.ReadAsync(ct).ConfigureAwait(false)
                ? new CatalogObject(reader.GetInt(0), reader.GetStringOrEmpty(1), reader.GetStringOrEmpty(2), reader.GetStringOrEmpty(3), reader.GetStringOrEmpty(4))
                : null,
            cancellationToken).ConfigureAwait(false);

        // Red de seguridad con los nombres canónicos devueltos por el catálogo (RF-MET-06).
        if (found is null || !profile.IsSchemaAllowed(found.Schema) || profile.IsObjectExcluded(found.Schema, found.Name))
        {
            return null;
        }

        if (database is not null)
        {
            _objectDatabases.AddOrUpdate(found, database);
        }

        return found;
    }

    /// <inheritdoc />
    /// <remarks>RF-MET-06: nunca devuelve columnas excluidas por el perfil. MaxLength en bytes, como sys.columns.</remarks>
    public async Task<IReadOnlyList<CatalogColumn>> GetColumnsAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(table);
        var columns = await ExecuteAsync(
            profile,
            DatabaseOf(table),
            connection,
            transaction,
            CatalogQueries.Columns,
            ObjectParameters(table),
            async (reader, ct) =>
            {
                var list = new List<CatalogColumn>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(new CatalogColumn(
                        reader.GetInt(0),
                        reader.GetStringOrEmpty(1),
                        CatalogQueries.ReadCatalogType(reader, 2),
                        reader.GetBool(8),
                        reader.GetBool(9),
                        reader.GetBool(10),
                        reader.GetBool(11),
                        reader.GetBool(12),
                        reader.GetBool(13)));
                }

                return list;
            },
            cancellationToken).ConfigureAwait(false);

        return IsTemp(table)
            ? columns
            : columns.Where(c => !profile.IsColumnExcluded(table.Schema, table.Name, c.Name)).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-04: sys.parameters sin el valor de retorno (parameter_id 0). Los nombres conservan "@".
    /// HasDefaultValue combina sys.parameters.has_default_value (solo CLR) con los valores por defecto
    /// obtenidos del análisis léxico de la definición T-SQL (si no está cifrada).
    /// </remarks>
    public Task<IReadOnlyList<CatalogParameter>> GetParametersAsync(ProfileRuntime profile, CatalogObject routine, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(routine);
        return ExecuteAsync<IReadOnlyList<CatalogParameter>>(
            profile,
            DatabaseOf(routine),
            connection,
            transaction,
            CatalogQueries.Parameters,
            ObjectParameters(routine),
            async (reader, ct) =>
            {
                var rows = new List<(int Ordinal, string Name, CatalogType Type, bool IsOutput, bool HasDefault, bool IsReadOnly)>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add((reader.GetInt(0), reader.GetStringOrEmpty(1), CatalogQueries.ReadCatalogType(reader, 2), reader.GetBool(8), reader.GetBool(9), reader.GetBool(10)));
                }

                string? definition = null;
                if (await reader.NextResultAsync(ct).ConfigureAwait(false) && await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    definition = reader.GetStringOrNull(0);
                }

                var defaults = RoutineParameterDefaults.Extract(definition);
                return rows.Select(r => new CatalogParameter(r.Ordinal, r.Name, r.Type, r.IsOutput, r.HasDefault || defaults.ContainsKey(r.Name), r.IsReadOnly)).ToList();
            },
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>RF-MET-11: SUM(rows) de sys.partitions (index_id 0 o 1); no requiere VIEW DATABASE STATE y nunca usa COUNT(*).</remarks>
    public Task<long?> GetApproximateRowCountAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(table);
        return ExecuteAsync(
            profile,
            DatabaseOf(table),
            connection,
            transaction,
            CatalogQueries.ApproximateRows,
            ObjectParameters(table),
            async (reader, ct) => await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetLongOrNull(0) : null,
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-12, RF-DDL-07: objetos que referencian al indicado según sys.sql_expression_dependencies, incluidas las
    /// referencias aún no resueltas por nombre. Formato "esquema.nombre (TYPE_DESC)". Solo se devuelven objetos visibles
    /// del perfil (esquema permitido, no excluido) (RF-MET-06).
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetDependentsAsync(ProfileRuntime profile, CatalogObject target, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(target);
        if (IsTemp(target))
        {
            return [];
        }

        var parameters = new CatalogParameters();
        parameters.Int("@id", target.ObjectId);
        parameters.Int("@class", target.TypeCode is "TY" or "TT" ? 6 : 1);
        parameters.NVarChar("@name", target.Name);
        parameters.NVarChar("@schema", target.Schema);
        var rows = await ExecuteAsync(
            profile,
            DatabaseOf(target),
            connection,
            transaction,
            CatalogQueries.Dependents,
            parameters,
            async (reader, ct) =>
            {
                var list = new List<(string Schema, string Name, string Type)>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add((reader.GetStringOrEmpty(0), reader.GetStringOrEmpty(1), reader.GetStringOrEmpty(2)));
                }

                return list;
            },
            cancellationToken).ConfigureAwait(false);

        return rows
            .Where(r => profile.IsSchemaAllowed(r.Schema) && !profile.IsObjectExcluded(r.Schema, r.Name))
            .Select(r => $"{r.Schema}.{r.Name} ({r.Type})")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>RF-MET-05, RF-DDL-05: sys.sql_modules.definition; null si el módulo está cifrado o no es un módulo.</remarks>
    public Task<string?> GetModuleDefinitionAsync(ProfileRuntime profile, CatalogObject module, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(module);
        return ExecuteAsync(
            profile,
            DatabaseOf(module),
            connection,
            transaction,
            CatalogQueries.ModuleDefinition,
            ObjectParameters(module),
            async (reader, ct) => await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetStringOrNull(0) : null,
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RS-DAT-02: nombres de columnas con clasificación de sensibilidad (sys.sensitivity_classifications, SQL Server 2019+
    /// y Azure). En versiones sin la vista, o si el login no puede leerla, devuelve una lista vacía.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetClassifiedColumnsAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(table);
        if (IsTemp(table))
        {
            return [];
        }

        var parameters = new CatalogParameters();
        parameters.Int("@id", table.ObjectId);
        try
        {
            return await ExecuteAsync<IReadOnlyList<string>>(
                profile,
                DatabaseOf(table),
                connection,
                transaction,
                CatalogQueries.ClassifiedColumns,
                parameters,
                async (reader, ct) =>
                {
                    var list = new List<string>();
                    do
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            list.Add(reader.GetStringOrEmpty(0));
                        }
                    }
                    while (await reader.NextResultAsync(ct).ConfigureAwait(false));

                    return list;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number != -2)
        {
            // La clasificación es informativa: sin permiso (VIEW ANY SENSITIVITY CLASSIFICATION) o sin la vista, no hay datos.
            return [];
        }
    }

    /// <summary>Base de datos en la que se resolvió el objeto (null = la inicial del perfil, "tempdb" para #temporales).</summary>
    internal string? DatabaseOf(CatalogObject catalogObject) =>
        IsTemp(catalogObject) ? TempDatabase
        : _objectDatabases.TryGetValue(catalogObject, out var database) ? database
        : null;

    private static bool IsTemp(CatalogObject catalogObject) => catalogObject.Name.StartsWith('#');

    private static CatalogParameters ObjectParameters(CatalogObject catalogObject)
    {
        var parameters = new CatalogParameters();
        parameters.Int("@id", catalogObject.ObjectId);
        parameters.NVarChar("@name", IsTemp(catalogObject) ? null : catalogObject.Name);
        return parameters;
    }

    private async Task<CatalogObject?> FindTempObjectAsync(ProfileRuntime profile, SqlObjectName name, SqlConnection? connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (name.Database is not null && !string.Equals(name.Database, TempDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string tempName;
        try
        {
            tempName = "tempdb.." + SqlNames.Quote(name.Name);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var parameters = new CatalogParameters();
        parameters.NVarChar("@temp", tempName, 512);
        return await ExecuteAsync(
            profile,
            null,
            connection,
            transaction,
            CatalogQueries.FindTempObject,
            parameters,
            async (reader, ct) => await reader.ReadAsync(ct).ConfigureAwait(false)
                ? new CatalogObject(reader.GetInt(0), "dbo", name.Name, reader.GetStringOrEmpty(1), reader.GetStringOrEmpty(2))
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(
        ProfileRuntime profile,
        string? database,
        SqlConnection? connection,
        SqlTransaction? transaction,
        string sql,
        CatalogParameters parameters,
        Func<SqlDataReader, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        SqlConnection? owned = null;
        try
        {
            var target = connection;
            if (target is null)
            {
                owned = await _connections.OpenAsync(profile, ConnectionRole.Read, CatalogCorrelationId, cancellationToken).ConfigureAwait(false);
                target = owned;
                transaction = null;
            }

            await using var command = CatalogSql.CreateCommand(target, transaction, profile, database, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await read(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is -2 or 1222)
        {
            throw CatalogSql.Translate(ex, profile, database)!;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
