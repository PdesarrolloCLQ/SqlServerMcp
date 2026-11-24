using System.Text.Json;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.IntegrationTests.Write;

/// <summary>RF-PRC-01..04 contra los procedimientos de VentasTest.</summary>
public class ProcedureExecutorIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientesPorSaldo_ResultadoSalidaYRetorno()
    {
        await using var h = WriteHarness.Create();
        var expected = ScratchTable.Scalar<int>("SELECT COUNT(*) FROM ventas.Clientes WHERE Saldo >= 2900");

        // El lector de mínimo privilegio no tiene EXECUTE sobre ventas: se usa el rol de escritura (sin efectos).
        var result = await h.Procedures.ExecuteAsync(new ProcedureExecutionRequest
        {
            Profile = h.Profile,
            Procedure = new SqlObjectName(null, null, "ventas", "usp_ClientesPorSaldo"),
            Arguments = new Dictionary<string, ProcedureArgument>
            {
                ["SaldoMinimo"] = new() { Value = Json("2900") },
                ["@cantidad"] = new() { Output = true },
            },
            Evaluation = WriteSql.ForProcedure(),
            CorrelationId = "prc-saldo",
            Role = ConnectionRole.Write,
        }, Ct);

        Assert.Equal(7, result.ReturnValue);
        var set = Assert.Single(result.ResultSets);
        Assert.Equal(["ClienteId", "Nombre", "Saldo"], set.Columns.Select(c => c.Name));
        Assert.Equal(expected, set.Rows.Count);
        Assert.Equal((long)expected, result.OutputParameters["@Cantidad"]);
    }

    [Fact]
    public async Task RolLectura_SiempreRevierteAunqueElProcedimientoEscriba()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(20);
        var procedure = "usp_" + table.Name;
        IntegrationEnvironment.ExecuteAdmin(
            $"CREATE PROCEDURE ia.{procedure} @Minimo int, @Cantidad int OUTPUT AS BEGIN SET NOCOUNT ON; " +
            $"UPDATE {table.Qualified} SET Valor = 9 WHERE Id >= @Minimo; " +
            $"SELECT Id, Valor FROM {table.Qualified} WHERE Id >= @Minimo ORDER BY Id; SET @Cantidad = @@ROWCOUNT; RETURN 7; END");
        // Permiso solo sobre este procedimiento de prueba; por encadenamiento de propiedad podría escribir en la tabla.
        IntegrationEnvironment.ExecuteAdmin($"GRANT EXECUTE ON ia.{procedure} TO mcp_lector;");
        try
        {
            var result = await h.Procedures.ExecuteAsync(new ProcedureExecutionRequest
            {
                Profile = h.Profile,
                Procedure = new SqlObjectName(null, null, "ia", procedure),
                Arguments = new Dictionary<string, ProcedureArgument>
                {
                    ["Minimo"] = new() { Value = Json("16") },
                    ["Cantidad"] = new() { Output = true },
                },
                Evaluation = WriteSql.ForProcedure(),
                CorrelationId = "prc-read",
                Role = ConnectionRole.Read,
            }, Ct);

            Assert.Equal(7, result.ReturnValue);
            Assert.False(result.Committed);
            var set = Assert.Single(result.ResultSets);
            Assert.Equal(5, set.Rows.Count);
            Assert.Equal(9L, set.Rows[0][1]);
            Assert.Equal(5L, result.OutputParameters["@Cantidad"]);
            Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin($"DROP PROCEDURE IF EXISTS ia.{procedure};");
        }
    }

    [Fact]
    public async Task LimitaFilasPorConjunto()
    {
        await using var h = WriteHarness.Create(c => c.Limits.MaxRows = 5);
        var result = await h.Procedures.ExecuteAsync(new ProcedureExecutionRequest
        {
            Profile = h.Profile,
            Procedure = new SqlObjectName(null, null, "ventas", "usp_ClientesPorSaldo"),
            Arguments = new Dictionary<string, ProcedureArgument>
            {
                ["@SaldoMinimo"] = new() { Value = Json("0") },
                ["Cantidad"] = new() { Output = true },
            },
            Evaluation = WriteSql.ForProcedure(),
            CorrelationId = "prc-max",
            Role = ConnectionRole.Write,
        }, Ct);

        var set = Assert.Single(result.ResultSets);
        Assert.Equal(5, set.Rows.Count);
        Assert.True(set.Truncated);
        Assert.Equal(300L, result.OutputParameters["@Cantidad"]);
    }

    [Fact]
    public async Task EscrituraConTransaccionGestionadaConfirmada()
    {
        await using var h = WriteHarness.Create();
        // ClienteId 10 ya está inactivo en los datos de prueba: el procedimiento no altera los datos compartidos.
        Assert.Equal(0, ScratchTable.Scalar<int>("SELECT CAST(Activo AS int) FROM ventas.Clientes WHERE ClienteId = 10"));

        var result = await h.Procedures.ExecuteAsync(new ProcedureExecutionRequest
        {
            Profile = h.Profile,
            Procedure = new SqlObjectName(null, null, "ventas", "usp_DesactivarCliente"),
            Arguments = new Dictionary<string, ProcedureArgument> { ["ClienteId"] = new() { Value = Json("10") } },
            Evaluation = WriteSql.ForProcedure(),
            CorrelationId = "prc-write",
            Role = ConnectionRole.Write,
        }, Ct);

        Assert.True(result.Committed);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(0, result.ReturnValue);
        Assert.Empty(result.ResultSets);
    }

    [Fact]
    public async Task EscrituraQueSuperaElMaximo_SeRevierte()
    {
        await using var h = WriteHarness.Create(c => c.Limits.MaxAffectedRows = 10);
        using var table = ScratchTable.WithNumbers(50);
        var procedure = "usp_" + table.Name;
        IntegrationEnvironment.ExecuteAdmin($"CREATE PROCEDURE ia.{procedure} AS UPDATE {table.Qualified} SET Valor = 1;");
        try
        {
            var ex = await Assert.ThrowsAsync<ToolFailureException>(() => h.Procedures.ExecuteAsync(new ProcedureExecutionRequest
            {
                Profile = h.Profile,
                Procedure = new SqlObjectName(null, null, "ia", procedure),
                Evaluation = WriteSql.ForProcedure(),
                CorrelationId = "prc-limit",
                Role = ConnectionRole.Write,
            }, Ct));
            Assert.Equal(ErrorCodes.LimitExceeded, ex.Code);
            Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin($"DROP PROCEDURE IF EXISTS ia.{procedure};");
        }
    }

    [Fact]
    public async Task ArgumentosInvalidos()
    {
        await using var h = WriteHarness.Create();
        var unknown = await Assert.ThrowsAsync<ToolFailureException>(() => h.Procedures.ExecuteAsync(Request(h, "usp_ClientesPorSaldo",
            ("NoExiste", new ProcedureArgument { Value = Json("1") })), Ct));
        Assert.Equal(ErrorCodes.InvalidArgument, unknown.Code);
        Assert.Contains("NoExiste", unknown.Message, StringComparison.Ordinal);

        var notOutput = await Assert.ThrowsAsync<ToolFailureException>(() => h.Procedures.ExecuteAsync(Request(h, "usp_ClientesPorSaldo",
            ("SaldoMinimo", new ProcedureArgument { Value = Json("1"), Output = true })), Ct));
        Assert.Equal(ErrorCodes.InvalidArgument, notOutput.Code);

        var duplicated = await Assert.ThrowsAsync<ToolFailureException>(() => h.Procedures.ExecuteAsync(Request(h, "usp_ClientesPorSaldo",
            ("SaldoMinimo", new ProcedureArgument { Value = Json("1") }),
            ("@saldominimo", new ProcedureArgument { Value = Json("2") })), Ct));
        Assert.Equal(ErrorCodes.InvalidArgument, duplicated.Code);

        var missing = await Assert.ThrowsAsync<ToolFailureException>(() => h.Procedures.ExecuteAsync(Request(h, "usp_NoExiste"), Ct));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
    }

    [Fact]
    public async Task ExcepcionDelSistemaDeSoloLectura()
    {
        await using var h = WriteHarness.Create();
        var result = await h.Procedures.ExecuteAsync(new ProcedureExecutionRequest
        {
            Profile = h.Profile,
            Procedure = new SqlObjectName(null, null, "sys", "sp_describe_first_result_set"),
            Arguments = new Dictionary<string, ProcedureArgument> { ["tsql"] = new() { Value = Json("\"SELECT ClienteId, Nombre FROM ventas.Clientes\"") } },
            Evaluation = WriteSql.ForProcedure(ProcedureExecutor.SystemReadOnlyRuleId),
            CorrelationId = "prc-sys",
            Role = ConnectionRole.Write,
        }, Ct);

        var set = Assert.Single(result.ResultSets);
        Assert.Equal(2, set.Rows.Count);
        Assert.False(result.Committed);
    }

    private static ProcedureExecutionRequest Request(WriteHarness h, string name, params (string Name, ProcedureArgument Argument)[] arguments) => new()
    {
        Profile = h.Profile,
        Procedure = new SqlObjectName(null, null, "ventas", name),
        Arguments = arguments.ToDictionary(a => a.Name, a => a.Argument),
        Evaluation = WriteSql.ForProcedure(),
        CorrelationId = "prc",
        Role = ConnectionRole.Write,
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
