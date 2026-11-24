using SqlServerMcp.Execution;
using SqlServerMcp.Metadata;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DDL-05..08: qué sentencias respaldan la definición, informan filas o dependientes.</summary>
public class DdlStatementClassifierTests
{
    [Theory]
    [InlineData("CREATE TABLE ia.X (Id int)", false)]
    [InlineData("ALTER TABLE ia.X ADD Nombre nvarchar(10) NULL", true)]
    [InlineData("DROP TABLE ia.X", true)]
    [InlineData("TRUNCATE TABLE ia.X", true)]
    [InlineData("CREATE OR ALTER VIEW ia.v AS SELECT 1 AS a", true)]
    [InlineData("ALTER VIEW ia.v AS SELECT 1 AS a", true)]
    [InlineData("EXEC sp_rename 'ia.X', 'Y'", true)]
    [InlineData("DISABLE TRIGGER ia.tr ON ia.X", true)]
    public void RequiresBackup(string sql, bool expected) =>
        Assert.Equal(expected, DdlStatementClassifier.RequiresBackup(WriteTestSql.Info(sql)));

    [Theory]
    [InlineData("DROP TABLE ia.X", true)]
    [InlineData("TRUNCATE TABLE ia.X", true)]
    [InlineData("ALTER TABLE ia.X DROP COLUMN Nombre", true)]
    [InlineData("ALTER TABLE ia.X DROP CONSTRAINT PK_X", false)]
    [InlineData("ALTER TABLE ia.X ALTER COLUMN Nombre nvarchar(20) NULL", true)]
    [InlineData("ALTER TABLE ia.X ADD Otro int NULL", false)]
    [InlineData("CREATE TABLE ia.X (Id int)", false)]
    public void NeedsApproximateRows(string sql, bool expected) =>
        Assert.Equal(expected, DdlStatementClassifier.NeedsApproximateRows(WriteTestSql.Info(sql)));

    [Theory]
    [InlineData("DROP VIEW ia.v", true)]
    [InlineData("ALTER PROCEDURE ia.p AS SELECT 1", true)]
    [InlineData("EXEC sp_rename 'ia.X', 'Y'", true)]
    [InlineData("TRUNCATE TABLE ia.X", false)]
    [InlineData("CREATE TABLE ia.X (Id int)", false)]
    public void NeedsDependents(string sql, bool expected) =>
        Assert.Equal(expected, DdlStatementClassifier.NeedsDependents(WriteTestSql.Info(sql)));

    [Theory]
    [InlineData("CREATE FULLTEXT CATALOG ftc", true)]
    [InlineData("DROP FULLTEXT INDEX ON ia.X", true)]
    [InlineData("CREATE TABLE ia.X (Id int)", false)]
    public void IsNonTransactional(string sql, bool expected) =>
        Assert.Equal(expected, DdlStatementClassifier.IsNonTransactional(WriteTestSql.Info(sql)));

    [Theory]
    [InlineData("CREATE TABLE ia.X (Id int)", null, "CREATE")]
    [InlineData("CREATE OR ALTER PROCEDURE ia.p AS SELECT 1", "CREATE", "CREATE OR ALTER")]
    [InlineData("EXEC sp_rename 'ia.X', 'Y'", "EXECUTE", "RENAME")]
    [InlineData("DROP TABLE ia.X", null, "DROP")]
    [InlineData("TRUNCATE TABLE ia.X", "truncate", "TRUNCATE")]
    public void OperationName(string sql, string? operation, string expected) =>
        Assert.Equal(expected, DdlStatementClassifier.OperationName(WriteTestSql.Info(sql, operation: operation)));

    [Fact]
    public void IsDdlCategory()
    {
        Assert.True(DdlStatementClassifier.IsDdlCategory(StatementCategory.DdlCreate));
        Assert.True(DdlStatementClassifier.IsDdlCategory(StatementCategory.ExtendedProperty));
        Assert.False(DdlStatementClassifier.IsDdlCategory(StatementCategory.Update));
        Assert.False(DdlStatementClassifier.IsDdlCategory(StatementCategory.Dcl));
    }

    [Fact]
    public void DescribeTable_GeneraJsonCompacto()
    {
        var table = new CatalogObject(1, "ia", "X", "U", "USER_TABLE");
        var json = DdlExecutor.DescribeTable(table,
        [
            new CatalogColumn(2, "Nombre", new CatalogType("nvarchar", 100, 0, 0), true, false, false, false, false, false),
            new CatalogColumn(1, "Id", new CatalogType("int", 4, 10, 0), false, true, false, false, false, false),
        ]);
        Assert.Equal(
            "{\"object\":\"ia.X\",\"type\":\"USER_TABLE\",\"columns\":[" +
            "{\"name\":\"Id\",\"type\":\"int\",\"nullable\":false,\"identity\":true,\"computed\":false}," +
            "{\"name\":\"Nombre\",\"type\":\"nvarchar(50)\",\"nullable\":true,\"identity\":false,\"computed\":false}]}",
            json);
    }
}
