using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>Sentencias legítimas que el motor debe permitir o enviar a confirmación (sección 3, RS-POL-04).</summary>
public sealed class AllowedStatementsTests
{
    [Theory]
    // ---- Consultas (DQL)
    [InlineData(ToolKind.Query, "SELECT ClienteId, Nombre FROM ventas.Clientes WHERE ClienteId = @id", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "WITH c AS (SELECT ClienteId, Nombre FROM ventas.Clientes) SELECT c.Nombre FROM c", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "WITH r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 10) SELECT n FROM r", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM ventas.Clientes UNION SELECT ClienteId FROM ventas.Facturas", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM ventas.Clientes EXCEPT SELECT ClienteId FROM ventas.Facturas", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId, ROW_NUMBER() OVER (PARTITION BY Nombre ORDER BY ClienteId) AS rn FROM ventas.Clientes", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM (SELECT ClienteId, Total, Anio FROM ventas.Facturas) s PIVOT (SUM(Total) FOR Anio IN ([2024], [2025])) p", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId, Nombre FROM ventas.Clientes FOR JSON PATH", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT c.Nombre, f.Total FROM ventas.Clientes c LEFT JOIN ventas.Facturas f ON f.ClienteId = c.ClienteId", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM ventas.Clientes c WHERE EXISTS (SELECT * FROM ventas.Facturas f WHERE f.ClienteId = c.ClienteId)", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT TOP (10) [ClienteId], [Nombre] FROM [ventas].[Clientes]", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT CAST(SESSION_CONTEXT(N'mcp_correlation_id') AS nvarchar(64)) AS cid, APP_NAME() AS app", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT 1 AS uno", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT value FROM STRING_SPLIT('a,b', ',')", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT a FROM #tmp", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT a FROM tempdb..#tmp", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM VentasTest.ventas.Clientes", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT COUNT(*) AS n FROM ventas.Facturas", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT Nombre FROM ventas.Clientes ORDER BY Nombre OFFSET 10 ROWS FETCH NEXT 10 ROWS ONLY", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT DATEADD(day, 1, FechaAlta) AS d FROM ventas.Clientes", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT a.FacturaId, b.FacturaId AS Otra FROM ventas.Facturas a JOIN ventas.Facturas b ON b.ClienteId = a.ClienteId", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT c.ClienteId, j.[key] FROM ventas.Clientes c CROSS APPLY OPENJSON(c.Datos) j", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT c.ClienteId, d.Total FROM ventas.Clientes c CROSS APPLY ventas.fn_Detalle(c.ClienteId) d", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ventas.fn_Calcular(ClienteId) AS v FROM ventas.Clientes", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT NEXT VALUE FOR ia.seq AS n", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT hierarchyid::GetRoot() AS h, geography::Point(1, 2, 4326) AS g", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM ventas.Clientes WITH (NOLOCK) OPTION (RECOMPILE)", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.Query, "SELECT @@ROWCOUNT AS n, GETDATE() AS ahora", PolicyAction.Allow, "POL-QRY-001")]
    [InlineData(ToolKind.ExecutionPlan, "SELECT * FROM ventas.Clientes WHERE ClienteId = 1", PolicyAction.Allow, "POL-QRY-001")]
    // ---- DML con filtro
    [InlineData(ToolKind.Dml, "INSERT INTO [ventas].[Clientes] ([Nombre], [Email]) VALUES (@p0, @p1)", PolicyAction.Allow, "POL-DML-INS")]
    [InlineData(ToolKind.Dml, "INSERT INTO ia.Copia (a, b) SELECT ClienteId, Nombre FROM ventas.Clientes", PolicyAction.Allow, "POL-DML-INS")]
    [InlineData(ToolKind.Dml, "UPDATE ventas.Clientes SET Nombre = @n WHERE ClienteId = @id", PolicyAction.Allow, "POL-DML-UPD")]
    [InlineData(ToolKind.Dml, "UPDATE t SET t.Nombre = s.Nombre FROM ia.T t JOIN ventas.Clientes s ON s.ClienteId = t.Id WHERE t.Id > 10", PolicyAction.Allow, "POL-DML-UPD")]
    [InlineData(ToolKind.Dml, "DELETE FROM ia.T WHERE Id = 5", PolicyAction.Allow, "POL-DML-DEL")]
    [InlineData(ToolKind.Dml, "DELETE FROM ia.T WHERE Id IN (SELECT Id FROM ia.Borrar)", PolicyAction.Allow, "POL-DML-DEL")]
    [InlineData(ToolKind.Dml, "MERGE ia.T AS t USING ventas.Clientes AS s ON t.Id = s.ClienteId WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.ClienteId);", PolicyAction.Allow, "POL-DML-MRG")]
    [InlineData(ToolKind.Dml, "UPDATE ia.T SET a = 1 OUTPUT inserted.Id, deleted.a WHERE Id = 1", PolicyAction.Allow, "POL-DML-UPD")]
    // ---- DDL
    [InlineData(ToolKind.Ddl, "CREATE TABLE ia.X (Id int PRIMARY KEY, Nombre nvarchar(50))", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE TABLE ia.X (Id int, ClienteId int REFERENCES ventas.Clientes (ClienteId))", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE OR ALTER PROCEDURE ia.p @id int AS BEGIN SET NOCOUNT ON; SELECT ClienteId, Nombre FROM ventas.Clientes WHERE ClienteId = @id; END", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS BEGIN TRY BEGIN TRAN; UPDATE ia.T SET a = 1 WHERE Id = 1; COMMIT; END TRY BEGIN CATCH ROLLBACK; THROW; END CATCH", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS BEGIN CREATE TABLE #t (a int); INSERT INTO #t (a) SELECT ClienteId FROM ventas.Clientes; SELECT a FROM #t; DROP TABLE #t; END", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE VIEW ia.v AS SELECT ClienteId, Nombre FROM ventas.Clientes", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "ALTER VIEW ia.v AS SELECT ClienteId FROM ventas.Clientes", PolicyAction.Allow, "POL-DDL-ALTER")]
    [InlineData(ToolKind.Ddl, "CREATE FUNCTION ia.f (@x int) RETURNS int WITH EXECUTE AS CALLER AS BEGIN RETURN @x * 2 END", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE FUNCTION ia.f () RETURNS TABLE AS RETURN SELECT ClienteId FROM ventas.Clientes", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE INDEX ix ON ia.T (a) INCLUDE (b)", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "ALTER INDEX ix ON ia.T REBUILD", PolicyAction.Allow, "POL-DDL-ALTER")]
    [InlineData(ToolKind.Ddl, "ALTER TABLE ia.T ADD c int NULL", PolicyAction.Allow, "POL-DDL-ALTER")]
    [InlineData(ToolKind.Ddl, "CREATE STATISTICS st ON ia.T (a)", PolicyAction.Allow, "POL-DDL-STATS")]
    [InlineData(ToolKind.Ddl, "UPDATE STATISTICS ia.T", PolicyAction.Allow, "POL-DDL-STATS")]
    [InlineData(ToolKind.Ddl, "CREATE SEQUENCE ia.seq START WITH 1", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE SYNONYM ia.s FOR ventas.Clientes", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE TYPE ia.Telefono FROM nvarchar(20)", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE TYPE ia.Lista AS TABLE (Id int)", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE TABLE #t (a int)", PolicyAction.Allow, "POL-DDL-TEMP")]
    [InlineData(ToolKind.Ddl, "SELECT ClienteId, Nombre INTO ia.Copia FROM ventas.Clientes", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "CREATE SCHEMA ia CREATE TABLE t (a int)", PolicyAction.Allow, "POL-DDL-CREATE")]
    [InlineData(ToolKind.Ddl, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'x', @level0type = N'SCHEMA', @level0name = N'ia', @level1type = N'TABLE', @level1name = N'T'", PolicyAction.Allow, "POL-DDL-EXTPROP")]
    [InlineData(ToolKind.Ddl, "EXEC sp_dropextendedproperty N'MS_Description', N'SCHEMA', N'ia', N'TABLE', N'T'", PolicyAction.Allow, "POL-DDL-EXTPROP")]
    // ---- DDL destructivo, triggers y objetos especiales: CONFIRMAR
    [InlineData(ToolKind.Ddl, "DROP TABLE IF EXISTS ia.T", PolicyAction.Confirm, "POL-DDL-DROP")]
    [InlineData(ToolKind.Ddl, "DROP PROCEDURE ia.p", PolicyAction.Confirm, "POL-DDL-DROP")]
    [InlineData(ToolKind.Ddl, "DROP INDEX ix ON ia.T", PolicyAction.Confirm, "POL-DDL-DROP")]
    [InlineData(ToolKind.Ddl, "DROP SCHEMA ia", PolicyAction.Confirm, "POL-DDL-DROP")]
    [InlineData(ToolKind.Ddl, "TRUNCATE TABLE ia.T", PolicyAction.Confirm, "POL-DDL-TRUNCATE")]
    [InlineData(ToolKind.Ddl, "ALTER TABLE ia.T DROP COLUMN c", PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE")]
    [InlineData(ToolKind.Ddl, "ALTER TABLE ia.T ALTER COLUMN c bigint", PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE")]
    [InlineData(ToolKind.Ddl, "ALTER TABLE ia.T SET (SYSTEM_VERSIONING = OFF)", PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE")]
    [InlineData(ToolKind.Ddl, "ALTER INDEX ix ON ia.T DISABLE", PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE")]
    [InlineData(ToolKind.Ddl, "EXEC sp_rename 'ia.T', 'T2'", PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE")]
    [InlineData(ToolKind.Ddl, "EXEC sp_rename N'ia.T.a', N'b', N'COLUMN'", PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE")]
    [InlineData(ToolKind.Ddl, "CREATE TRIGGER ia.trg ON ia.T AFTER INSERT AS SET NOCOUNT ON", PolicyAction.Confirm, "POL-DDL-TRIGGER")]
    [InlineData(ToolKind.Ddl, "CREATE TRIGGER ia.trg ON ia.T AFTER INSERT AS BEGIN UPDATE t SET a = 1 FROM ia.T t JOIN inserted i ON i.Id = t.Id END", PolicyAction.Confirm, "POL-DDL-TRIGGER")]
    [InlineData(ToolKind.Ddl, "ENABLE TRIGGER ALL ON ia.T", PolicyAction.Confirm, "POL-DDL-TRIGGER")]
    [InlineData(ToolKind.Ddl, "DISABLE TRIGGER ia.trg ON ia.T", PolicyAction.Confirm, "POL-DDL-TRIGGER")]
    [InlineData(ToolKind.Ddl, "CREATE PARTITION FUNCTION pf (int) AS RANGE LEFT FOR VALUES (1, 100)", PolicyAction.Confirm, "POL-DDL-CONFIRM")]
    [InlineData(ToolKind.Ddl, "DROP TABLE #t", PolicyAction.Allow, "POL-DDL-TEMP")]
    // ---- Procedimientos y validación sin herramienta destino
    [InlineData(ToolKind.Any, "EXEC sp_help 'ventas.Clientes'", PolicyAction.Allow, "POL-PRC-SYS-RO")]
    [InlineData(ToolKind.Any, "EXEC ventas.usp_ClientesPorSaldo 100", PolicyAction.Allow, "POL-PRC-001")]
    [InlineData(ToolKind.Any, "SELECT 1 AS uno", PolicyAction.Allow, "POL-QRY-001")]
    public void Sentencia_DecisionEsperada(ToolKind tool, string sql, PolicyAction action, string ruleId)
    {
        Policies.AssertDecision(sql, tool, action, ruleId);
    }

    [Fact]
    public void SelectStar_PermitidoConAdvertencia()
    {
        var e = Policies.AssertDecision("SELECT * FROM ventas.Clientes", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001");
        Assert.True(e.Statement.HasSelectStar);
        Assert.Contains(e.Warnings, w => w.Contains("RS-DAT-06", StringComparison.Ordinal));
        Assert.Empty(e.MaskOrdinals);
    }

    [Fact]
    public void InsertDeLaHerramientaInsertRows_InformaDestinoYOperacion()
    {
        var e = Policies.AssertDecision("INSERT INTO [ventas].[Clientes] ([Nombre], [Email]) VALUES (@p0, @p1)", ToolKind.Dml, PolicyAction.Allow, "POL-DML-INS");
        Assert.Equal(new SqlObjectName(null, null, "ventas", "Clientes"), e.Statement.TargetObject);
        Assert.Equal("INSERT", e.Statement.Operation);
        Assert.Equal(StatementCategory.Insert, e.Statement.Category);
        Assert.Contains(e.Statement.Objects, o => o.Role == ObjectRole.Write && o.Name.Name == "Clientes");
        Assert.Null(e.Alert);
        Assert.Equal(RiskLevel.Medium, e.Risk);
    }

    [Fact]
    public void SampleTable_InformaTopYColumnas()
    {
        var e = Policies.AssertDecision("SELECT TOP (10) [ClienteId], [Nombre] FROM [ventas].[Clientes]", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001");
        Assert.True(e.Statement.HasTopClause);
        Assert.Contains(e.Statement.Columns, c => c.Column == "Nombre" && c.Schema == "ventas" && c.Table == "Clientes" && c.InSelectList && c.OutputOrdinal == 1);
        Assert.Contains(e.Statement.Objects, o => o.Role == ObjectRole.Read && o.Name == new SqlObjectName(null, null, "ventas", "Clientes"));
        Assert.Equal(RiskLevel.Low, e.Risk);
    }

    [Fact]
    public void DdlDestructivo_InformaOperacionTipoYDestino()
    {
        var e = Policies.AssertDecision("DROP TABLE IF EXISTS ia.T", ToolKind.Ddl, PolicyAction.Confirm, "POL-DDL-DROP");
        Assert.Equal("DROP", e.Statement.Operation);
        Assert.Equal("TABLE", e.Statement.ObjectType);
        Assert.Equal(new SqlObjectName(null, null, "ia", "T"), e.Statement.TargetObject);
        Assert.Equal(RiskLevel.High, e.Risk);
    }

    [Fact]
    public void UpdateConAlias_ElDestinoEsLaTablaReal()
    {
        var e = Policies.AssertDecision("UPDATE t SET Nombre = 'x' FROM ventas.Clientes t WHERE t.ClienteId = 1", ToolKind.Dml, PolicyAction.Allow, "POL-DML-UPD");
        Assert.Equal(new SqlObjectName(null, null, "ventas", "Clientes"), e.Statement.TargetObject);
        Assert.Contains(e.Statement.Objects, o => o.Role == ObjectRole.Write && o.Name.Name == "Clientes");
    }

    [Fact]
    public void Trigger_ElDestinoEsElTriggerConElEsquemaDeSuTabla()
    {
        var e = Policies.AssertDecision("CREATE TRIGGER trg ON ia.T AFTER UPDATE AS SELECT Id FROM deleted", ToolKind.Ddl, PolicyAction.Confirm, "POL-DDL-TRIGGER");
        Assert.Equal(new SqlObjectName(null, null, "ia", "trg"), e.Statement.TargetObject);
        Assert.Equal("TRIGGER", e.Statement.ObjectType);
        Assert.Contains(e.Statement.Objects, o => o.Role == ObjectRole.DdlTarget && o.Name.Name == "T");
    }

    [Fact]
    public void SpRename_ElDestinoEsElObjetoRenombrado()
    {
        var e = Policies.AssertDecision("EXEC sp_rename 'ia.T.a', 'b', 'COLUMN'", ToolKind.Ddl, PolicyAction.Confirm, "POL-DDL-DESTRUCTIVE");
        Assert.Equal(new SqlObjectName(null, null, "ia", "T"), e.Statement.TargetObject);
        Assert.Equal("RENAME", e.Statement.Operation);
        Assert.Equal("COLUMN", e.Statement.ObjectType);
    }

    [Fact]
    public void ColumnaSensibleEnListaPrincipal_SeEnmascaraPorPosicion()
    {
        var profile = TestProfiles.WithoutExcludedColumns;
        Assert.Equal(new[] { 1 }, Policies.AssertDecision("SELECT Nombre, Password FROM ventas.Usuarios", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile).MaskOrdinals);
        Assert.Equal(new[] { 0 }, Policies.AssertDecision("SELECT u.Password AS p, Nombre FROM ventas.Usuarios u", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile).MaskOrdinals);
        Assert.Equal(new[] { 0, 2 }, Policies.AssertDecision("SELECT ApiToken, Nombre, (Contrasena) FROM ventas.Usuarios", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile).MaskOrdinals);
        Assert.Equal(new[] { 0 }, Policies.AssertDecision("SELECT Password FROM ventas.Usuarios UNION ALL SELECT Nombre FROM ventas.Clientes", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile).MaskOrdinals);
        Assert.Empty(Policies.AssertDecision("SELECT Nombre, *, Password FROM ventas.Usuarios", ToolKind.Query, PolicyAction.Allow, "POL-QRY-001", profile).MaskOrdinals);
    }
}
