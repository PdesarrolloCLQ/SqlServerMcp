using System.Security.Cryptography;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlServerMcp.IntegrationTests.SqlServerContainerFixture))]

namespace SqlServerMcp.IntegrationTests;

/// <summary>
/// SQL Server en contenedor para las pruebas de integración (RNF-08). Si las variables SQLSERVERMCP_TEST_* ya están
/// definidas, se usa esa instancia; si no, se levanta un contenedor efímero con Testcontainers, se crea la base de
/// pruebas y los logins de mínimo privilegio (scripts/setup-principals.sql). Sin Docker, las pruebas se omiten.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        if (IntegrationEnvironment.IsAvailable || Environment.GetEnvironmentVariable("SQLSERVERMCP_TEST_NO_DOCKER") == "1")
        {
            return;
        }

        var sa = NewPassword("Sa");
        var reader = NewPassword("Lect");
        var writer = NewPassword("Escr");
        try
        {
            _container = new MsSqlBuilder(Image).WithPassword(sa).Build();
            await _container.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Docker no disponible: las pruebas de integración se omitirán.
            _container = null;
            return;
        }

        var sqlDir = Path.Combine(AppContext.BaseDirectory, "Sql");
        await RunScriptAsync(File.ReadAllText(Path.Combine(sqlDir, "01-seed-database.sql"))).ConfigureAwait(false);
        var principals = File.ReadAllText(Path.Combine(sqlDir, "02-setup-principals.sql"))
            .Replace("$(Database)", "VentasTest", StringComparison.Ordinal)
            .Replace("$(DataSchema)", "ventas", StringComparison.Ordinal)
            .Replace("$(WorkSchema)", "ia", StringComparison.Ordinal)
            .Replace("$(DeniedSchema)", "seguridad", StringComparison.Ordinal)
            .Replace("$(ReaderPassword)", reader, StringComparison.Ordinal)
            .Replace("$(WriterPassword)", writer, StringComparison.Ordinal);
        await RunScriptAsync(principals).ConfigureAwait(false);

        Environment.SetEnvironmentVariable("SQLSERVERMCP_TEST_SERVER", $"{_container.Hostname},{_container.GetMappedPublicPort(1433)}");
        Environment.SetEnvironmentVariable("SQLSERVERMCP_TEST_DATABASE", "VentasTest");
        Environment.SetEnvironmentVariable("SQLSERVERMCP_TEST_SA_PASSWORD", sa);
        Environment.SetEnvironmentVariable(IntegrationEnvironment.ReaderPasswordVariable, reader);
        Environment.SetEnvironmentVariable(IntegrationEnvironment.WriterPasswordVariable, writer);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunScriptAsync(string script)
    {
        var result = await _container!.ExecScriptAsync(script).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("No fue posible preparar la base de pruebas: " + result.Stderr + result.Stdout);
        }
    }

    private static string NewPassword(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10)) + "Aa1!";
}
