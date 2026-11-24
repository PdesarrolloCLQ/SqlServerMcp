using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Metadata;
using SqlServerMcp.Security;

namespace SqlServerMcp.IntegrationTests.Metadata;

/// <summary>Ayudantes de las pruebas de integración de metadatos (contenedor de desarrollo, RNF-08).</summary>
internal static partial class MetadataIntegrationSupport
{
    public static SqlConnectionFactory CreateFactory() => new(new SecretResolver(), NullLogger<SqlConnectionFactory>.Instance);

    public static (MetadataService Service, CatalogLookup Catalog, SqlConnectionFactory Factory) CreateServices()
    {
        var factory = CreateFactory();
        var catalog = new CatalogLookup(factory);
        return (new MetadataService(factory, catalog), catalog, factory);
    }

    /// <summary>Ejecuta un script con separadores GO usando el login administrador del contenedor.</summary>
    public static void ExecuteAdminScript(string script, string? database = null, int timeoutSeconds = 600)
    {
        using var connection = IntegrationEnvironment.OpenAdminConnection();
        if (database is not null)
        {
            connection.ChangeDatabase(database);
        }

        foreach (var batch in GoSeparator().Split(script))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandTimeout = timeoutSeconds;
            command.CommandText = batch;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Elimina un esquema de pruebas y todos sus objetos (idempotente).</summary>
    public static void DropSchema(string schema, string? database = null)
    {
        const string sql = """
            IF SCHEMA_ID(@s) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += N'ALTER TABLE ' + QUOTENAME(@s) + N'.' + QUOTENAME(t.name) + N' SET (SYSTEM_VERSIONING = OFF);'
                FROM sys.tables AS t WHERE t.schema_id = SCHEMA_ID(@s) AND t.temporal_type = 2;
                SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
                             + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
                FROM sys.foreign_keys AS fk
                WHERE fk.schema_id = SCHEMA_ID(@s) OR fk.referenced_object_id IN (SELECT o.object_id FROM sys.objects AS o WHERE o.schema_id = SCHEMA_ID(@s));
                SELECT @sql += N'DROP ' + CASE RTRIM(o.type) WHEN 'V' THEN N'VIEW' WHEN 'P' THEN N'PROCEDURE' WHEN 'SN' THEN N'SYNONYM'
                                                               WHEN 'SO' THEN N'SEQUENCE' ELSE N'FUNCTION' END
                             + N' ' + QUOTENAME(@s) + N'.' + QUOTENAME(o.name) + N';'
                FROM sys.objects AS o WHERE o.schema_id = SCHEMA_ID(@s) AND o.type IN ('V', 'P', 'SN', 'SO', 'FN', 'IF', 'TF');
                SELECT @sql += N'DROP TABLE ' + QUOTENAME(@s) + N'.' + QUOTENAME(o.name) + N';'
                FROM sys.objects AS o WHERE o.schema_id = SCHEMA_ID(@s) AND o.type = 'U';
                SELECT @sql += N'DROP TYPE ' + QUOTENAME(@s) + N'.' + QUOTENAME(t.name) + N';'
                FROM sys.types AS t WHERE t.schema_id = SCHEMA_ID(@s) AND t.is_user_defined = 1;
                SET @sql += N'DROP SCHEMA ' + QUOTENAME(@s) + N';';
                EXEC sys.sp_executesql @sql;
            END
            """;

        using var connection = IntegrationEnvironment.OpenAdminConnection();
        if (database is not null)
        {
            connection.ChangeDatabase(database);
        }

        using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@s", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.ExecuteNonQuery();
    }

    /// <summary>Perfil de pruebas con el esquema adicional indicado permitido y las exclusiones estándar.</summary>
    public static ProfileRuntime CreateProfileWithSchema(string schema, string name, params string[] extraExcludedTables) =>
        IntegrationEnvironment.CreateProfile(
            OperationMode.Lectura,
            c =>
            {
                c.AllowedSchemas = ["ventas", "ia", schema];
                c.Excluded = new ExclusionConfig
                {
                    Schemas = ["seguridad"],
                    Tables = ["ventas.TarjetasCredito", .. extraExcludedTables],
                    Columns = ["*.Contrasena"],
                };
            },
            name);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
