using System.Text.Json.Nodes;

namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>Criterios de aceptación que requieren una instancia propia del servidor (configuración o estado distintos).</summary>
[Collection(EndToEndCollection.Name)]
public sealed class IsolatedServerTests
{
    private static readonly string[] WriteTools = ["execute_dml", "insert_rows", "execute_procedure", "execute_ddl", "begin_transaction", "commit_transaction", "rollback_transaction"];

    [Fact]
    public async Task CA25_ModoLectura_NoPublicaHerramientasDeEscritura()
    {
        IntegrationEnvironment.RequireDatabase();
        await using var server = await McpServerHarness.StartAsync(profileArgument: "lectura");
        var tools = await server.Client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("run_query", names);
        Assert.Contains("validate_sql", names);
        foreach (var write in WriteTools)
        {
            Assert.DoesNotContain(write, names);
        }

        foreach (var tool in tools)
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, $"{tool.Name} debe declarar readOnlyHint=true");
            Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public async Task CA26_ClienteSinElicitation_DropRechazadoConConfirmationUnavailable()
    {
        IntegrationEnvironment.RequireDatabase();
        IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.CA26; CREATE TABLE ia.CA26 (Id int NOT NULL);");
        try
        {
            await using var server = await McpServerHarness.StartAsync(supportsElicitation: false);
            var call = await server.CallAsync("execute_ddl", new { sql = "DROP TABLE ia.CA26" });
            Assert.Equal("CONFIRMATION_UNAVAILABLE", call.ErrorCode);
            using var connection = IntegrationEnvironment.OpenAdminConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT OBJECT_ID(N'ia.CA26')";
            Assert.NotEqual(DBNull.Value, command.ExecuteScalar());
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.CA26;");
        }
    }

    [Fact]
    public async Task CA27_FallaDeAuditoria_RechazaEscrituras()
    {
        IntegrationEnvironment.RequireDatabase();
        var dir = McpServerHarness.NewDirectory();
        var blocked = Path.Combine(dir, "audit-bloqueada");
        await File.WriteAllTextAsync(blocked, "un archivo donde debería haber un directorio");
        var config = McpServerHarness.DefaultConfig(dir);
        config["audit"]!["path"] = blocked;
        await using var server = await McpServerHarness.StartAsync(config, directory: dir);
        var write = await server.CallAsync("execute_dml", new { sql = "UPDATE ventas.Clientes SET Activo = Activo WHERE ClienteId = 1" });
        Assert.Equal("AUDIT_UNAVAILABLE", write.ErrorCode);
    }

    [Fact]
    public async Task CA28_ErrorDeConexion_NoExponeSecretos()
    {
        IntegrationEnvironment.RequireDatabase();
        var dir = McpServerHarness.NewDirectory();
        var config = McpServerHarness.DefaultConfig(dir);
        config["profiles"]!["desarrollo"]!["connection"]!["server"] = "127.0.0.1,14999";
        config["profiles"]!["desarrollo"]!["connection"]!["connectTimeoutSeconds"] = 2;
        config["profiles"]!["desarrollo"]!["connection"]!["retry"] = new JsonObject { ["enabled"] = false };
        await using var server = await McpServerHarness.StartAsync(config, directory: dir);
        var call = await server.CallAsync("test_connection", new { profile = "desarrollo" });
        Assert.True(call.IsError);
        Assert.Equal("CONNECTION_ERROR", call.ErrorCode);
        var reader = Environment.GetEnvironmentVariable(IntegrationEnvironment.ReaderPasswordVariable)!;
        Assert.DoesNotContain(reader, call.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", call.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("14999", call.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CA22_LoginSysadmin_ElServidorNoInicia()
    {
        IntegrationEnvironment.RequireDatabase();
        var dir = McpServerHarness.NewDirectory();
        var config = McpServerHarness.DefaultConfig(dir, includeLectura: false);
        var connection = config["profiles"]!["desarrollo"]!["connection"]!;
        connection["userName"] = "sa";
        connection["credentialRef"] = "env:SQLSERVERMCP_TEST_SA_PASSWORD";
        config["profiles"]!["desarrollo"]!.AsObject().Remove("writeConnection");
        var (exitCode, stderr) = await McpServerHarness.RunProcessAsync(config, dir, TimeSpan.FromSeconds(90));
        Assert.Equal(3, exitCode);
        Assert.Contains("sysadmin", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(IntegrationEnvironment.SaPassword!, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CA21_ConsultaQueExcedeElTiempo_CanceladaSinBloqueosResiduales()
    {
        IntegrationEnvironment.RequireDatabase();
        var dir = McpServerHarness.NewDirectory();
        var config = McpServerHarness.DefaultConfig(dir, d => d["limits"]!["commandTimeoutSeconds"] = 3);
        await using var server = await McpServerHarness.StartAsync(config, directory: dir);
        var started = DateTime.UtcNow;
        var call = await server.CallAsync("run_query", new
        {
            // El filtro no sargable obliga a evaluar las 125.000 millones de combinaciones (sin atajos del optimizador).
            sql = "SELECT COUNT_BIG(*) AS n FROM ventas.Facturas a CROSS JOIN ventas.Facturas b CROSS JOIN ventas.Facturas c WHERE CHECKSUM(a.FacturaId, b.FacturaId, c.FacturaId) = 42",
        });
        Assert.True(call.IsError, call.ToString());
        Assert.Contains(call.ErrorCode, new[] { "TIMEOUT", "CONFIRMATION_DENIED" });
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60));

        // Sin bloqueos residuales: otra consulta funciona de inmediato.
        var after = await server.CallAsync("run_query", new { sql = "SELECT COUNT(*) AS n FROM ventas.Clientes" });
        Assert.True(after.Ok, after.ToString());
    }

    [Fact]
    public async Task RSDCL04_IntentosCriticosRepetidos_SuspendenLaEscritura()
    {
        IntegrationEnvironment.RequireDatabase();
        await using var server = await McpServerHarness.StartAsync();
        for (var i = 0; i < 3; i++)
        {
            var attempt = await server.CallAsync("execute_ddl", new { sql = "GRANT CONTROL ON SCHEMA::dbo TO public" });
            Assert.Equal("POLICY_BLOCKED", attempt.ErrorCode);
        }

        var write = await server.CallAsync("execute_dml", new { sql = "UPDATE ventas.Clientes SET Activo = Activo WHERE ClienteId = 1" });
        Assert.Equal("SESSION_SUSPENDED", write.ErrorCode);
        var read = await server.CallAsync("run_query", new { sql = "SELECT 1 AS uno" });
        Assert.True(read.Ok, read.ToString());
    }
}
