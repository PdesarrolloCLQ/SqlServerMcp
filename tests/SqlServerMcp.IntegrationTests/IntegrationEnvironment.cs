using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.IntegrationTests;

/// <summary>
/// Entorno de pruebas de integración contra SQL Server en contenedor (RNF-08).
/// Requiere las variables SQLSERVERMCP_TEST_*; si faltan, las pruebas se omiten.
/// </summary>
public static class IntegrationEnvironment
{
    public const string ReaderPasswordVariable = "SQLSERVERMCP_TEST_READER_PASSWORD";
    public const string WriterPasswordVariable = "SQLSERVERMCP_TEST_WRITER_PASSWORD";

    public static string? Server => Environment.GetEnvironmentVariable("SQLSERVERMCP_TEST_SERVER");

    public static string Database => Environment.GetEnvironmentVariable("SQLSERVERMCP_TEST_DATABASE") ?? "VentasTest";

    public static string? SaPassword => Environment.GetEnvironmentVariable("SQLSERVERMCP_TEST_SA_PASSWORD");

    public static bool IsAvailable =>
        !string.IsNullOrEmpty(Server)
        && !string.IsNullOrEmpty(SaPassword)
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReaderPasswordVariable))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(WriterPasswordVariable));

    /// <summary>Omite la prueba si no hay base de datos de integración configurada.</summary>
    public static void RequireDatabase()
    {
        if (!IsAvailable)
        {
            Assert.Skip("Base de datos de integración no configurada (variables SQLSERVERMCP_TEST_*).");
        }
    }

    /// <summary>Conexión administrativa (solo para preparar/limpiar datos de prueba en el contenedor).</summary>
    public static SqlConnection OpenAdminConnection()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            UserID = "sa",
            Password = SaPassword,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
        };
        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    public static void ExecuteAdmin(string sql)
    {
        using var connection = OpenAdminConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Perfil estándar de pruebas con login lector y escritor de mínimo privilegio.</summary>
    public static ProfileRuntime CreateProfile(OperationMode mode = OperationMode.Completo, Action<ProfileConfig>? customize = null, string name = "pruebas")
    {
        var config = new ProfileConfig
        {
            Mode = mode,
            Environment = ProfileEnvironment.Development,
            Connection = new ConnectionConfig
            {
                Server = Server ?? "localhost,14333",
                Database = Database,
                Authentication = AuthenticationKind.SqlPassword,
                UserName = "mcp_lector",
                CredentialRef = "env:" + ReaderPasswordVariable,
                Encrypt = EncryptMode.Mandatory,
                TrustServerCertificate = true,
                Retry = new RetryConfig { Enabled = false },
            },
            WriteConnection = new WriteConnectionConfig
            {
                UserName = "mcp_escritor",
                CredentialRef = "env:" + WriterPasswordVariable,
            },
            AllowedDatabases = [Database],
            AllowedSchemas = ["ventas", "ia"],
            DdlSchemas = ["ia"],
            Excluded = new ExclusionConfig
            {
                Schemas = ["seguridad"],
                Tables = ["ventas.TarjetasCredito"],
                Columns = ["*.Contrasena"],
            },
        };
        customize?.Invoke(config);
        return new ProfileRuntime(name, config, config.Limits) { Status = ProfileStatus.Verified };
    }
}
