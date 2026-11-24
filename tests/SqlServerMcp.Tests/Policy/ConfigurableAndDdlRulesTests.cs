using SqlServerMcp.Configuration;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>Reglas de DDL (RF-DDL-02/03/04), allowlists por herramienta (RS-POL-04) y reglas configurables (RS-POL-10).</summary>
public sealed class ConfigurableAndDdlRulesTests
{
    [Theory]
    [InlineData("CREATE TABLE T (a int)", "POL-DDL-NOSCHEMA")]
    [InlineData("CREATE TABLE ventas.X (a int)", "POL-DDL-SCHEMA")]
    [InlineData("CREATE TABLE OtraBD.ia.X (a int)", "POL-DDL-SCHEMA")]
    [InlineData("CREATE TABLE ia.X (a int REFERENCES seguridad.Y (Id))", "POL-DAT-001")]
    [InlineData("CREATE SCHEMA ia2", "POL-DDL-SCHEMA")]
    [InlineData("CREATE SCHEMA ia AUTHORIZATION dbo", "POL-DDL-OWNER")]
    [InlineData("CREATE SCHEMA ia CREATE TABLE t (a int) GRANT SELECT ON t TO x", "POL-DCL-001")]
    [InlineData("ALTER SCHEMA ia TRANSFER ventas.Clientes", "POL-DDL-SCHEMA")]
    [InlineData("DROP SCHEMA ventas", "POL-DDL-SCHEMA")]
    [InlineData("CREATE OR ALTER VIEW ia.v WITH ENCRYPTION AS SELECT 1 AS a", "POL-DDL-ENC")]
    [InlineData("CREATE PROCEDURE ia.p WITH ENCRYPTION AS SELECT 1", "POL-DDL-ENC")]
    [InlineData("CREATE PROCEDURE ia.p AS DROP TABLE ventas.Clientes", "POL-DDL-SCHEMA")]
    [InlineData("CREATE PROCEDURE ia.p AS DELETE FROM ia.T", "POL-DML-001")]
    [InlineData("CREATE PROCEDURE ia.p AS UPDATE ia.T SET a = 1 WHERE 1 = 1", "POL-DML-002")]
    [InlineData("CREATE INDEX ix ON ventas.Clientes (Nombre)", "POL-DDL-SCHEMA")]
    [InlineData("TRUNCATE TABLE ventas.Facturas", "POL-DDL-SCHEMA")]
    [InlineData("DROP TABLE ia.T, ventas.Clientes", "POL-DDL-SCHEMA")]
    [InlineData("CREATE TRIGGER ia.trg ON ventas.Clientes AFTER INSERT AS SELECT 1", "POL-DDL-SCHEMA")]
    [InlineData("DROP TRIGGER trg", "POL-DDL-NOSCHEMA")]
    [InlineData("EXEC sp_rename 'T', 'T2'", "POL-DDL-ARGS")]
    [InlineData("EXEC sp_rename 'ventas.Clientes', 'X'", "POL-DDL-SCHEMA")]
    [InlineData("EXEC sp_rename 'VentasTest', 'X', 'DATABASE'", "POL-ADM-001")]
    [InlineData("EXEC sp_addextendedproperty N'MS_Description', N'x'", "POL-DDL-ARGS")]
    [InlineData("EXEC sp_addextendedproperty N'MS_Description', N'x', N'SCHEMA', N'ventas'", "POL-DDL-SCHEMA")]
    [InlineData("CREATE TYPE ia.Clr EXTERNAL NAME a.[b.c]", "POL-EXT-003")]
    [InlineData("CREATE RULE ia.r AS @x > 0", "POL-UNKNOWN-001")]
    public void Ddl_Bloqueado(string sql, string ruleId)
    {
        Policies.AssertDecision(sql, ToolKind.Ddl, PolicyAction.Block, ruleId);
    }

    [Fact]
    public void CreateSchema_ConEsquemaYPropietarioConfigurados_Permitido()
    {
        var profile = TestProfiles.Create(c =>
        {
            c.DdlSchemas = ["ia", "ia2"];
            c.Policy.AllowedSchemaOwners = ["dbo"];
        });
        Policies.AssertDecision("CREATE SCHEMA ia2 AUTHORIZATION dbo", ToolKind.Ddl, PolicyAction.Allow, "POL-DDL-CREATE", profile);
    }

    [Fact]
    public void WithEncryption_PermitidoSoloSiElPerfilLoHabilita()
    {
        var profile = TestProfiles.Create(c => c.Policy.AllowEncryptedModules = true);
        Policies.AssertDecision("CREATE OR ALTER VIEW ia.v WITH ENCRYPTION AS SELECT 1 AS a", ToolKind.Ddl, PolicyAction.Allow, "POL-DDL-CREATE", profile);
    }

    [Theory]
    [InlineData(ToolKind.Query, "INSERT INTO ia.T (a) VALUES (1)", "execute_dml")]
    [InlineData(ToolKind.Query, "CREATE TABLE ia.X (a int)", "execute_ddl")]
    [InlineData(ToolKind.Query, "EXEC ventas.usp_ClientesPorSaldo 1", "execute_procedure")]
    [InlineData(ToolKind.ExecutionPlan, "DELETE FROM ia.T WHERE Id = 1", "execute_dml")]
    [InlineData(ToolKind.Dml, "SELECT 1", "run_query")]
    [InlineData(ToolKind.Dml, "DROP TABLE ia.T", "execute_ddl")]
    [InlineData(ToolKind.Ddl, "EXEC ventas.usp_ClientesPorSaldo 1", "execute_procedure")]
    [InlineData(ToolKind.Ddl, "SELECT 1", "run_query")]
    [InlineData(ToolKind.Ddl, "UPDATE ia.T SET a = 1 WHERE Id = 1", "execute_dml")]
    public void AllowlistDeHerramienta_SugiereLaHerramientaCorrecta(ToolKind tool, string sql, string suggested)
    {
        var e = Policies.AssertDecision(sql, tool, PolicyAction.Block, "POL-TOOL-001");
        Assert.Contains(suggested, e.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorDeEscritura_SeExplicaComoEjecucionDeProcedimiento()
    {
        var e = Policies.AssertDecision("SELEC 1", ToolKind.Query, PolicyAction.Block, "POL-TOOL-001");
        Assert.Contains("SELEC", e.Reason, StringComparison.Ordinal);
        Assert.Equal(StatementCategory.ExecuteProcedure, e.Statement.Category);
    }

    [Fact]
    public void ReglaLocal_DeleteFacturas_Bloquea()
    {
        var e = Policies.AssertDecision("DELETE FROM ventas.Facturas WHERE FacturaId = -1", ToolKind.Dml, PolicyAction.Block, "R-LOCAL-01", TestProfiles.EndToEnd);
        Assert.Null(e.ErrorCode);
        Assert.Equal("Las facturas no se eliminan.", e.Reason);
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Theory]
    [InlineData("DELETE FROM ventas.Facturas WHERE FacturaId = -1")]
    [InlineData("DELETE f FROM ventas.Facturas f WHERE f.FacturaId = -1")]
    [InlineData("WITH x AS (SELECT * FROM ventas.Facturas WHERE FacturaId < 0) DELETE FROM x WHERE FacturaId = -2")]
    [InlineData("DELETE FROM [VENTAS].[FACTURAS] WHERE FacturaId = 1")]
    [InlineData("DELETE FROM ventas.Facturas")]
    public void ReglaLocal_CoincideConDestinoAliasCteYMayusculas(string sql)
    {
        Policies.AssertDecision(sql, ToolKind.Dml, PolicyAction.Block, "R-LOCAL-01", TestProfiles.EndToEnd);
    }

    [Fact]
    public void ReglaLocal_NoAfectaAOtrasSentencias()
    {
        Policies.AssertDecision("DELETE FROM ventas.Clientes WHERE ClienteId = 1", ToolKind.Dml, PolicyAction.Allow, "POL-DML-DEL", TestProfiles.EndToEnd);
        Policies.AssertDecision("SELECT FacturaId FROM ventas.Facturas", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", TestProfiles.EndToEnd);
        Policies.AssertDecision("UPDATE ventas.Facturas SET Total = 1 WHERE FacturaId = 1", ToolKind.Dml, PolicyAction.Allow, "POL-DML-UPD", TestProfiles.EndToEnd);
    }

    [Fact]
    public void ReglaLocal_NoRelajaReglasInmutables()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-TODO", Action = PolicyAction.Allow }));
        Policies.AssertDecision("GRANT SELECT ON ventas.Clientes TO x", ToolKind.Any, PolicyAction.Block, "POL-DCL-001", profile);
        Policies.AssertDecision("SELECT * FROM OPENQUERY(SRV, 'x')", ToolKind.Query, PolicyAction.Block, "POL-EXT-001", profile);
        Policies.AssertDecision("SELECT 1 AS uno", ToolKind.Query, PolicyAction.Allow, "R-TODO", profile);
    }

    [Theory]
    [InlineData(PolicyAction.Allow, PolicyAction.Confirm)]
    [InlineData(PolicyAction.Alert, PolicyAction.Confirm)]
    [InlineData(PolicyAction.Confirm, PolicyAction.Confirm)]
    [InlineData(PolicyAction.Block, PolicyAction.Block)]
    public void DropAction_SeAcotaAlPisoConfirmar(PolicyAction configured, PolicyAction expected)
    {
        var profile = TestProfiles.Create(c => c.Policy.DropAction = configured);
        Policies.AssertDecision("DROP TABLE ia.T", ToolKind.Ddl, expected, "POL-DDL-DROP", profile);
    }

    [Theory]
    [InlineData(PolicyAction.Allow, PolicyAction.Confirm)]
    [InlineData(PolicyAction.Confirm, PolicyAction.Confirm)]
    [InlineData(PolicyAction.Block, PolicyAction.Block)]
    public void DmlSinFiltro_SoloPuedeRelajarseAConfirmar(PolicyAction configured, PolicyAction expected)
    {
        var profile = TestProfiles.Create(c => c.Policy.UnfilteredDml = configured);
        Policies.AssertDecision("DELETE FROM ia.T", ToolKind.Dml, expected, "POL-DML-001", profile);
        Policies.AssertDecision("UPDATE ia.T SET a = 1 WHERE 1 = 1", ToolKind.Dml, expected, "POL-DML-002", profile);
    }

    [Fact]
    public void TriggerAction_PisoAlertar()
    {
        var profile = TestProfiles.Create(c => c.Policy.TriggerAction = PolicyAction.Allow);
        var e = Policies.AssertDecision("CREATE TRIGGER ia.trg ON ia.T AFTER INSERT AS SET NOCOUNT ON", ToolKind.Ddl, PolicyAction.Alert, "POL-DDL-TRIGGER", profile);
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Fact]
    public void SqlDinamico_PisoConfirmar()
    {
        var profile = TestProfiles.Create(c => c.Policy.DynamicSql = PolicyAction.Allow);
        Policies.AssertDecision("EXEC('SELECT 1')", ToolKind.Any, PolicyAction.Confirm, "POL-DYN-002", profile);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE ia.T", "POL-DDL-TRUNCATE")]
    [InlineData("ALTER TABLE ia.T DROP COLUMN c", "POL-DDL-DESTRUCTIVE")]
    public void AccionesDestructivas_ConfigurablesHastaBloquear(string sql, string ruleId)
    {
        var profile = TestProfiles.Create(c =>
        {
            c.Policy.TruncateAction = PolicyAction.Block;
            c.Policy.DestructiveAlterAction = PolicyAction.Block;
        });
        Policies.AssertDecision(sql, ToolKind.Ddl, PolicyAction.Block, ruleId, profile);
    }

    [Fact]
    public void CreateAction_Confirmar()
    {
        var profile = TestProfiles.Create(c => c.Policy.CreateAction = PolicyAction.Confirm);
        Policies.AssertDecision("CREATE TABLE ia.X (a int)", ToolKind.Ddl, PolicyAction.Confirm, "POL-DDL-CREATE", profile);
        Policies.AssertDecision("CREATE TABLE #t (a int)", ToolKind.Ddl, PolicyAction.Allow, "POL-DDL-TEMP", profile);
    }

    [Fact]
    public void AlwaysConfirmDml_ConfirmaUpdateDeleteMergePeroNoInsert()
    {
        var profile = TestProfiles.Create(c => c.Policy.AlwaysConfirmDml = true);
        Policies.AssertDecision("UPDATE ia.T SET a = 1 WHERE Id = 1", ToolKind.Dml, PolicyAction.Confirm, "POL-DML-ALWAYS", profile);
        Policies.AssertDecision("DELETE FROM ia.T WHERE Id = 1", ToolKind.Dml, PolicyAction.Confirm, "POL-DML-ALWAYS", profile);
        Policies.AssertDecision("MERGE ia.T AS t USING ia.S AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", ToolKind.Dml, PolicyAction.Confirm, "POL-DML-ALWAYS", profile);
        Policies.AssertDecision("INSERT INTO ia.T (a) VALUES (1)", ToolKind.Dml, PolicyAction.Allow, "POL-DML-INS", profile);
    }

    [Fact]
    public void ReglaPermisiva_SeAcotaAlPisoDeLaCategoria()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-DROP-IA", Statement = "Drop", Schema = "ia", Action = PolicyAction.Allow }));
        var e = Policies.AssertDecision("DROP TABLE ia.T", ToolKind.Ddl, PolicyAction.Confirm, "R-DROP-IA", profile);
        Assert.Contains("CONFIRMAR", e.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReglaPorColumna_Alerta()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-SALDO", Statement = "Select", Columns = ["Saldo"], Action = PolicyAction.Alert, Reason = "Consulta de saldos" }));
        var e = Policies.AssertDecision("SELECT ClienteId, Saldo FROM ventas.Clientes", ToolKind.Query, PolicyAction.Alert, "R-SALDO", profile);
        Assert.Equal(AlertSeverity.Warning, e.Alert);
        Policies.AssertDecision("SELECT ClienteId FROM ventas.Clientes", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile);
    }

    [Fact]
    public void ReglaPorEsquemaConComodin_YLaMasRestrictivaGana()
    {
        var profile = TestProfiles.Create(c =>
        {
            c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-VENT-ALERT", Statements = ["Update"], Schema = "vent*", Action = PolicyAction.Alert });
            c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-VENT-CONFIRM", Statements = ["Update", "Delete"], Object = "ventas.*", Action = PolicyAction.Confirm });
        });
        Policies.AssertDecision("UPDATE ventas.Clientes SET Nombre = 'x' WHERE ClienteId = 1", ToolKind.Dml, PolicyAction.Confirm, "R-VENT-CONFIRM", profile);
        Policies.AssertDecision("UPDATE ia.T SET a = 1 WHERE Id = 1", ToolKind.Dml, PolicyAction.Allow, "POL-DML-UPD", profile);
    }

    [Fact]
    public void ReglaPorCategoria_DdlCreate()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-NO-VISTAS", Statement = "DdlCreate", Object = "ia.v*", Action = PolicyAction.Block }));
        Policies.AssertDecision("CREATE VIEW ia.vClientes AS SELECT ClienteId FROM ventas.Clientes", ToolKind.Ddl, PolicyAction.Block, "R-NO-VISTAS", profile);
        Policies.AssertDecision("CREATE TABLE ia.T2 (a int)", ToolKind.Ddl, PolicyAction.Allow, "POL-DDL-CREATE", profile);
    }

    [Fact]
    public void ReglasConFilasEstimadas_NoSeAplicanEnElAnalisisEstatico()
    {
        var profile = TestProfiles.Create(c => c.Policy.Rules.Add(new PolicyRuleConfig { Id = "R-FILAS", Statement = "Delete", MinEstimatedRows = 50, Action = PolicyAction.Block }));
        Policies.AssertDecision("DELETE FROM ia.T WHERE Id > 0", ToolKind.Dml, PolicyAction.Allow, "POL-DML-DEL", profile);
    }

    [Fact]
    public void CatalogoDelSistema_HabilitadoPorPerfil_SalvoVistasSensibles()
    {
        var profile = TestProfiles.Create(c => c.Policy.AllowSystemCatalogQueries = true);
        Policies.AssertDecision("SELECT name FROM sys.tables", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile);
        Policies.AssertDecision("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile);
        Policies.AssertDecision("SELECT name FROM sys.sql_logins", ToolKind.Query, PolicyAction.Block, "POL-CAT-001", profile);
        Policies.AssertDecision("SELECT name FROM master.sys.tables", ToolKind.Query, PolicyAction.Block, "POL-REF-002", profile);
    }

    [Fact]
    public void EsquemaDboNoPermitido_LosNombresSinEsquemaSeInterpretanComoDbo()
    {
        Policies.AssertDecision("SELECT * FROM Clientes", ToolKind.Query, PolicyAction.Block, "POL-REF-003", TestProfiles.EndToEnd);
        Policies.AssertDecision("SELECT * FROM dbo.Clientes", ToolKind.Query, PolicyAction.Block, "POL-REF-003", TestProfiles.EndToEnd);
        Policies.AssertDecision("SELECT dbo.fn_x(1) AS v", ToolKind.Query, PolicyAction.Block, "POL-REF-003", TestProfiles.EndToEnd);
    }
}
