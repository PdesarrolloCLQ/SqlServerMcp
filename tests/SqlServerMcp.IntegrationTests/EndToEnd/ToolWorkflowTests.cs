namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>Flujos completos de las herramientas por protocolo MCP (rutas felices y salvaguardas principales).</summary>
[Collection(EndToEndCollection.Name)]
public sealed class ToolWorkflowTests(SharedServerFixture fixture)
{
    private McpServerHarness Server => fixture.Harness!;

    [Fact]
    public async Task ValidateSql_ClasificaSinEjecutar()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("validate_sql", new { sql = "DELETE FROM ventas.Clientes WHERE ClienteId = 1", targetTool = "executeDml" });
        Assert.True(call.Ok, call.ToString());
        Assert.Equal("Delete", call.Data!["category"]!.GetValue<string>());
        Assert.Equal("ALLOW", call.Data!["decision"]!.GetValue<string>());
        Assert.NotNull(call.Data!["note"]);

        var blocked = await Server.CallAsync("validate_sql", new { sql = "GRANT SELECT ON SCHEMA::ventas TO public" });
        Assert.True(blocked.Ok, blocked.ToString());
        Assert.Equal("BLOCK", blocked.Data!["decision"]!.GetValue<string>());
        Assert.StartsWith("POL-DCL", blocked.Data!["ruleId"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SampleTable_SinColumnasExcluidas()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("sample_table", new { table = "ventas.Usuarios", rows = 5 });
        Assert.True(call.Ok, call.ToString());
        var columns = call.Data!["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Login", columns);
        Assert.DoesNotContain("Contrasena", columns);
        var token = call.Data!["columns"]!.AsArray().First(c => c!["name"]!.GetValue<string>() == "Token")!;
        Assert.True(token["masked"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PlanEstimado_ConOperadores()
    {
        IntegrationEnvironment.RequireDatabase();
        var call = await Server.CallAsync("get_execution_plan", new { sql = "SELECT c.Nombre, SUM(f.Total) AS total FROM ventas.Clientes c JOIN ventas.Facturas f ON f.ClienteId = c.ClienteId GROUP BY c.Nombre" });
        Assert.True(call.Ok, call.ToString());
        Assert.True(call.Data!["estimatedCost"]!.GetValue<double>() > 0);
        Assert.NotEmpty(call.Data!["costlyOperators"]!.AsArray());
    }

    [Fact]
    public async Task InsertRows_TransaccionExplicita_YRollback()
    {
        IntegrationEnvironment.RequireDatabase();
        IntegrationEnvironment.ExecuteAdmin("""
            DROP TABLE IF EXISTS ia.E2EInsert;
            CREATE TABLE ia.E2EInsert (Id int IDENTITY(1,1) PRIMARY KEY, Nombre nvarchar(50) NOT NULL, Monto decimal(10,2) NULL);
            """);
        try
        {
            var inserted = await Server.CallAsync("insert_rows", new
            {
                table = "ia.E2EInsert",
                rows = new object[] { new { Nombre = "uno", Monto = 1.5 }, new { Nombre = "dos" } },
                returnInserted = true,
            });
            Assert.True(inserted.Ok, inserted.ToString());
            Assert.Equal(2, inserted.Structured["rowsAffected"]!.GetValue<long>());
            Assert.Equal(2, CountRows("ia.E2EInsert"));

            var identity = await Server.CallAsync("insert_rows", new { table = "ia.E2EInsert", rows = new object[] { new { Id = 99, Nombre = "x" } } });
            Assert.Equal("INVALID_ARGUMENT", identity.ErrorCode);

            var begin = await Server.CallAsync("begin_transaction");
            Assert.True(begin.Ok, begin.ToString());
            var insideTx = await Server.CallAsync("execute_dml", new { sql = "INSERT INTO ia.E2EInsert (Nombre) VALUES (@n)", parameters = new[] { new { name = "@n", value = "tx" } } });
            Assert.True(insideTx.Ok, insideTx.ToString());
            Assert.True(insideTx.Data!["inExplicitTransaction"]!.GetValue<bool>());
            var seen = await Server.CallAsync("run_query", new { sql = "SELECT COUNT(*) AS n FROM ia.E2EInsert" });
            Assert.Equal(3, seen.Data!["rows"]![0]![0]!.GetValue<long>());
            var rollback = await Server.CallAsync("rollback_transaction");
            Assert.True(rollback.Ok, rollback.ToString());
            Assert.Equal(2, CountRows("ia.E2EInsert"));
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.E2EInsert;");
        }
    }

    [Fact]
    public async Task Procedimiento_ConSalidaYValorDeRetorno()
    {
        IntegrationEnvironment.RequireDatabase();
        // Los procedimientos de solo lectura se ejecutan con el login lector: el DBA concede EXECUTE puntual (RF-PRC-03).
        IntegrationEnvironment.ExecuteAdmin("GRANT EXECUTE ON ventas.usp_ClientesPorSaldo TO rol_mcp_lectura;");
        var call = await Server.CallAsync("execute_procedure", new
        {
            procedure = "ventas.usp_ClientesPorSaldo",
            arguments = new object[] { new { name = "@SaldoMinimo", value = 3000 }, new { name = "@Cantidad", value = (object?)null, output = true } },
        });
        Assert.True(call.Ok, call.ToString());
        Assert.Equal(7, call.Data!["returnValue"]!.GetValue<int>());
        Assert.Equal("lectura", call.Data!["executionRole"]!.GetValue<string>());
        var resultRows = call.Data!["resultSets"]![0]!["rows"]!.AsArray().Count;
        Assert.True(resultRows > 0);

        var system = await Server.CallAsync("execute_procedure", new { procedure = "sys.sp_configure" });
        Assert.Equal("POLICY_BLOCKED", system.ErrorCode);
    }

    [Fact]
    public async Task Ddl_CreaConPermisoYEliminaConConfirmacion_RespaldandoDefinicion()
    {
        IntegrationEnvironment.RequireDatabase();
        IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.E2EDdl;");
        try
        {
            var create = await Server.CallAsync("execute_ddl", new { sql = "CREATE TABLE ia.E2EDdl (Id int NOT NULL PRIMARY KEY, Nombre nvarchar(20) NULL)" });
            Assert.True(create.Ok, create.ToString());
            Assert.True(create.Data!["executed"]!.GetValue<bool>());

            var wrongSchema = await Server.CallAsync("execute_ddl", new { sql = "CREATE TABLE ventas.NoPermitida (Id int)" });
            Assert.Equal("POLICY_BLOCKED", wrongSchema.ErrorCode);

            var preview = await Server.CallAsync("execute_ddl", new { sql = "DROP TABLE ia.E2EDdl", preview = true });
            Assert.True(preview.Ok, preview.ToString());
            Assert.Equal("CONFIRM", preview.Data!["decision"]!.GetValue<string>());
            Assert.False(preview.Data!["executed"]!.GetValue<bool>());

            Server.OnElicit = _ => McpServerHarness.Approve();
            var drop = await Server.CallAsync("execute_ddl", new { sql = "DROP TABLE ia.E2EDdl" });
            Assert.True(drop.Ok, drop.ToString());
            Assert.NotNull(drop.Data!["schemaChangeId"]);
            Assert.Contains(Server.ElicitationMessages, m => m.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase));

            var changes = McpServerHarness.ReadJsonLines(Server.AuditDirectory, "schema-changes-*.jsonl").ToList();
            Assert.Contains(changes, c => c["previousDefinition"]?.GetValue<string>()?.Contains("Nombre", StringComparison.Ordinal) == true);
            Assert.Contains(McpServerHarness.ReadJsonLines(Server.AuditDirectory, "alerts-*.jsonl"),
                a => a["code"]?.GetValue<string>() == "DROP_TABLE_EXECUTED" && a["severity"]?.GetValue<string>() == "Critical");
        }
        finally
        {
            Server.OnElicit = null;
            IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.E2EDdl;");
        }
    }

    [Fact]
    public async Task Metadatos_RespetanExclusiones()
    {
        IntegrationEnvironment.RequireDatabase();
        var tables = await Server.CallAsync("list_tables", new { schema = "ventas" });
        Assert.True(tables.Ok, tables.ToString());
        var names = tables.Data!["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Clientes", names);
        Assert.DoesNotContain("TarjetasCredito", names);

        var describe = await Server.CallAsync("describe_table", new { table = "ventas.Usuarios" });
        Assert.True(describe.Ok, describe.ToString());
        Assert.DoesNotContain(describe.Data!["columns"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "Contrasena");

        var hidden = await Server.CallAsync("describe_table", new { table = "seguridad.Claves" });
        Assert.Equal("NOT_FOUND", hidden.ErrorCode);
    }

    private static int CountRows(string table)
    {
        using var connection = IntegrationEnvironment.OpenAdminConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (int)command.ExecuteScalar()!;
    }

}
