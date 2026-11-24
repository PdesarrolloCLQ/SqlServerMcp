using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>Sinónimo del catálogo: esquema, nombre y objeto base tal como lo guarda SQL Server.</summary>
public sealed record SynonymEntry(string Schema, string Name, string BaseObjectName);

/// <summary>Objeto del catálogo: tipo (sys.objects.type), definición del módulo y, si es un sinónimo, su objeto base.</summary>
public sealed record ModuleCatalogEntry(string Database, string Schema, string Name, string Type, string? Definition, string? SynonymBase)
{
    public SqlObjectName FullName => new(null, Database, Schema, Name);
}

/// <summary>
/// Consultas al catálogo que necesita la política en tiempo de ejecución (resolución de sinónimos y definiciones de
/// módulos). Siempre con el login de lectura. Los errores se propagan: el llamador bloquea (fail-closed).
/// </summary>
public interface ICatalogResolver
{
    Task<IReadOnlyList<SynonymEntry>> GetSynonymsAsync(ProfileRuntime profile, string database, string correlationId, CancellationToken cancellationToken);

    /// <summary>Objetos con ese nombre; si el esquema es null, los de cualquier esquema (los nombres de una parte se resuelven según el usuario).</summary>
    Task<IReadOnlyList<ModuleCatalogEntry>> GetObjectsAsync(ProfileRuntime profile, SqlObjectName name, string correlationId, CancellationToken cancellationToken);
}

public sealed class CatalogResolver(ISqlConnectionFactory connections) : ICatalogResolver
{
    public async Task<IReadOnlyList<SynonymEntry>> GetSynonymsAsync(ProfileRuntime profile, string database, string correlationId, CancellationToken cancellationToken)
    {
        var db = SqlNames.Quote(database);
        await using var connection = await connections.OpenAsync(profile, ConnectionRole.Read, correlationId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;
        command.CommandText = $"""
            SELECT s.name, sn.name, sn.base_object_name
            FROM {db}.sys.synonyms AS sn
            JOIN {db}.sys.schemas AS s ON s.schema_id = sn.schema_id;
            """;
        var result = new List<SynonymEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SynonymEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ModuleCatalogEntry>> GetObjectsAsync(ProfileRuntime profile, SqlObjectName name, string correlationId, CancellationToken cancellationToken)
    {
        var database = string.IsNullOrEmpty(name.Database) ? profile.InitialDatabase : name.Database;
        var db = SqlNames.Quote(database);
        await using var connection = await connections.OpenAsync(profile, ConnectionRole.Read, correlationId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;
        command.CommandText = $"""
            SELECT s.name, o.name, RTRIM(o.type), m.definition, sn.base_object_name
            FROM {db}.sys.objects AS o
            JOIN {db}.sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN {db}.sys.sql_modules AS m ON m.object_id = o.object_id
            LEFT JOIN {db}.sys.synonyms AS sn ON sn.object_id = o.object_id
            WHERE o.name = @name AND (@schema IS NULL OR s.name = @schema);
            """;
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = name.Name });
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128)
        {
            Value = string.IsNullOrEmpty(name.Schema) ? DBNull.Value : name.Schema,
        });

        var result = new List<ModuleCatalogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ModuleCatalogEntry(
                database,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }
}
