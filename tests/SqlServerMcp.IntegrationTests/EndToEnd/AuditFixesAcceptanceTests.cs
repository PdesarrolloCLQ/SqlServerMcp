using System.Text.Json.Nodes;

namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Pruebas de aceptación de la auditoría de seguridad, por protocolo MCP contra SQL Server real: evasión por sinónimos
/// (A01–A06, A12, A13), columnas sensibles y PII (A07–A10, A14), umbral acumulado en transacciones (A11, A15) y
/// regresiones que deben comportarse exactamente igual que antes (R01–R07). Los objetos se crean en el esquema de
/// trabajo "ia", el único autorizado para DDL en el perfil de pruebas.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class AuditFixesAcceptanceTests(SharedServerFixture fixture)
{
    private McpServerHarness Server => fixture.Harness!;

    private const string Setup = """
        DROP SYNONYM IF EXISTS ia.synExterno; DROP SYNONYM IF EXISTS ia.sProd;
        DROP SYNONYM IF EXISTS ia.s1; DROP SYNONYM IF EXISTS ia.s2; DROP SYNONYM IF EXISTS ia.s3; DROP SYNONYM IF EXISTS ia.s4;
        DROP PROCEDURE IF EXISTS ia.pDinamico; DROP PROCEDURE IF EXISTS ia.pUsaSyn;
        DROP TABLE IF EXISTS ia.AudT; DROP TABLE IF EXISTS ia.AudProducto; DROP TABLE IF EXISTS ia.AudCarga; DROP TABLE IF EXISTS ia.AudDrop;
        CREATE TABLE ia.AudT (Id int NOT NULL PRIMARY KEY, SSN nvarchar(20) NULL, ApiKey nvarchar(50) NULL, Email nvarchar(100) NULL,
            Telefono nvarchar(30) NULL, ShippingMethod nvarchar(20) NULL, MappingId int NULL, TokenizerName nvarchar(20) NULL);
        INSERT INTO ia.AudT VALUES (1, N'123-45-6789', N'key-abc', N'ana@example.com', N'555-1234', N'aereo', 7, N'bpe');
        CREATE TABLE ia.AudProducto (ProductoId int NOT NULL PRIMARY KEY, SKU nvarchar(20) NOT NULL);
        INSERT INTO ia.AudProducto VALUES (1, N'SKU-1'), (2, N'SKU-2');
        CREATE TABLE ia.AudCarga (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, Valor int NOT NULL);
        CREATE TABLE ia.AudDrop (Id int NOT NULL PRIMARY KEY, Sobra int NULL);
        CREATE SYNONYM ia.synExterno FOR sp_executesql;
        """;

    private const string Cleanup = """
        DROP SYNONYM IF EXISTS ia.synExterno; DROP SYNONYM IF EXISTS ia.sProd;
        DROP SYNONYM IF EXISTS ia.s1; DROP SYNONYM IF EXISTS ia.s2; DROP SYNONYM IF EXISTS ia.s3; DROP SYNONYM IF EXISTS ia.s4;
        DROP PROCEDURE IF EXISTS ia.pDinamico; DROP PROCEDURE IF EXISTS ia.pUsaSyn;
        DROP TABLE IF EXISTS ia.AudT; DROP TABLE IF EXISTS ia.AudProducto; DROP TABLE IF EXISTS ia.AudCarga; DROP TABLE IF EXISTS ia.AudDrop;
        """;

    private static readonly string InsertNinety = "INSERT INTO ia.AudCarga (Valor) SELECT TOP (90) FacturaId FROM ventas.Facturas ORDER BY FacturaId";

    // ------------------------------------------------------------------ 1. Sinónimos

    [Theory]
    [InlineData("CREATE SYNONYM ia.s1 FOR sp_executesql", "POL-DYN-001")]
    [InlineData("CREATE SYNONYM ia.s2 FOR xp_cmdshell", "POL-EXT-003")]
    [InlineData("CREATE SYNONYM ia.s3 FOR dbo.sp_executesql", "POL-DYN-001")]
    [InlineData("CREATE SYNONYM ia.s4 FOR sp_OACreate", "POL-")]
    public async Task A01a04_CreateSynonymHaciaObjetoDelSistema_Bloqueado(string sql, string rulePrefix)
    {
        IntegrationEnvironment.RequireDatabase();
        await AssertBlockedEverywhere(sql, "executeDdl", "execute_ddl", rulePrefix);
    }

    [Fact]
    public async Task A05_ProcedimientoQueUsaSinonimoCreadoFueraDelMcp_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            // Perfil sin lista de procedimientos (como en la auditoría): el EXEC del sinónimo no lo frena la lista de
            // permitidos, solo la resolución del sinónimo hacia sp_executesql.
            var dir = McpServerHarness.NewDirectory();
            await using var server = await McpServerHarness.StartAsync(McpServerHarness.DefaultConfig(dir, d => d.Remove("procedures")), directory: dir);
            await AssertBlockedEverywhere(server, "CREATE PROCEDURE ia.pUsaSyn @s nvarchar(max) AS EXEC ia.synExterno @s", "executeDdl", "execute_ddl", "POL-DYN-001");
            Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.procedures WHERE object_id = OBJECT_ID(N'ia.pUsaSyn')"));
        });
    }

    [Fact]
    public async Task A06_ExecuteProcedureDeModuloCreadoFueraDelMcpConExecDeCadena_Bloqueado()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            IntegrationEnvironment.ExecuteAdmin("CREATE PROCEDURE ia.pDinamico @sql nvarchar(max) AS EXEC(@sql);");

            // Perfil sin lista de procedimientos: hoy significa "cualquier procedimiento de los esquemas permitidos".
            var dir = McpServerHarness.NewDirectory();
            var config = McpServerHarness.DefaultConfig(dir, d => d.Remove("procedures"));
            await using var server = await McpServerHarness.StartAsync(config, directory: dir);
            var call = await server.CallAsync("execute_procedure", new
            {
                procedure = "ia.pDinamico",
                arguments = new object[] { new { name = "@sql", value = "DROP TABLE ia.AudDrop" } },
            });
            Assert.Equal("POLICY_BLOCKED", call.ErrorCode);
            Assert.Equal("POL-DYN-001", call.RuleId);
            Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'ia.AudDrop')"));

            var policy = await server.CallAsync("get_server_policy");
            Assert.Contains("Lista vacía", policy.Data!["profile"]!["proceduresNote"]!.GetValue<string>(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A12_A13_SinonimoHaciaTablaDeUsuario_PermitidoYUtilizable()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            var validation = await Server.CallAsync("validate_sql", new { sql = "CREATE SYNONYM ia.sProd FOR ia.AudProducto", targetTool = "executeDdl" });
            Assert.Equal("ALLOW", validation.Data!["decision"]!.GetValue<string>());

            var create = await Server.CallAsync("execute_ddl", new { sql = "CREATE SYNONYM ia.sProd FOR ia.AudProducto" });
            Assert.True(create.Ok, create.ToString());

            var query = await Server.CallAsync("run_query", new { sql = "SELECT ProductoId, SKU FROM ia.sProd WHERE ProductoId = 1" });
            Assert.True(query.Ok, query.ToString());
            Assert.Equal("SKU-1", query.Data!["rows"]![0]![1]!.GetValue<string>());
        });
    }

    // ------------------------------------------------------------------ 2. Columnas sensibles y PII

    [Fact]
    public async Task A07_A08_FiltroYEscrituraSobreColumnaSensible_Bloqueados()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            await AssertBlockedEverywhere("SELECT SSN FROM ia.AudT WHERE SSN = '1'", "runQuery", "run_query", "POL-DAT");
            await AssertBlockedEverywhere("UPDATE ia.AudT SET ApiKey = N'x' WHERE Id = 1", "executeDml", "execute_dml", "POL-DAT");
            Assert.Equal(1, Count("SELECT COUNT(*) FROM ia.AudT WHERE ApiKey = N'key-abc'"));
        });
    }

    [Fact]
    public async Task A09_A10_SecretosYPii_SeEnmascaran()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            var secrets = await Server.CallAsync("run_query", new { sql = "SELECT Id, SSN, ApiKey FROM ia.AudT" });
            Assert.True(secrets.Ok, secrets.ToString());
            AssertMasked(secrets, "SSN", "ApiKey");
            Assert.False(Column(secrets, "Id")["masked"]!.GetValue<bool>());

            var pii = await Server.CallAsync("run_query", new { sql = "SELECT Email, Telefono FROM ia.AudT" });
            Assert.True(pii.Ok, pii.ToString());
            AssertMasked(pii, "Email", "Telefono");

            // La PII enmascarada se puede usar en filtros (buscar por correo es legítimo).
            var byEmail = await Server.CallAsync("run_query", new { sql = "SELECT Id FROM ia.AudT WHERE Email = @e", parameters = new[] { new { name = "@e", value = "ana@example.com" } } });
            Assert.True(byEmail.Ok, byEmail.ToString());
            Assert.Single(byEmail.Data!["rows"]!.AsArray());
        });
    }

    [Fact]
    public async Task A14_DescribeTable_MarcasSensibleYPii_SinFalsosPositivos()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            var call = await Server.CallAsync("describe_table", new { table = "ia.AudT" });
            Assert.True(call.Ok, call.ToString());
            var columns = call.Data!["columns"]!.AsArray().ToDictionary(c => c!["name"]!.GetValue<string>(), c => c!);
            foreach (var name in new[] { "ShippingMethod", "MappingId", "TokenizerName" })
            {
                Assert.False(columns[name]["isSensitive"]!.GetValue<bool>(), name);
            }

            Assert.True(columns["SSN"]["isSensitive"]!.GetValue<bool>());
            Assert.True(columns["ApiKey"]["isSensitive"]!.GetValue<bool>());
            Assert.True(columns["Email"]["isPii"]!.GetValue<bool>());
            Assert.True(columns["Telefono"]["isPii"]!.GetValue<bool>());

            var policy = await Server.CallAsync("get_server_policy");
            var protection = policy.Data!["dataProtection"]!;
            Assert.Contains(protection["sensitivePatterns"]!.AsArray(), p => p!.GetValue<string>() == "ssn");
            Assert.Contains(protection["piiPatterns"]!.AsArray(), p => p!.GetValue<string>() == "email");
            Assert.Equal("mask", protection["piiAction"]!.GetValue<string>());
        });
    }

    // ------------------------------------------------------------------ 3. Umbral acumulado en transacciones

    [Fact]
    public async Task A11_DosInsertsDe90EnTransaccion_ElSegundoRequiereConfirmacion()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            Server.ElicitationMessages.Clear();
            Server.OnElicit = _ => McpServerHarness.Decline();
            try
            {
                Assert.True((await Server.CallAsync("begin_transaction")).Ok);
                var first = await Server.CallAsync("execute_dml", new { sql = InsertNinety });
                Assert.True(first.Ok, first.ToString());
                Assert.Contains(first.Structured["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("Acumulado en la transacción: 90", StringComparison.Ordinal));

                var second = await Server.CallAsync("execute_dml", new { sql = InsertNinety });
                Assert.Equal("CONFIRMATION_DENIED", second.ErrorCode);
                Assert.NotEmpty(Server.ElicitationMessages);
            }
            finally
            {
                await Server.CallAsync("rollback_transaction");
                Server.OnElicit = null;
            }

            Assert.Equal(0, Count("SELECT COUNT(*) FROM ia.AudCarga"));

            // Cliente sin elicitation: el segundo INSERT se rechaza sin ejecutarse.
            await using var noElicitation = await McpServerHarness.StartAsync(supportsElicitation: false);
            Assert.True((await noElicitation.CallAsync("begin_transaction")).Ok);
            Assert.True((await noElicitation.CallAsync("execute_dml", new { sql = InsertNinety })).Ok);
            var rejected = await noElicitation.CallAsync("execute_dml", new { sql = InsertNinety });
            Assert.Equal("CONFIRMATION_UNAVAILABLE", rejected.ErrorCode);
            Assert.Equal("POL-DML-TX-THRESHOLD", rejected.RuleId);
            await noElicitation.CallAsync("rollback_transaction");
        });
    }

    [Fact]
    public async Task A15_DosInsertsDe90FueraDeTransaccion_AmbosPermitidos()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            Server.OnElicit = _ => McpServerHarness.Decline();
            try
            {
                Assert.True((await Server.CallAsync("execute_dml", new { sql = InsertNinety })).Ok);
                Assert.True((await Server.CallAsync("execute_dml", new { sql = InsertNinety })).Ok);
            }
            finally
            {
                Server.OnElicit = null;
            }

            Assert.Equal(180, Count("SELECT COUNT(*) FROM ia.AudCarga"));
        });
    }

    // ------------------------------------------------------------------ Regresión

    [Theory]
    [InlineData("DELETE FROM ventas.Clientes", "executeDml", "POL-DML-001")]
    [InlineData("UPDATE ventas.Clientes SET Activo = 1", "executeDml", "POL-DML-001")]
    [InlineData("DELETE FROM ventas.Clientes WHERE 2 > 1 OR ClienteId = -1", "executeDml", "POL-DML-002")]
    [InlineData("SELECT 1 AS a; SELECT 2 AS b", "runQuery", "POL-BATCH-001")]
    [InlineData("SELECT 1 AS a SELECT 2 AS b", "runQuery", "POL-BATCH-001")]
    [InlineData("SELECT 1 AS a /* comentario */ ; -- otro\nSELECT 2 AS b", "runQuery", "POL-BATCH-001")]
    [InlineData("EXEC sp_executesql N'SELECT 1'", "any", "POL-DYN")]
    [InlineData("EXEC (@sql)", "any", "POL-DYN")]
    [InlineData("EXEC @procedimiento", "any", "POL-")]
    [InlineData("CREATE PROCEDURE ia.pR1 @s nvarchar(max) AS EXEC(@s)", "executeDdl", "POL-DYN-001")]
    [InlineData("CREATE PROCEDURE ia.pR2 @s nvarchar(max) AS EXEC sp_executesql @s", "executeDdl", "POL-DYN-001")]
    [InlineData("EXEC xp_cmdshell 'dir'", "any", "POL-EXT-003")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'c:\\x.txt', SINGLE_CLOB) AS t", "runQuery", "POL-EXT-001")]
    [InlineData("CREATE PROCEDURE ia.pR3 WITH EXECUTE AS OWNER AS SELECT 1 AS uno", "executeDdl", "POL-IMP-003")]
    [InlineData("GRANT SELECT ON SCHEMA::ventas TO public", "any", "POL-DCL")]
    [InlineData("WAITFOR DELAY '00:00:01'", "any", "POL-WAIT-001")]
    [InlineData("CREATE SYNONYM ia.sR1 FOR master.sys.sql_logins", "executeDdl", "POL-CAT-001")]
    [InlineData("CREATE SYNONYM ia.sR2 FOR OtraBD.dbo.Tabla", "executeDdl", "POL-REF-001")]
    public async Task R01aR05_SiguenBloqueados(string sql, string targetTool, string rulePrefix)
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("validate_sql", new { sql, targetTool });
        Assert.True(call.Ok, call.ToString());
        Assert.Equal("BLOCK", call.Data!["decision"]!.GetValue<string>());
        Assert.StartsWith(rulePrefix, call.Data!["ruleId"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R06_DropTriggerYDropColumn_ConfirmanYSeRechazanSinElicitation()
    {
        IntegrationEnvironment.RequireDatabase();
        await WithObjects(async () =>
        {
            string[] statements =
            [
                "DROP TABLE ia.AudDrop",
                "CREATE TRIGGER ia.trgAud ON ia.AudDrop AFTER INSERT AS SET NOCOUNT ON",
                "ALTER TABLE ia.AudDrop DROP COLUMN Sobra",
            ];
            foreach (var sql in statements)
            {
                var validation = await Server.CallAsync("validate_sql", new { sql, targetTool = "executeDdl" });
                Assert.Equal("CONFIRM", validation.Data!["decision"]!.GetValue<string>());
            }

            await using var server = await McpServerHarness.StartAsync(supportsElicitation: false);
            foreach (var sql in statements)
            {
                var call = await server.CallAsync("execute_ddl", new { sql });
                Assert.Equal("CONFIRMATION_UNAVAILABLE", call.ErrorCode);
            }

            Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'ia.AudDrop') AND name = N'Sobra'"));
        });
    }

    [Fact]
    public async Task R07_DeteccionDeInyeccionEnResultados_SeMantiene()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("run_query", new { sql = "SELECT Notas FROM ventas.Clientes WHERE ClienteId = 1" });
        Assert.True(call.Ok, call.ToString());
        Assert.Contains(call.Structured["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("inyección de prompt", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Auxiliares

    /// <summary>La misma sentencia debe bloquearse en validate_sql y al ejecutarla.</summary>
    private Task AssertBlockedEverywhere(string sql, string targetTool, string executeTool, string rulePrefix) =>
        AssertBlockedEverywhere(Server, sql, targetTool, executeTool, rulePrefix);

    private static async Task AssertBlockedEverywhere(McpServerHarness server, string sql, string targetTool, string executeTool, string rulePrefix)
    {
        var validation = await server.CallAsync("validate_sql", new { sql, targetTool });
        Assert.True(validation.Ok, validation.ToString());
        Assert.Equal("BLOCK", validation.Data!["decision"]!.GetValue<string>());
        Assert.StartsWith(rulePrefix, validation.Data!["ruleId"]!.GetValue<string>(), StringComparison.Ordinal);

        var execution = await server.CallAsync(executeTool, new { sql });
        Assert.Equal("POLICY_BLOCKED", execution.ErrorCode);
        Assert.StartsWith(rulePrefix, execution.RuleId, StringComparison.Ordinal);
        Assert.Equal(validation.Data!["ruleId"]!.GetValue<string>(), execution.RuleId);
    }

    private static async Task WithObjects(Func<Task> test)
    {
        IntegrationEnvironment.ExecuteAdmin(Setup);
        try
        {
            await test();
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin(Cleanup);
        }
    }

    private static JsonNode Column(ToolCall call, string name) =>
        call.Data!["columns"]!.AsArray().First(c => c!["name"]!.GetValue<string>() == name)!;

    private static void AssertMasked(ToolCall call, params string[] names)
    {
        var columns = call.Data!["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        foreach (var name in names)
        {
            Assert.True(Column(call, name)["masked"]!.GetValue<bool>(), name);
            var value = call.Data!["rows"]![0]![columns.IndexOf(name)]!.GetValue<string>();
            Assert.DoesNotContain(value, new[] { "123-45-6789", "key-abc", "ana@example.com", "555-1234" });
        }
    }

    private static int Count(string sql)
    {
        using var connection = IntegrationEnvironment.OpenAdminConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
