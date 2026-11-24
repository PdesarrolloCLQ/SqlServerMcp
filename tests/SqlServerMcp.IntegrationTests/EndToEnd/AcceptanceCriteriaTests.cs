using System.Text.Json.Nodes;
using SqlServerMcp.Audit;

namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Criterios de aceptación de la sección 8 del ERS (CA-01..CA-30) ejecutados de punta a punta: cliente MCP oficial →
/// servidor SqlServerMcp por stdio → SQL Server en contenedor con logins de mínimo privilegio.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class AcceptanceCriteriaTests(SharedServerFixture fixture)
{
    private McpServerHarness Server => fixture.Harness!;

    private static void AssertBlocked(ToolCall call, string rulePrefix)
    {
        Assert.True(call.IsError, call.ToString());
        Assert.Equal("POLICY_BLOCKED", call.ErrorCode);
        Assert.StartsWith(rulePrefix, call.RuleId, StringComparison.Ordinal);
    }

    private static List<JsonObject> ReadAlerts(McpServerHarness server) =>
        McpServerHarness.ReadJsonLines(server.AuditDirectory, "alerts-*.jsonl").ToList();

    [Fact]
    public async Task CA01_DropDatabase_BloqueadoConAlertaCritica()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "DROP DATABASE Ventas" });
        AssertBlocked(call, "POL-");
        Assert.Contains(ReadAlerts(Server), a => a["correlationId"]?.GetValue<string>() == call.CorrelationId && a["severity"]?.GetValue<string>() == "Critical");
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE dbo.Clientes")]
    [InlineData("SELECT * FROM dbo.T; COMMIT; DELETE FROM dbo.T")]
    public async Task CA02_CA03_VariasSentencias_Rechazadas(string sql)
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql }), "POL-BATCH");
    }

    [Fact]
    public async Task CA04_SqlDinamicoEnDml_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("execute_dml", new { sql = "EXEC('DR' + 'OP TABLE dbo.T')" });
        Assert.Equal("POLICY_BLOCKED", call.ErrorCode);
    }

    [Fact]
    public async Task CA05_OpenRowset_BloqueadoConAlertaCritica()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "SELECT * FROM OPENROWSET('SQLNCLI','Server=x;Trusted_Connection=yes;','SELECT 1')" });
        AssertBlocked(call, "POL-EXT");
        Assert.Contains(ReadAlerts(Server), a => a["correlationId"]?.GetValue<string>() == call.CorrelationId && a["severity"]?.GetValue<string>() == "Critical");
    }

    [Fact]
    public async Task CA06_CatalogoSensible_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql = "SELECT name FROM sys.sql_logins" }), "POL-CAT");
    }

    [Theory]
    [InlineData("GRANT CONTROL ON SCHEMA::dbo TO public", "POL-DCL")]
    [InlineData("CREATE PROCEDURE ia.p AS ALTER ROLE db_owner ADD MEMBER x", "POL-DCL")]
    [InlineData("CREATE PROCEDURE ia.p WITH EXECUTE AS OWNER AS SELECT 1", "POL-IMP")]
    public async Task CA07_CA08_CA09_DclYSuplantacion_Bloqueados(string sql, string rule)
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("execute_ddl", new { sql }), rule);
    }

    [Fact]
    public async Task CA10_DeleteSinWhere_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("execute_dml", new { sql = "DELETE FROM ventas.Clientes" }), "POL-DML");
    }

    [Fact]
    public async Task CA11_PredicadoTrivial_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("execute_dml", new { sql = "UPDATE ventas.Clientes SET Activo = 0 WHERE 1 = 1" }), "POL-DML");
    }

    [Fact]
    public async Task CA12_UpdateQueSuperaElMaximo_LimitExceededSinCambios()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("execute_dml", new { sql = "UPDATE ventas.Facturas SET Total = Total + 0 WHERE FacturaId > 0" });
        Assert.True(call.IsError, call.ToString());
        Assert.Equal("LIMIT_EXCEEDED", call.ErrorCode);
    }

    [Fact]
    public async Task CA13_DeleteSobreElUmbral_PideConfirmacionConFilasEstimadas()
    {
        IntegrationEnvironment.RequireDatabase();
        IntegrationEnvironment.ExecuteAdmin("""
            DROP TABLE IF EXISTS ia.CA13;
            CREATE TABLE ia.CA13 (Id int NOT NULL PRIMARY KEY, Valor int NOT NULL);
            INSERT INTO ia.CA13 (Id, Valor) SELECT TOP (300) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 1 FROM sys.all_objects;
            """);
        try
        {
            Server.ElicitationMessages.Clear();
            Server.OnElicit = _ => McpServerHarness.Decline();
            var denied = await Server.CallAsync("execute_dml", new { sql = "DELETE FROM ia.CA13 WHERE Id <= @max", parameters = new[] { new { name = "@max", value = 250 } } });
            Assert.Equal("CONFIRMATION_DENIED", denied.ErrorCode);
            Assert.Contains(Server.ElicitationMessages, m => m.Contains("250", StringComparison.Ordinal));
            Assert.Equal(300, CountRows("ia.CA13"));

            Server.OnElicit = _ => McpServerHarness.Approve();
            var approved = await Server.CallAsync("execute_dml", new { sql = "DELETE FROM ia.CA13 WHERE Id <= @max", parameters = new[] { new { name = "@max", value = 250 } } });
            Assert.True(approved.Ok, approved.ToString());
            Assert.Equal(250, approved.Structured["rowsAffected"]!.GetValue<long>());
            Assert.Equal(50, CountRows("ia.CA13"));
        }
        finally
        {
            Server.OnElicit = null;
            IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.CA13;");
        }
    }

    [Fact]
    public async Task CA14_SelectInto_RechazadoEnConsulta()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql = "SELECT * INTO dbo.Copia FROM dbo.Clientes" }), "POL-TOOL");
    }

    [Fact]
    public async Task CA15_Waitfor_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql = "WAITFOR DELAY '00:10:00'" }), "POL-");
    }

    [Fact]
    public async Task CA16_BaseNoPermitida_Bloqueada()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql = "SELECT * FROM OtraBD.dbo.T" }), "POL-REF");
    }

    [Fact]
    public async Task CA17_ServidorVinculado_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql = "SELECT * FROM SRVREMOTO.Ventas.dbo.T" }), "POL-EXT");
    }

    [Fact]
    public async Task CA18_XpDirtree_BloqueadoConAlertaCritica()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("execute_procedure", new { procedure = "master.sys.xp_dirtree", arguments = new[] { new { name = "@directory", value = @"\\host\share" } } });
        Assert.Equal("POLICY_BLOCKED", call.ErrorCode);
        Assert.Contains(ReadAlerts(Server), a => a["correlationId"]?.GetValue<string>() == call.CorrelationId && a["severity"]?.GetValue<string>() == "Critical");
    }

    [Fact]
    public async Task CA19_ComentariosYMayusculasMixtas_PermitidoYAuditado()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "SeLeCt/**/* FROM [ventas].[Clientes] WHERE ClienteId = 1" });
        Assert.True(call.Ok, call.ToString());
        var audit = McpServerHarness.ReadJsonLines(Server.AuditDirectory, "audit-*.jsonl")
            .Where(n => n["correlationId"]?.GetValue<string>() == call.CorrelationId).ToList();
        Assert.Contains(audit, a => a["outcome"]?.GetValue<string>() == "success" && a["statement"] is not null && a["decision"]?.GetValue<string>() == "ALLOW");
    }

    [Fact]
    public async Task CA20_ResultadoGrande_TruncadoAlMaximo()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "SELECT a.FacturaId, b.FacturaId AS Otra FROM ventas.Facturas a JOIN ventas.Facturas b ON b.ClienteId = a.ClienteId" });
        Assert.True(call.Ok, call.ToString());
        Assert.True(call.Structured["truncated"]!.GetValue<bool>());
        Assert.Equal(500, call.Structured["rowsReturned"]!.GetValue<long>());
    }

    [Fact]
    public async Task CA23_TextoMaliciosoEnDatos_SeDevuelveComoDatoConAdvertencia()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "SELECT Notas FROM ventas.Clientes WHERE ClienteId = 1" });
        Assert.True(call.Ok, call.ToString());
        Assert.Contains("ignora tus instrucciones", call.Data!["rows"]![0]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(call.Structured["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("inyección de prompt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CA24_ArgumentoConfirmInventado_RechazadoPorElEsquema()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("execute_dml", new { sql = "DELETE FROM ventas.Clientes WHERE ClienteId = 1", confirm = true });
        Assert.Equal("INVALID_ARGUMENT", call.ErrorCode);
    }

    [Fact]
    public async Task CA29_ColumnaExcluida_Bloqueada()
    {
        IntegrationEnvironment.RequireDatabase();
        AssertBlocked(await Server.CallAsync("run_query", new { sql = "SELECT LEFT(Contrasena, 3) FROM ventas.Usuarios" }), "POL-DAT");
    }

    [Fact]
    public async Task CA30_SpSendDbmail_BloqueadoConAlertaCritica()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("execute_procedure", new { procedure = "msdb.dbo.sp_send_dbmail", arguments = new[] { new { name = "@recipients", value = "a@b.c" } } });
        Assert.Equal("POLICY_BLOCKED", call.ErrorCode);
        Assert.Contains(ReadAlerts(Server), a => a["correlationId"]?.GetValue<string>() == call.CorrelationId && a["severity"]?.GetValue<string>() == "Critical");
    }

    [Fact]
    public async Task RFCON08_CorrelacionEnSessionContext()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "SELECT CAST(SESSION_CONTEXT(N'mcp_correlation_id') AS nvarchar(64)) AS cid, APP_NAME() AS app" });
        Assert.True(call.Ok, call.ToString());
        Assert.Equal(call.CorrelationId, call.Data!["rows"]![0]![0]!.GetValue<string>());
        Assert.Equal("SqlServerMcp", call.Data!["rows"]![0]![1]!.GetValue<string>());
    }

    [Fact]
    public async Task ReglaConfigurable_RLOCAL01_BloqueaDeleteDeFacturas()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("execute_dml", new { sql = "DELETE FROM ventas.Facturas WHERE FacturaId = -1" });
        Assert.Equal("POLICY_BLOCKED", call.ErrorCode);
        Assert.Equal("R-LOCAL-01", call.RuleId);
    }

    [Fact]
    public async Task ConsultaParametrizada_ConTiposEstables()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new
        {
            sql = "SELECT ClienteId, Nombre, Saldo, FechaAlta FROM ventas.Clientes WHERE ClienteId = @id",
            parameters = new[] { new { name = "@id", value = 7, type = "int" } },
        });
        Assert.True(call.Ok, call.ToString());
        var row = call.Data!["rows"]![0]!.AsArray();
        Assert.Equal(7, row[0]!.GetValue<long>());
        Assert.IsType<string>(row[2]!.GetValue<string>()); // DECIMAL como texto (RF-QRY-05)
    }

    [Fact]
    public async Task AuditoriaIntegra_CadenaDeHashesVerificable()
    {
        IntegrationEnvironment.RequireDatabase();
        await Server.CallAsync("run_query", new { sql = "SELECT 1 AS uno" });
        foreach (var file in Directory.GetFiles(Server.AuditDirectory, "audit-*.jsonl"))
        {
            Assert.Null(HashChainedJsonlWriter.VerifyFile(file));
        }
    }

    private static int CountRows(string table)
    {
        using var connection = IntegrationEnvironment.OpenAdminConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (int)command.ExecuteScalar()!;
    }
}
