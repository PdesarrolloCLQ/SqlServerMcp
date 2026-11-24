using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>ApplyRuntimeFacts (RS-LIM, RS-POL-14, RF-DML-03/05) y EvaluateProcedure (RF-PRC-02/03, sección 5.4).</summary>
public sealed class RuntimeFactsAndProcedureTests
{
    private static readonly PolicyEngine Engine = Policies.Engine;

    [Fact]
    public void Runtime_InsertSobreElMaximo_LimitExceeded()
    {
        var profile = TestProfiles.EndToEnd;
        var e = Policies.Evaluate("INSERT INTO [ventas].[Clientes] ([Nombre], [Email]) VALUES (@p0, @p1)", ToolKind.Dml, profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(1001, null), profile);
        Policies.AssertDecision(runtime, PolicyAction.Block, "POL-LIM-001");
        Assert.Equal(ErrorCodes.LimitExceeded, runtime.ErrorCode);
        Assert.Equal(e.Hits.Count + 1, runtime.Hits.Count);
    }

    [Fact]
    public void Runtime_DentroDelUmbral_NoCambiaLaDecision()
    {
        var profile = TestProfiles.EndToEnd;
        var e = Policies.Evaluate("DELETE FROM ia.T WHERE Id < 50", ToolKind.Dml, profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(100, null), profile);
        Policies.AssertDecision(runtime, PolicyAction.Allow, "POL-DML-DEL");
    }

    [Fact]
    public void Runtime_NuncaRelajaUnaConfirmacion()
    {
        var profile = TestProfiles.EndToEnd;
        var e = Policies.Evaluate("DROP TABLE ia.T", ToolKind.Ddl, profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(null, null, 10), profile);
        Policies.AssertDecision(runtime, PolicyAction.Confirm, "POL-DDL-DROP");
    }

    [Fact]
    public void Runtime_UnaEvaluacionBloqueadaSeDevuelveSinCambios()
    {
        var profile = TestProfiles.EndToEnd;
        var e = Policies.Evaluate("DELETE FROM ia.T", ToolKind.Dml, profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(1, 0.1, 1), profile);
        Assert.Same(e, runtime);
    }

    [Fact]
    public void Runtime_ConfirmacionPreviaEscalaABloqueo()
    {
        var profile = TestProfiles.Create(c =>
        {
            c.Policy.AlwaysConfirmDml = true;
            c.Limits.MaxAffectedRows = 10;
        });
        var e = Policies.AssertDecision("UPDATE ia.T SET a = 1 WHERE Id > 0", ToolKind.Dml, PolicyAction.Confirm, "POL-DML-ALWAYS", profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(11, null), profile);
        Policies.AssertDecision(runtime, PolicyAction.Block, "POL-LIM-001");
    }

    [Theory]
    [InlineData(60, PolicyAction.Block, "R-FILAS")]
    [InlineData(50, PolicyAction.Block, "R-FILAS")]
    [InlineData(10, PolicyAction.Allow, "POL-DML-DEL")]
    public void Runtime_ReglasConMinEstimatedRows(long rows, PolicyAction action, string ruleId)
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-FILAS", Statement = "Delete", MinEstimatedRows = 50, Action = PolicyAction.Block, Reason = "Borrado masivo" }));
        var e = Policies.Evaluate("DELETE FROM ia.T WHERE Id > 0", ToolKind.Dml, profile);
        Policies.AssertDecision(Engine.ApplyRuntimeFacts(e, new RuntimeFacts(rows, null), profile), action, ruleId);
    }

    [Theory]
    [InlineData(50.0, PolicyAction.Confirm, "POL-COST-001")]
    [InlineData(5.0, PolicyAction.Allow, "POL-QRY-001")]
    public void Runtime_CostoEstimadoDelPlan(double cost, PolicyAction action, string ruleId)
    {
        var profile = TestProfiles.Create(c => c.Limits.ConfirmAboveEstimatedCost = 10);
        var e = Policies.Evaluate("SELECT ClienteId FROM ventas.Clientes", ToolKind.Query, profile);
        Policies.AssertDecision(Engine.ApplyRuntimeFacts(e, new RuntimeFacts(null, cost, 1000), profile), action, ruleId);
    }

    [Fact]
    public void Runtime_ReglaConMinEstimatedCost()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-COSTO", Statement = "Select", MinEstimatedCost = 100, Action = PolicyAction.Alert }));
        var e = Policies.Evaluate("SELECT ClienteId FROM ventas.Clientes", ToolKind.Query, profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(null, 150, null), profile);
        Policies.AssertDecision(runtime, PolicyAction.Alert, "R-COSTO");
        Assert.Equal(AlertSeverity.Warning, runtime.Alert);
    }

    [Theory]
    [InlineData("SELECT a.FacturaId FROM ventas.Facturas a CROSS JOIN ventas.Facturas b")]
    [InlineData("SELECT a.FacturaId FROM ventas.Facturas a, ventas.Clientes b")]
    [InlineData("SELECT a.FacturaId FROM ventas.Facturas a JOIN ventas.Clientes b ON 1 = 1")]
    public void Runtime_ProductoCartesianoSobreTablaGrande_Confirmacion(string sql)
    {
        var profile = TestProfiles.Create(c => c.Limits.LargeTableRows = 1000);
        var e = Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile);
        Assert.True(e.Statement.HasCartesianJoin);
        Policies.AssertDecision(Engine.ApplyRuntimeFacts(e, new RuntimeFacts(null, 1, 5000), profile), PolicyAction.Confirm, "POL-LIM-CART");
        Policies.AssertDecision(Engine.ApplyRuntimeFacts(e, new RuntimeFacts(null, 1, 10), profile), PolicyAction.Allow, "POL-QRY-001");
    }

    [Fact]
    public void Runtime_JoinConCondicion_NoEsCartesiano()
    {
        var e = Policies.Evaluate("SELECT a.FacturaId FROM ventas.Facturas a JOIN ventas.Clientes b ON b.ClienteId = a.ClienteId", ToolKind.Query);
        Assert.False(e.Statement.HasCartesianJoin);
    }

    [Fact]
    public void Runtime_IndiceSobreTablaGrande_Alerta()
    {
        var profile = TestProfiles.Create(c => c.Limits.LargeTableRows = 1000);
        var e = Policies.Evaluate("CREATE INDEX ix ON ia.T (a)", ToolKind.Ddl, profile);
        var runtime = Engine.ApplyRuntimeFacts(e, new RuntimeFacts(null, null, 2000), profile);
        Policies.AssertDecision(runtime, PolicyAction.Alert, "POL-DDL-IDX-LARGE");
        Assert.Equal(AlertSeverity.Warning, runtime.Alert);
    }

    [Theory]
    [InlineData(null, null, "sp_help")]
    [InlineData(null, "sys", "sp_helptext")]
    [InlineData(null, "dbo", "sp_describe_first_result_set")]
    [InlineData(null, "SYS", "SP_HELP")]
    public void Procedimiento_SistemaDeSoloLectura_Permitido(string? database, string? schema, string name)
    {
        var e = Engine.EvaluateProcedure(new SqlObjectName(null, database, schema, name), TestProfiles.Default);
        Policies.AssertDecision(e, PolicyAction.Allow, "POL-PRC-SYS-RO");
        Assert.Equal(StatementCategory.ExecuteProcedure, e.Statement.Category);
    }

    [Theory]
    [InlineData(null, null, "ventas", "sp_help", "POL-PRC-SYS")]
    [InlineData(null, null, null, "sp_who", "POL-PRC-SYS")]
    [InlineData(null, null, "sys", "objects", "POL-PRC-SYS")]
    [InlineData("SRV", "VentasTest", "ventas", "usp_X", "POL-EXT-002")]
    [InlineData(null, "OtraBD", "ventas", "usp_X", "POL-REF-001")]
    [InlineData(null, "master", "dbo", "usp_X", "POL-REF-002")]
    [InlineData(null, null, "seguridad", "usp_X", "POL-DAT-001")]
    [InlineData(null, null, "otro", "usp_X", "POL-REF-003")]
    [InlineData(null, null, null, "sp_executesql", "POL-DYN-001")]
    [InlineData(null, null, null, "sp_addrolemember", "POL-DCL-003")]
    [InlineData(null, null, null, "sp_configure", "POL-ADM-004")]
    [InlineData(null, "master", null, "xp_cmdshell", "POL-EXT-003")]
    [InlineData(null, null, "", "", "POL-PRC-INVALID")]
    public void Procedimiento_Bloqueado(string? server, string? database, string? schema, string name, string ruleId)
    {
        var e = Engine.EvaluateProcedure(new SqlObjectName(server, database, schema, name), TestProfiles.Default);
        Policies.AssertDecision(e, PolicyAction.Block, ruleId);
    }

    [Fact]
    public void Procedimiento_SinListaDePermitidos_CualquieraEnEsquemaPermitido()
    {
        var e = Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_Cualquiera"), TestProfiles.Default);
        Policies.AssertDecision(e, PolicyAction.Allow, "POL-PRC-001");
        Assert.Equal("EXEC [ventas].[usp_Cualquiera]", e.Statement.NormalizedSql);
        Assert.Equal(64, e.Statement.Hash.Length);
        Assert.Equal(e.Statement.Hash.ToLowerInvariant(), e.Statement.Hash);
        Assert.Equal(new SqlObjectName(null, null, "ventas", "usp_Cualquiera"), e.Statement.TargetObject);
        Assert.Equal("EXECUTE", e.Statement.Operation);
    }

    [Fact]
    public void Procedimiento_ListasDelPerfil()
    {
        var profile = TestProfiles.Create(c =>
        {
            c.Procedures =
            [
                new ProcedureRuleConfig { Name = "ventas.usp_ClientesPorSaldo", Kind = ProcedureKind.ReadOnly },
                new ProcedureRuleConfig { Name = "ventas.usp_Desactivar*", Kind = ProcedureKind.SideEffects, Confirm = true },
            ];
            c.DeniedProcedures = ["ventas.usp_DesactivarTodo"];
        });

        Policies.AssertDecision(Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_ClientesPorSaldo"), profile), PolicyAction.Allow, "POL-PRC-001");
        Policies.AssertDecision(Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_DesactivarCliente"), profile), PolicyAction.Confirm, "POL-PRC-CONFIRM");
        Policies.AssertDecision(Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_DesactivarTodo"), profile), PolicyAction.Block, "POL-PRC-DENY");
        Policies.AssertDecision(Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_Otro"), profile), PolicyAction.Block, "POL-PRC-NOTALLOWED");
        Policies.AssertDecision(Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_ClientesPorSáldo"), profile), PolicyAction.Block, "POL-PRC-NOTALLOWED");
    }

    [Fact]
    public void Procedimiento_ReglaConfigurableDeEjecucion()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-EXEC", Statement = "Execute", Object = "ventas.usp_*", Action = PolicyAction.Confirm }));
        Policies.AssertDecision(Engine.EvaluateProcedure(new SqlObjectName(null, null, "ventas", "usp_X"), profile), PolicyAction.Confirm, "R-EXEC");
    }

    [Fact]
    public void ExecEnTextoLibre_UsaLasMismasListasDeProcedimientos()
    {
        var profile = TestProfiles.Create(c => c.Procedures = [new ProcedureRuleConfig { Name = "ventas.usp_Permitido" }]);
        Policies.AssertDecision("EXEC ventas.usp_Permitido 1", ToolKind.Any, PolicyAction.Allow, "POL-PRC-001", profile);
        Policies.AssertDecision("EXEC ventas.usp_Otro 1", ToolKind.Any, PolicyAction.Block, "POL-PRC-NOTALLOWED", profile);
        Policies.AssertDecision("CREATE PROCEDURE ia.p AS EXEC ventas.usp_Otro 1", ToolKind.Ddl, PolicyAction.Block, "POL-PRC-NOTALLOWED", profile);
    }
}
