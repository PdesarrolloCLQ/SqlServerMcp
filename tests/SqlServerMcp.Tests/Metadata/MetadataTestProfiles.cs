using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Metadata;

/// <summary>Perfiles y dobles de prueba para las pruebas unitarias de metadatos.</summary>
internal static class MetadataTestProfiles
{
    public static ProfileRuntime Create(Action<ProfileConfig>? customize = null)
    {
        var config = new ProfileConfig
        {
            Mode = OperationMode.Lectura,
            Connection = new ConnectionConfig { Server = "localhost,14333", Database = "VentasTest" },
            AllowedDatabases = ["VentasTest", "Historico"],
            AllowedSchemas = ["ventas", "ia", "seguridad"],
            Excluded = new ExclusionConfig
            {
                Schemas = ["seguridad"],
                Tables = ["ventas.TarjetasCredito", "temp.*", "Auditoria*"],
                Columns = ["*.Contrasena", "ventas.Usuarios.Token"],
            },
        };
        customize?.Invoke(config);
        return new ProfileRuntime("unitario", config, config.Limits);
    }
}

/// <summary>Fábrica que falla si se intenta abrir una conexión: verifica que la validación ocurre antes.</summary>
internal sealed class NoConnectionFactory : ISqlConnectionFactory
{
    public int OpenCalls { get; private set; }

    public Task<SqlConnection> OpenAsync(ProfileRuntime profile, ConnectionRole role, string correlationId, CancellationToken cancellationToken)
    {
        OpenCalls++;
        throw new InvalidOperationException("No se esperaba abrir una conexión en una prueba unitaria.");
    }

    public string DescribeTarget(ProfileRuntime profile) => "sin-conexion";
}
