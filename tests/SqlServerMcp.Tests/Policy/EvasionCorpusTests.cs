using SqlServerMcp.Configuration;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>
/// Corpus de evasión (RS-POL-12): comentarios, mayúsculas, delimitadores, Unicode, subconsultas, CTE, funciones en
/// FROM, cuerpos de módulos, SQL dinámico, nombres de varias partes, sinónimos, QUOTED_IDENTIFIER, separadores GO,
/// entradas vacías y errores de escritura. Todas deben bloquearse con la regla indicada.
/// </summary>
public sealed class EvasionCorpusTests
{
    [Theory]
    // ---- Lotes y separadores (RS-POL-03)
    [InlineData(ToolKind.Query, "SELECT 1 /* comentario */ ; DROP TABLE ia.T", "POL-BATCH-001")]
    [InlineData(ToolKind.Query, "SELECT 1\nGO\nSELECT 2", "POL-BATCH-001")]
    [InlineData(ToolKind.Query, "SELECT 1\r\ngo\r\nDROP TABLE ia.T", "POL-BATCH-001")]
    [InlineData(ToolKind.Query, "SELECT 1; COMMIT", "POL-BATCH-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM ventas.Clientes; WAITFOR DELAY '00:01'", "POL-BATCH-001")]
    [InlineData(ToolKind.Any, "DECLARE @s nvarchar(max) = N'x'; EXEC(@s)", "POL-BATCH-001")]
    // ---- Entradas vacías y errores de escritura (RS-POL-02)
    [InlineData(ToolKind.Query, "", "POL-PARSE-002")]
    [InlineData(ToolKind.Query, "   \r\n\t ", "POL-PARSE-002")]
    [InlineData(ToolKind.Query, "-- solo un comentario", "POL-PARSE-002")]
    [InlineData(ToolKind.Query, "/* bloque */ ;", "POL-PARSE-002")]
    [InlineData(ToolKind.Query, "SELEC * FRM", "POL-PARSE-001")]
    [InlineData(ToolKind.Query, "SELEC 1", "POL-TOOL-001")]
    [InlineData(ToolKind.Query, "SELECT 1 FROM", "POL-PARSE-001")]
    // ---- Catálogo sensible con mayúsculas, delimitadores y subconsultas (CA-06)
    [InlineData(ToolKind.Query, "SeLeCt * FrOm sys.SQL_LOGINS", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM [sys].[sql_logins]", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM \"sys\".\"sql_logins\"", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM master.sys.sql_logins", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM syslogins", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM ventas.Clientes WHERE ClienteId IN (SELECT principal_id FROM sys.server_principals)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT (SELECT TOP 1 name FROM sys.sql_logins) AS n", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT 1 AS a WHERE 1 = (SELECT COUNT(*) FROM sys.credentials)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM sys.fn_dblog(NULL, NULL)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM ::fn_dblog(NULL, NULL)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM fn_dblog(NULL, NULL)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM sys.fn_get_audit_file('c:\\a\\*', DEFAULT, DEFAULT)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT PWDCOMPARE('x', 0x01)", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT pwdencrypt('x')", "POL-CAT-001")]
    [InlineData(ToolKind.Query, "SELECT LOGINPROPERTY('sa', 'PasswordHash')", "POL-CAT-001")]
    // ---- Acceso externo en cualquier posición (CA-05, CA-17)
    [InlineData(ToolKind.Query, "WITH c AS (SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')) SELECT * FROM c", "POL-EXT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM OPENROWSET(BULK 'c:\\x.txt', SINGLE_CLOB) AS t", "POL-EXT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", "POL-EXT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM OPENQUERY(SRV, 'SELECT 1')", "POL-EXT-001")]
    [InlineData(ToolKind.Query, "SELECT c.ClienteId FROM ventas.Clientes c CROSS APPLY (SELECT * FROM OPENQUERY(SRV, 'SELECT 1')) q", "POL-EXT-001")]
    [InlineData(ToolKind.Query, "SELECT ClienteId FROM ventas.Clientes WHERE EXISTS (SELECT 1 FROM SRV.Ventas.dbo.T)", "POL-EXT-002")]
    [InlineData(ToolKind.Query, "SELECT * FROM SRV.VentasTest.ventas.fn_Detalle(1)", "POL-EXT-002")]
    [InlineData(ToolKind.Query, "EXEC ('SELECT 1') AT SRV", "POL-EXT-002")]
    [InlineData(ToolKind.Any, "BULK INSERT ia.T FROM 'c:\\x.csv'", "POL-EXT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM msdb.dbo.sysjobs", "POL-EXT-004")]
    // ---- xp_* con prefijos, delimitadores, mayúsculas y Unicode (RS-POL-06)
    [InlineData(ToolKind.Any, "EXEC master..xp_cmdshell 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC [master].[dbo].[xp_cmdshell] 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC [master]..[xp_cmdshell] 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC sys.xp_cmdshell 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC \"xp_cmdshell\" 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "xp_cmdshell 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXECUTE XP_CMDSHELL 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC ｘｐ_cmdshell 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC [xp_cmdshell ] 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC master.sys.xp_dirtree '\\\\host\\share'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC sp_OACreate 'WScript.Shell', @o OUT", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC sp_execute_external_script @language = N'Python', @script = N'print(1)'", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "EXEC sp_addlinkedserver 'SRV'", "POL-EXT-002")]
    [InlineData(ToolKind.Any, "EXEC msdb.dbo.sp_start_job 'x'", "POL-EXT-004")]
    [InlineData(ToolKind.Any, "EXEC sp_send_dbmail @recipients = 'a@b.c'", "POL-EXT-004")]
    [InlineData(ToolKind.Any, "INSERT INTO #t EXEC xp_cmdshell 'dir'", "POL-EXT-003")]
    // ---- Cuerpos de módulos (RF-DDL-02)
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS EXEC master..xp_cmdshell 'dir'", "POL-EXT-003")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS BEGIN IF 1 = 1 BEGIN TRY GRANT SELECT ON ventas.Clientes TO x END TRY BEGIN CATCH END CATCH END", "POL-DCL-001")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS BEGIN WHILE 1 = 0 BEGIN CREATE LOGIN x WITH PASSWORD = 'Abc123!!' END END", "POL-DCL-002")]
    [InlineData(ToolKind.Ddl, "CREATE TRIGGER ia.t ON ia.T AFTER INSERT AS EXEC xp_cmdshell 'x'", "POL-EXT-003")]
    [InlineData(ToolKind.Ddl, "CREATE FUNCTION ia.f() RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM sys.sql_logins) END", "POL-CAT-001")]
    [InlineData(ToolKind.Ddl, "CREATE VIEW ia.v AS SELECT * FROM OPENQUERY(SRV, 'SELECT 1')", "POL-EXT-001")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS SHUTDOWN", "POL-ADM-004")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS EXECUTE AS LOGIN = 'sa'", "POL-IMP-001")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS WAITFOR DELAY '00:00:10'", "POL-WAIT-001")]
    // ---- SQL dinámico (sección 3, CA-04)
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS EXEC('SELECT 1')", "POL-DYN-001")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS BEGIN DECLARE @s nvarchar(max) = N'SELECT 1'; EXEC sp_executesql @s END", "POL-DYN-001")]
    [InlineData(ToolKind.Any, "EXEC sp_executesql N'SELECT 1'", "POL-DYN-001")]
    [InlineData(ToolKind.Any, "EXEC sys.sp_executesql @stmt = N'SELECT 1'", "POL-DYN-001")]
    [InlineData(ToolKind.Any, "EXECUTE [SP_EXECUTESQL] N'SELECT 1'", "POL-DYN-001")]
    [InlineData(ToolKind.Dml, "INSERT INTO ia.T EXEC('SELECT 1')", "POL-DYN-001")]
    [InlineData(ToolKind.Any, "EXEC('SELECT 1') AS USER = 'dbo'", "POL-IMP-002")]
    // ---- SELECT INTO a través de CTE, DML sobre objetos excluidos, INSERT EXEC, DML componible
    [InlineData(ToolKind.Query, "WITH c AS (SELECT ClienteId FROM ventas.Clientes) SELECT * INTO ia.X FROM c", "POL-TOOL-002")]
    [InlineData(ToolKind.Dml, "DELETE FROM ia.T OUTPUT deleted.Id INTO ventas.TarjetasCredito (Id) WHERE Id = 1", "POL-DAT-001")]
    [InlineData(ToolKind.Dml, "MERGE ventas.TarjetasCredito AS t USING ia.S AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", "POL-DAT-001")]
    [InlineData(ToolKind.Dml, "UPDATE t SET Nombre = 'x' FROM seguridad.Usuarios t WHERE t.Id = 1", "POL-DAT-001")]
    [InlineData(ToolKind.Dml, "INSERT INTO ia.T EXEC ventas.usp_ClientesPorSaldo 1", "POL-DML-EXEC")]
    [InlineData(ToolKind.Dml, "INSERT INTO ia.T (a) SELECT x FROM (DELETE FROM ia.S OUTPUT deleted.x WHERE x = 1) AS d", "POL-DML-COMPOSABLE")]
    // ---- Referencias entre bases de datos y objetos temporales (RS-POL-07)
    [InlineData(ToolKind.Query, "SELECT * FROM OtraBD..T", "POL-REF-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM master.dbo.spt_values", "POL-REF-002")]
    [InlineData(ToolKind.Query, "SELECT * FROM tempdb.dbo.t", "POL-REF-002")]
    [InlineData(ToolKind.Query, "SELECT * FROM ##global", "POL-REF-002")]
    [InlineData(ToolKind.Query, "SELECT * FROM otro.Tabla", "POL-REF-003")]
    [InlineData(ToolKind.Query, "SELECT * FROM sys.objects", "POL-REF-004")]
    [InlineData(ToolKind.Query, "SELECT * FROM INFORMATION_SCHEMA.COLUMNS", "POL-REF-004")]
    [InlineData(ToolKind.Query, "SELECT * FROM sysobjects", "POL-REF-004")]
    [InlineData(ToolKind.Query, "SELECT OBJECT_DEFINITION(OBJECT_ID('seguridad.p'))", "POL-REF-004")]
    [InlineData(ToolKind.Query, "SELECT * FROM seguridad.Usuarios", "POL-DAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM ventas.tarjetascredito", "POL-DAT-001")]
    [InlineData(ToolKind.Query, "SELECT * FROM [ventas].[TarjetasCredito] t WHERE t.Id IN (SELECT 1)", "POL-DAT-001")]
    // ---- Sinónimos hacia servidores vinculados o catálogo
    [InlineData(ToolKind.Ddl, "CREATE SYNONYM ia.s FOR SRV.db.dbo.t", "POL-EXT-002")]
    [InlineData(ToolKind.Ddl, "CREATE SYNONYM ia.s FOR master.sys.sql_logins", "POL-CAT-001")]
    [InlineData(ToolKind.Ddl, "CREATE SYNONYM ia.s FOR ventas.TarjetasCredito", "POL-DAT-001")]
    // ---- Estado de sesión y QUOTED_IDENTIFIER
    [InlineData(ToolKind.Query, "SET QUOTED_IDENTIFIER OFF", "POL-SET-002")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p AS SET QUOTED_IDENTIFIER OFF", "POL-SET-002")]
    [InlineData(ToolKind.Query, "SET QUOTED_IDENTIFIER OFF; SELECT \"xp_cmdshell\"", "POL-BATCH-001")]
    [InlineData(ToolKind.Any, "SET SHOWPLAN_XML ON", "POL-SET-001")]
    [InlineData(ToolKind.Any, "SET ROWCOUNT 0", "POL-SET-001")]
    [InlineData(ToolKind.Any, "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED", "POL-SET-001")]
    [InlineData(ToolKind.Any, "SET IMPLICIT_TRANSACTIONS ON", "POL-TCL-001")]
    [InlineData(ToolKind.Any, "BEGIN TRAN", "POL-TCL-001")]
    [InlineData(ToolKind.Any, "COMMIT", "POL-TCL-001")]
    [InlineData(ToolKind.Any, "SAVE TRANSACTION s1", "POL-TCL-001")]
    [InlineData(ToolKind.Any, "BEGIN DISTRIBUTED TRANSACTION", "POL-TCL-002")]
    [InlineData(ToolKind.Any, "USE master", "POL-CTX-001")]
    [InlineData(ToolKind.Any, "IF 1 = 1 SELECT 1", "POL-PROC-001")]
    // ---- Suplantación (CA-09)
    [InlineData(ToolKind.Any, "EXECUTE AS LOGIN = 'sa'", "POL-IMP-001")]
    [InlineData(ToolKind.Any, "REVERT", "POL-IMP-001")]
    [InlineData(ToolKind.Any, "SETUSER 'dbo'", "POL-IMP-001")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p WITH EXECUTE AS SELF AS SELECT 1", "POL-IMP-003")]
    [InlineData(ToolKind.Ddl, "CREATE PROCEDURE ia.p WITH EXECUTE AS 'dbo' AS SELECT 1", "POL-IMP-003")]
    [InlineData(ToolKind.Ddl, "CREATE TRIGGER ia.t ON ia.T WITH EXECUTE AS OWNER AFTER INSERT AS SELECT 1", "POL-IMP-003")]
    [InlineData(ToolKind.Ddl, "CREATE FUNCTION ia.f (@x int) RETURNS int WITH EXECUTE AS OWNER AS BEGIN RETURN @x END", "POL-IMP-003")]
    // ---- DCL, criptografía, gobierno y administración (sección 5.4, Anexo C)
    [InlineData(ToolKind.Any, "EXEC sp_addlogin 'x', 'y'", "POL-DCL-002")]
    [InlineData(ToolKind.Any, "EXEC sp_addsrvrolemember 'x', 'sysadmin'", "POL-DCL-003")]
    [InlineData(ToolKind.Any, "EXEC dbo.sp_addrolemember 'db_owner', 'x'", "POL-DCL-003")]
    [InlineData(ToolKind.Any, "EXEC sp_changedbowner 'x'", "POL-DCL-004")]
    [InlineData(ToolKind.Any, "ALTER AUTHORIZATION ON SCHEMA::ia TO x", "POL-DCL-004")]
    [InlineData(ToolKind.Any, "CREATE USER x WITHOUT LOGIN", "POL-DCL-002")]
    [InlineData(ToolKind.Any, "OPEN SYMMETRIC KEY k DECRYPTION BY PASSWORD = 'x'", "POL-DCL-005")]
    [InlineData(ToolKind.Any, "CREATE CERTIFICATE c WITH SUBJECT = 'x'", "POL-DCL-005")]
    [InlineData(ToolKind.Any, "CREATE SECURITY POLICY ia.p ADD FILTER PREDICATE ia.f(a) ON ia.T", "POL-DCL-006")]
    [InlineData(ToolKind.Ddl, "ALTER TABLE ia.T ALTER COLUMN a ADD MASKED WITH (FUNCTION = 'default()')", "POL-DCL-006")]
    [InlineData(ToolKind.Ddl, "CREATE TABLE ia.U (a int, b nvarchar(10) MASKED WITH (FUNCTION = 'default()'))", "POL-DCL-006")]
    [InlineData(ToolKind.Any, "ALTER DATABASE CURRENT SET TRUSTWORTHY ON", "POL-DCL-007")]
    [InlineData(ToolKind.Any, "ALTER DATABASE CURRENT SET DB_CHAINING ON", "POL-DCL-007")]
    [InlineData(ToolKind.Any, "EXEC sp_configure 'show advanced options', 1", "POL-ADM-004")]
    [InlineData(ToolKind.Any, "RESTORE DATABASE x FROM DISK = 'c:\\x.bak'", "POL-ADM-002")]
    [InlineData(ToolKind.Any, "DBCC TRACEON(3604)", "POL-ADM-003")]
    [InlineData(ToolKind.Any, "CHECKPOINT", "POL-ADM-004")]
    [InlineData(ToolKind.Any, "KILL 55", "POL-ADM-004")]
    [InlineData(ToolKind.Any, "CREATE ASSEMBLY a FROM 0x00", "POL-EXT-003")]
    [InlineData(ToolKind.Any, "CREATE EXTERNAL DATA SOURCE d WITH (LOCATION = 'https://x')", "POL-EXT-001")]
    [InlineData(ToolKind.Any, "CREATE QUEUE ia.q", "POL-EXT-005")]
    [InlineData(ToolKind.Ddl, "CREATE TRIGGER t ON ALL SERVER FOR LOGON AS PRINT 1", "POL-EXT-005")]
    [InlineData(ToolKind.Ddl, "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS PRINT 1", "POL-EXT-005")]
    [InlineData(ToolKind.Ddl, "DISABLE TRIGGER ALL ON DATABASE", "POL-EXT-005")]
    public void Corpus_Bloqueado(ToolKind tool, string sql, string ruleId)
    {
        Policies.AssertDecision(sql, tool, PolicyAction.Block, ruleId);
    }

    [Theory]
    // ---- Columnas sensibles en modo enmascarar: inferencia y renombrado (RS-DAT-02, CA-29)
    [InlineData("SELECT Nombre FROM ventas.Usuarios WHERE Password = 'x'", "POL-DAT-004")]
    [InlineData("SELECT Nombre FROM ventas.Usuarios ORDER BY Password", "POL-DAT-004")]
    [InlineData("SELECT Nombre FROM ventas.Usuarios GROUP BY Nombre, Password", "POL-DAT-004")]
    [InlineData("SELECT HASHBYTES('SHA2_256', Password) FROM ventas.Usuarios", "POL-DAT-004")]
    [InlineData("SELECT CAST(Password AS varbinary(100)) FROM ventas.Usuarios", "POL-DAT-004")]
    [InlineData("SELECT u.Nombre FROM ventas.Usuarios u JOIN ventas.Sesiones s ON s.Token = u.ApiToken", "POL-DAT-004")]
    [InlineData("SELECT ClienteId FROM ventas.Clientes c WHERE EXISTS (SELECT 1 FROM ventas.Usuarios u WHERE u.Password = 'x')", "POL-DAT-004")]
    [InlineData("SELECT Nombre FROM ventas.Clientes UNION ALL SELECT Password FROM ventas.Usuarios", "POL-DAT-004")]
    [InlineData("WITH c AS (SELECT Password AS p FROM ventas.Usuarios) SELECT p FROM c", "POL-DAT-004")]
    [InlineData("SELECT d.p FROM (SELECT Password AS p FROM ventas.Usuarios) d", "POL-DAT-004")]
    [InlineData("SELECT Password FROM ventas.Usuarios ORDER BY 1", "POL-DAT-004")]
    [InlineData("SELECT Password AS p FROM ventas.Usuarios ORDER BY p", "POL-DAT-004")]
    [InlineData("SELECT Nombre, Password FROM ventas.Usuarios FOR JSON PATH", "POL-DAT-005")]
    [InlineData("SELECT *, Password AS p FROM ventas.Usuarios", "POL-DAT-005")]
    [InlineData("SELECT * FROM ventas.Usuarios FOR XML AUTO", "POL-DAT-005")]
    [InlineData("SELECT Nombre FROM ventas.Clientes UNION ALL SELECT * FROM ventas.Usuarios", "POL-DAT-005")]
    [InlineData("SELECT * FROM (SELECT * FROM ventas.Usuarios) AS d (a, b, c)", "POL-DAT-005")]
    [InlineData("WITH c (a, b) AS (SELECT * FROM ventas.Usuarios) SELECT * FROM c", "POL-DAT-005")]
    [InlineData("SELECT (SELECT * FROM ventas.Usuarios FOR JSON PATH) AS j", "POL-DAT-005")]
    public void SensibleEnModoEnmascarar_Bloqueado(string sql, string ruleId)
    {
        Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Block, ruleId, TestProfiles.WithoutExcludedColumns);
    }

    [Theory]
    [InlineData(ToolKind.Dml, "UPDATE ia.T SET a = 1 OUTPUT deleted.Password WHERE Id = 1", "POL-DAT-006")]
    [InlineData(ToolKind.Dml, "UPDATE ia.T SET Password = 'x' WHERE Id = 1", "POL-DAT-004")]
    [InlineData(ToolKind.Dml, "INSERT INTO ia.Copia SELECT * FROM ventas.Usuarios", "POL-DAT-005")]
    [InlineData(ToolKind.Ddl, "CREATE VIEW ia.v AS SELECT Password AS p FROM ventas.Usuarios", "POL-DAT-004")]
    [InlineData(ToolKind.Ddl, "CREATE VIEW ia.v (a, b) AS SELECT * FROM ventas.Usuarios", "POL-DAT-005")]
    [InlineData(ToolKind.Ddl, "EXEC sp_rename 'ia.U.Password', 'x', 'COLUMN'", "POL-DAT-004")]
    [InlineData(ToolKind.Ddl, "ALTER TABLE ia.U ADD Copia AS (Password)", "POL-DAT-004")]
    public void SensibleEnDmlYDdl_Bloqueado(ToolKind tool, string sql, string ruleId)
    {
        Policies.AssertDecision(sql, tool, PolicyAction.Block, ruleId, TestProfiles.WithoutExcludedColumns);
    }

    [Theory]
    [InlineData("SELECT 1 WHERE 1 = 1", " + 1", 20_000)]
    [InlineData("SELECT 1 WHERE 1 = 1", " AND 1 = 1", 9_000)]
    [InlineData("SELECT 1 WHERE 1 = 1", " OR NOT 1 = 1", 6_000)]
    [InlineData("SELECT 1", " UNION ALL SELECT 1", 5_000)]
    public void CadenasDeOperadoresMuyLargas_NoRompenElProceso(string head, string piece, int count)
    {
        var sql = head + string.Concat(Enumerable.Repeat(piece, count));
        var profile = TestProfiles.Create(c => c.Limits.MaxStatementBytes = 262_144);
        var e = Policies.Evaluate(sql, ToolKind.Query, profile);
        Policies.AssertDecision(e, PolicyAction.Block, "POL-PARSE-003");
    }

    [Theory]
    [InlineData(5_000)]
    [InlineData(30_000)]
    public void ParentesisProfundamenteAnidados_NoRompenElProceso(int depth)
    {
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth);
        var profile = TestProfiles.Create(c => c.Limits.MaxStatementBytes = 262_144);
        Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Block, "POL-PARSE-003", profile);
    }

    [Fact]
    public void SubconsultasProfundamenteAnidadas_NoRompenElProceso()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT (", 1_000)) + "SELECT 1" + new string(')', 1_000);
        var profile = TestProfiles.Create(c => c.Limits.MaxStatementBytes = 262_144);
        Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Block, "POL-PARSE-003", profile);
    }

    [Fact]
    public void IfAnidadosEnUnModulo_NoRompenElProceso()
    {
        var sql = "CREATE PROCEDURE ia.p AS " + string.Concat(Enumerable.Repeat("IF 1 = 1 ", 3_000)) + "SELECT 1";
        var profile = TestProfiles.Create(c => c.Limits.MaxStatementBytes = 262_144);
        Policies.AssertDecision(sql, ToolKind.Ddl, PolicyAction.Block, "POL-PARSE-003", profile);
    }

    [Fact]
    public void SqlDinamicoLiteralEnModoConfirmar_SeAnalizaRecursivamente()
    {
        var profile = TestProfiles.Create(c => c.Policy.DynamicSql = PolicyAction.Confirm);

        Policies.AssertDecision("EXEC('SELECT Nombre FROM ventas.Clientes')", ToolKind.Any, PolicyAction.Confirm, "POL-DYN-002", profile);
        Policies.AssertDecision("EXEC sp_executesql N'SELECT 1'", ToolKind.Any, PolicyAction.Confirm, "POL-DYN-002", profile);
        Policies.AssertDecision("EXEC('GR' + 'ANT SELECT ON ventas.Clientes TO x')", ToolKind.Any, PolicyAction.Block, "POL-DCL-001", profile);
        Policies.AssertDecision("EXEC('xp_cmdshell ''dir''')", ToolKind.Any, PolicyAction.Block, "POL-EXT-003", profile);
        Policies.AssertDecision("EXEC('SELECT * FROM sys.sql_logins')", ToolKind.Any, PolicyAction.Block, "POL-CAT-001", profile);
        Policies.AssertDecision("EXEC sys.sp_executesql @stmt = N'UPDATE ia.T SET a = 1'", ToolKind.Any, PolicyAction.Block, "POL-DML-001", profile);
        Policies.AssertDecision("EXEC('EXEC(''EXEC(''''EXEC(''''''''SELECT 1'''''''')'''')'')')", ToolKind.Any, PolicyAction.Block, "POL-DYN-003", profile);
        Policies.AssertDecision("EXEC('SELEC * FRM')", ToolKind.Any, PolicyAction.Block, "POL-DYN-003", profile);
        Policies.AssertDecision("EXEC master.sys.sp_executesql N'SELECT 1'", ToolKind.Any, PolicyAction.Block, "POL-REF-002", profile);
        Policies.AssertDecision("CREATE PROCEDURE ia.p AS EXEC('DELETE FROM ia.T WHERE Id = 1')", ToolKind.Ddl, PolicyAction.Confirm, "POL-DYN-002", profile);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE ia.p AS BEGIN DECLARE @s nvarchar(max) = N'SELECT 1'; EXEC(@s) END")]
    [InlineData("CREATE PROCEDURE ia.p AS BEGIN DECLARE @s nvarchar(max) = N'SELECT 1'; EXEC sp_executesql @s END")]
    [InlineData("CREATE PROCEDURE ia.p AS BEGIN DECLARE @p sysname = N'ia.q'; EXEC @p END")]
    [InlineData("CREATE PROCEDURE ia.p AS BEGIN DECLARE @t sysname = N'T'; EXEC('SELECT * FROM ia.' + @t) END")]
    public void SqlDinamicoNoLiteral_SeBloqueaInclusoEnModoConfirmar(string sql)
    {
        var profile = TestProfiles.Create(c => c.Policy.DynamicSql = PolicyAction.Confirm);
        Policies.AssertDecision(sql, ToolKind.Ddl, PolicyAction.Block, "POL-DYN-003", profile);
    }
}
