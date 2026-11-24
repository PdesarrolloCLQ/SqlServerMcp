namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Invoca cada herramienta publicada al menos una vez. El arnés valida structuredContent contra el outputSchema
/// de la herramienta (como Claude Code y otros clientes): si no coincide, el cliente descarta la respuesta.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class OutputSchemaTests(SharedServerFixture fixture)
{
    private McpServerHarness Server => fixture.Harness!;

    [Fact]
    public async Task Servidor_YMetadatos_CumplenSuEsquemaDeSalida()
    {
        IntegrationEnvironment.RequireDatabase();
        await Ok("get_server_policy");
        await Ok("test_connection");
        await Ok("list_databases");
        await Ok("list_schemas");
        await Ok("list_tables", new { schema = "ventas" });
        await Ok("describe_table", new { table = "ventas.Clientes" });
        await Ok("describe_table", new { table = "ventas.vClientesActivos" });
        await Ok("list_routines", new { schema = "ventas" });
        await Ok("describe_routine", new { routine = "ventas.usp_ClientesPorSaldo" });
        await Ok("describe_routine", new { routine = "ventas.fn_TotalCliente" });
        await Ok("get_object_definition", new { name = "ventas.vClientesActivos" });
        await Ok("list_indexes", new { table = "ventas.Facturas" });
        await Ok("get_relationships", new { table = "ventas.Facturas" });
        await Ok("get_dependencies", new { name = "ventas.Clientes" });
        await Ok("search_objects", new { text = "Cliente" });
        await Ok("get_table_stats", new { schema = "ventas" });

        // Respuestas de error: también deben cumplir el esquema.
        var missing = await Server.CallAsync("get_object_definition", new { name = "ventas.NoExiste" });
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task ListObjects_TodosLosTipos_CumplenSuEsquemaDeSalida()
    {
        IntegrationEnvironment.RequireDatabase();
        IntegrationEnvironment.ExecuteAdmin("""
            DROP TRIGGER IF EXISTS ia.trg_E2EObjetos;
            DROP TABLE IF EXISTS ia.E2EObjetos;
            DROP SYNONYM IF EXISTS ia.E2ESinonimo;
            DROP SEQUENCE IF EXISTS ia.E2ESecuencia;
            DROP TYPE IF EXISTS ia.E2ETipo;
            CREATE TABLE ia.E2EObjetos (Id int NOT NULL PRIMARY KEY);
            CREATE SEQUENCE ia.E2ESecuencia AS int START WITH 1;
            CREATE SYNONYM ia.E2ESinonimo FOR ventas.Clientes;
            CREATE TYPE ia.E2ETipo FROM nvarchar(20) NULL;
            """);
        IntegrationEnvironment.ExecuteAdmin("CREATE TRIGGER ia.trg_E2EObjetos ON ia.E2EObjetos AFTER INSERT AS SET NOCOUNT ON;");
        try
        {
            foreach (var kind in new[] { "trigger", "sequence", "synonym", "type" })
            {
                var call = await Ok("list_objects", new { kind, schema = "ia" });
                Assert.NotEmpty(call.Data!["items"]!.AsArray());
            }
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin("""
                DROP TRIGGER IF EXISTS ia.trg_E2EObjetos;
                DROP TABLE IF EXISTS ia.E2EObjetos;
                DROP SYNONYM IF EXISTS ia.E2ESinonimo;
                DROP SEQUENCE IF EXISTS ia.E2ESecuencia;
                DROP TYPE IF EXISTS ia.E2ETipo;
                """);
        }
    }

    [Fact]
    public async Task TransaccionConCommit_CumpleSuEsquemaDeSalida()
    {
        IntegrationEnvironment.RequireDatabase();
        IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.E2ECommit; CREATE TABLE ia.E2ECommit (Id int IDENTITY(1,1) PRIMARY KEY, Nombre nvarchar(20) NOT NULL);");
        try
        {
            await Ok("begin_transaction");
            await Ok("execute_dml", new { sql = "INSERT INTO ia.E2ECommit (Nombre) VALUES (@n)", parameters = new[] { new { name = "@n", value = "commit" } } });
            await Ok("commit_transaction");
            var count = await Ok("run_query", new { sql = "SELECT COUNT(*) AS n FROM ia.E2ECommit" });
            Assert.Equal(1, count.Data!["rows"]![0]![0]!.GetValue<long>());
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin("DROP TABLE IF EXISTS ia.E2ECommit;");
        }
    }

    private async Task<ToolCall> Ok(string tool, object? arguments = null)
    {
        var call = await Server.CallAsync(tool, arguments);
        Assert.True(call.Ok, $"{tool}: {call}");
        return call;
    }
}
