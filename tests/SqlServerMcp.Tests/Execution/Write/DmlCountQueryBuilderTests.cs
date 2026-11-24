using System.Text.RegularExpressions;
using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-03: conteo con el mismo origen y predicado.</summary>
public class DmlCountQueryBuilderTests
{
    private readonly DmlCountQueryBuilder _builder = new();

    [Fact]
    public void Update_GeneraConteoConElMismoPredicado()
    {
        var sql = Build("UPDATE ventas.Clientes SET Activo = 0 WHERE Saldo > 100 AND Activo = 1");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ventas.Clientes WHERE Saldo > 100 AND Activo = 1", sql);
    }

    [Fact]
    public void Delete_ConFromOpcionalYCorchetes()
    {
        var sql = Build("DELETE FROM [ventas].[Facturas] WHERE [ClienteId] = 5");
        Assert.Equal("SELECT COUNT_BIG(*) FROM [ventas].[Facturas] WHERE [ClienteId] = 5", sql);
    }

    [Fact]
    public void ConservaParametros()
    {
        var sql = Build("UPDATE ventas.Clientes SET Saldo = Saldo + @delta WHERE ClienteId BETWEEN @desde AND @hasta");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ventas.Clientes WHERE ClienteId BETWEEN @desde AND @hasta", sql);
        Assert.DoesNotContain("@delta", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ConservaCte()
    {
        var sql = Build("WITH c AS (SELECT ClienteId, Activo FROM ventas.Clientes WHERE Saldo < @s) UPDATE c SET Activo = 0 WHERE ClienteId > 10");
        Assert.Equal("WITH c AS (SELECT ClienteId, Activo FROM ventas.Clientes WHERE Saldo < @s) SELECT COUNT_BIG(*) FROM c WHERE ClienteId > 10", sql);
    }

    [Fact]
    public void Top_SeEnvuelveEnTablaDerivada()
    {
        var sql = Build("UPDATE TOP (10) ventas.Clientes SET Activo = 0 WHERE Activo = 1");
        Assert.Equal("SELECT COUNT_BIG(*) FROM (SELECT TOP (10) 1 AS x FROM ventas.Clientes WHERE Activo = 1) AS q", sql);
    }

    [Fact]
    public void DeleteTopConParametro()
    {
        var sql = Build("DELETE TOP (@n) FROM ventas.Facturas WHERE Total < @t");
        Assert.Equal("SELECT COUNT_BIG(*) FROM (SELECT TOP (@n) 1 AS x FROM ventas.Facturas WHERE Total < @t) AS q", sql);
    }

    [Fact]
    public void TopPercent_NoEsEstimable() =>
        Assert.Null(Build("UPDATE TOP (10) PERCENT ventas.Clientes SET Activo = 0 WHERE Activo = 1"));

    [Fact]
    public void ConFrom_NoEsEstimablePorConteo() =>
        Assert.Null(Build("UPDATE c SET Activo = 0 FROM ventas.Clientes AS c JOIN ventas.Facturas f ON f.ClienteId = c.ClienteId WHERE f.Total > 10"));

    [Fact]
    public void DeleteConFromYAlias_NoEsEstimable() =>
        Assert.Null(Build("DELETE f FROM ventas.Facturas f WHERE f.Total > 10"));

    [Fact]
    public void QuitaSugerenciasDeTabla()
    {
        var sql = Build("UPDATE ventas.Clientes WITH (UPDLOCK, ROWLOCK) SET Activo = 0 WHERE ClienteId = 1");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ventas.Clientes WHERE ClienteId = 1", sql);
    }

    [Fact]
    public void ConservaSugerenciasDeConsulta()
    {
        var sql = Build("DELETE FROM ventas.Facturas WHERE Total > 1 OPTION (MAXDOP 1)");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ventas.Facturas WHERE Total > 1 OPTION (MAXDOP 1)", sql);
    }

    [Fact]
    public void SinWhere_CuentaTodaLaTabla()
    {
        var sql = Build("DELETE FROM ia.Temporal");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ia.Temporal", sql);
    }

    [Fact]
    public void SubconsultaEnPredicado()
    {
        var sql = Build("DELETE FROM ventas.Facturas WHERE ClienteId IN (SELECT ClienteId FROM ventas.Clientes WHERE Activo = 0)");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ventas.Facturas WHERE ClienteId IN (SELECT ClienteId FROM ventas.Clientes WHERE Activo = 0)", sql);
    }

    [Fact]
    public void ConOutput_IgnoraLaClausula()
    {
        var sql = Build("UPDATE ventas.Clientes SET Activo = 0 OUTPUT inserted.ClienteId, deleted.Activo WHERE ClienteId < 5");
        Assert.Equal("SELECT COUNT_BIG(*) FROM ventas.Clientes WHERE ClienteId < 5", sql);
    }

    [Theory]
    [InlineData("INSERT INTO ventas.Clientes (Nombre) VALUES (N'a')")]
    [InlineData("MERGE ia.T AS t USING ia.S AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.a = s.a;")]
    [InlineData("SELECT 1")]
    public void OtrasSentencias_DevuelvenNull(string sql) =>
        Assert.Null(_builder.Build(WriteTestSql.Parse(sql)));

    [Fact]
    public void FragmentoNulo_DevuelveNull() => Assert.Null(_builder.Build(null));

    [Fact]
    public void NoModificaElArbolOriginal()
    {
        var statement = WriteTestSql.Parse("UPDATE TOP (5) ventas.Clientes WITH (ROWLOCK) SET Activo = 0 WHERE ClienteId = @id");
        var before = Generate(statement);
        _ = _builder.Build(statement);
        Assert.Equal(before, Generate(statement));
    }

    private string? Build(string sql)
    {
        var text = _builder.Build(WriteTestSql.Parse(sql));
        return text is null ? null : Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Generate(Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragment fragment)
    {
        new Microsoft.SqlServer.TransactSql.ScriptDom.Sql170ScriptGenerator().GenerateScript(fragment, out var script);
        return script;
    }
}
