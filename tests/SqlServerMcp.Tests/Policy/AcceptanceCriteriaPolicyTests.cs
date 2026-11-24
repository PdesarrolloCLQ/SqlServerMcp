using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>Criterios de aceptación estáticos de la sección 8 del ERS (CA-01..CA-19, CA-29, CA-30) a nivel del motor.</summary>
public sealed class AcceptanceCriteriaPolicyTests
{
    [Fact]
    public void CA01_DropDatabase_BloqueoAdministracionConAlertaCritica()
    {
        var e = Policies.AssertDecision("DROP DATABASE Ventas", ToolKind.Query, PolicyAction.Block, "POL-ADM-001");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
        Assert.Equal(RiskLevel.Critical, e.Risk);
        Assert.Equal(StatementCategory.Administration, e.Statement.Category);
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE dbo.Clientes", 2)]
    [InlineData("SELECT * FROM dbo.T; COMMIT; DELETE FROM dbo.T", 3)]
    public void CA02_CA03_VariasSentencias_Rechazadas(string sql, int statements)
    {
        var e = Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Block, "POL-BATCH-001");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
        Assert.Equal(statements, e.Statement.StatementCount);
        Assert.Contains("exactamente una sentencia", e.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CA04_SqlDinamicoEnExecuteDml_Bloqueado()
    {
        var e = Policies.AssertDecision("EXEC('DR' + 'OP TABLE dbo.T')", ToolKind.Dml, PolicyAction.Block, "POL-DYN-001");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
        Assert.Contains(e.Hits, h => h.RuleId == "POL-TOOL-001");
        Assert.Equal(StatementCategory.DynamicSql, e.Statement.Category);
    }

    [Fact]
    public void CA04_SqlDinamicoEnModoConfirmar_AnalizaElLiteralYBloqueaPorTipo()
    {
        var profile = TestProfiles.Create(c => c.Policy.DynamicSql = PolicyAction.Confirm);
        var e = Policies.AssertDecision("EXEC('DR' + 'OP TABLE dbo.T')", ToolKind.Dml, PolicyAction.Block, "POL-TOOL-001", profile);
        Assert.Contains(e.Hits, h => h.RuleId == "POL-DYN-002");
        Assert.Contains("DropTableStatement", e.Statement.NestedStatementTypes);
    }

    [Fact]
    public void CA05_Openrowset_BloqueoAccesoExternoCritico()
    {
        var e = Policies.AssertDecision("SELECT * FROM OPENROWSET('SQLNCLI','Server=x;Trusted_Connection=yes;','SELECT 1')", ToolKind.Query, PolicyAction.Block, "POL-EXT-001");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
    }

    [Fact]
    public void CA06_CatalogoSensible_Bloqueado()
    {
        var e = Policies.AssertDecision("SELECT name FROM sys.sql_logins", ToolKind.Query, PolicyAction.Block, "POL-CAT-001");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Fact]
    public void CA07_Grant_BloqueoDclCritico()
    {
        var e = Policies.AssertDecision("GRANT CONTROL ON SCHEMA::dbo TO public", ToolKind.Ddl, PolicyAction.Block, "POL-DCL-001");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
        Assert.Equal(StatementCategory.Dcl, e.Statement.Category);
    }

    [Fact]
    public void CA08_DclDentroDeUnModulo_Bloqueado()
    {
        var e = Policies.AssertDecision("CREATE PROCEDURE ia.p AS ALTER ROLE db_owner ADD MEMBER x", ToolKind.Ddl, PolicyAction.Block, "POL-DCL-003");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
        Assert.Contains("AlterRoleStatement", e.Statement.NestedStatementTypes);
    }

    [Fact]
    public void CA09_WithExecuteAsOwner_BloqueoSuplantacion()
    {
        var e = Policies.AssertDecision("CREATE PROCEDURE ia.p WITH EXECUTE AS OWNER AS SELECT 1", ToolKind.Ddl, PolicyAction.Block, "POL-IMP-003");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
        Assert.Equal(RiskLevel.Critical, e.Risk);
    }

    [Fact]
    public void CA10_DeleteSinWhere_Bloqueado()
    {
        var e = Policies.AssertDecision("DELETE FROM dbo.Clientes", ToolKind.Dml, PolicyAction.Block, "POL-DML-001");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
        Assert.Equal("Agregue una cláusula WHERE que limite las filas afectadas.", e.Suggestion);
        Assert.False(e.Statement.HasWhereClause);
    }

    [Fact]
    public void CA11_PredicadoTrivial_Bloqueado()
    {
        var e = Policies.AssertDecision("UPDATE dbo.Clientes SET Activo = 0 WHERE 1 = 1", ToolKind.Dml, PolicyAction.Block, "POL-DML-002");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
        Assert.True(e.Statement.HasWhereClause);
        Assert.True(e.Statement.HasTrivialPredicate);
    }

    [Fact]
    public void CA12_FilasEstimadasSobreElMaximo_LimitExceeded()
    {
        var profile = TestProfiles.EndToEnd;
        var e = Policies.AssertDecision("UPDATE ventas.Facturas SET Total = Total + 0 WHERE FacturaId > 0", ToolKind.Dml, PolicyAction.Allow, "POL-DML-UPD", profile);

        var runtime = Policies.Engine.ApplyRuntimeFacts(e, new RuntimeFacts(5000, null), profile);

        Policies.AssertDecision(runtime, PolicyAction.Block, "POL-LIM-001");
        Assert.Equal(ErrorCodes.LimitExceeded, runtime.ErrorCode);
        Assert.Equal(AlertSeverity.Warning, runtime.Alert);
    }

    [Fact]
    public void CA13_FilasEstimadasSobreElUmbral_Confirmacion()
    {
        var profile = TestProfiles.EndToEnd;
        var e = Policies.AssertDecision("DELETE FROM ia.CA13 WHERE Id <= @max", ToolKind.Dml, PolicyAction.Allow, "POL-DML-DEL", profile);

        var runtime = Policies.Engine.ApplyRuntimeFacts(e, new RuntimeFacts(250, null), profile);

        Policies.AssertDecision(runtime, PolicyAction.Confirm, "POL-DML-THRESHOLD");
        Assert.Null(runtime.ErrorCode);
        Assert.Contains("250", runtime.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CA14_SelectIntoEnRunQuery_Rechazado()
    {
        var e = Policies.AssertDecision("SELECT * INTO dbo.Copia FROM dbo.Clientes", ToolKind.Query, PolicyAction.Block, "POL-TOOL-002");
        Assert.Equal(StatementCategory.SelectInto, e.Statement.Category);
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Fact]
    public void CA15_Waitfor_Bloqueado()
    {
        var e = Policies.AssertDecision("WAITFOR DELAY '00:10:00'", ToolKind.Query, PolicyAction.Block, "POL-WAIT-001");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Fact]
    public void CA16_BaseDeDatosNoPermitida_Bloqueada()
    {
        var e = Policies.AssertDecision("SELECT * FROM OtraBD.dbo.T", ToolKind.Query, PolicyAction.Block, "POL-REF-001");
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Fact]
    public void CA17_ServidorVinculado_BloqueoCritico()
    {
        var e = Policies.AssertDecision("SELECT * FROM SRVREMOTO.Ventas.dbo.T", ToolKind.Query, PolicyAction.Block, "POL-EXT-002");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
    }

    [Fact]
    public void CA18_XpDirtreeComoProcedimiento_BloqueoCritico()
    {
        var e = Policies.Engine.EvaluateProcedure(new SqlObjectName(null, "master", "sys", "xp_dirtree"), TestProfiles.Default);
        Policies.AssertDecision(e, PolicyAction.Block, "POL-EXT-003");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
    }

    [Fact]
    public void CA19_ComentariosYMayusculas_PermitidoYNormalizado()
    {
        var e = Policies.AssertDecision("SeLeCt/**/* FROM [dbo].[T]", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001");
        Assert.Null(e.Alert);
        Assert.Equal("SELECT * FROM [dbo].[T]", e.Statement.NormalizedSql);

        var other = Policies.Evaluate("select *\n  from [dbo].[T]   -- otro comentario", ToolKind.Query);
        Assert.Equal(e.Statement.Hash, other.Statement.Hash);
    }

    [Fact]
    public void CA29_ModoEnmascarar_FuncionSobreColumnaSensible_Bloqueada()
    {
        var e = Policies.AssertDecision("SELECT LEFT(Contrasena, 3) FROM dbo.Usuarios", ToolKind.Query, PolicyAction.Block, "POL-DAT-004", TestProfiles.WithoutExcludedColumns);
        Assert.Equal(AlertSeverity.Warning, e.Alert);
    }

    [Fact]
    public void CA29_ModoEnmascarar_ColumnaSensibleDirecta_SePermiteEnmascarada()
    {
        var e = Policies.AssertDecision("SELECT ClienteId, Contrasena FROM dbo.Usuarios", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", TestProfiles.WithoutExcludedColumns);
        Assert.Equal(new[] { 1 }, e.MaskOrdinals);
    }

    [Fact]
    public void CA29_ModoBloquear_ColumnaSensible_Bloqueada()
    {
        var profile = TestProfiles.Create(c =>
        {
            c.Excluded.Columns = [];
            c.Sensitive.Action = SensitiveColumnAction.Block;
        });
        var e = Policies.AssertDecision("SELECT LEFT(Contrasena, 3) FROM dbo.Usuarios", ToolKind.Query, PolicyAction.Block, "POL-DAT-003", profile);
        Assert.Equal(AlertSeverity.Warning, e.Alert);
        Policies.AssertDecision("SELECT Contrasena FROM dbo.Usuarios", ToolKind.Query, PolicyAction.Block, "POL-DAT-003", profile);
    }

    [Fact]
    public void CA29_ColumnaExcluida_BloqueadaComoEnLaPruebaDePuntaAPunta()
    {
        Policies.AssertBlockedWithPrefix("SELECT LEFT(Contrasena, 3) FROM ventas.Usuarios", ToolKind.Query, "POL-DAT", TestProfiles.EndToEnd);
        Policies.AssertDecision("SELECT LEFT(Contrasena, 3) FROM ventas.Usuarios", ToolKind.Query, PolicyAction.Block, "POL-DAT-002", TestProfiles.EndToEnd);
    }

    [Fact]
    public void CA30_SpSendDbmail_BloqueoCritico()
    {
        var e = Policies.Engine.EvaluateProcedure(new SqlObjectName(null, "msdb", "dbo", "sp_send_dbmail"), TestProfiles.Default);
        Policies.AssertDecision(e, PolicyAction.Block, "POL-EXT-004");
        Assert.Equal(AlertSeverity.Critical, e.Alert);
    }
}
