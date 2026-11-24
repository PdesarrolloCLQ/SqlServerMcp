using SqlServerMcp.Execution;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-03: elección de la estrategia de estimación.</summary>
public class DmlEstimatorTests
{
    private readonly DmlEstimator _estimator = new(new DmlCountQueryBuilder());

    [Theory]
    [InlineData("INSERT INTO ia.T (a) VALUES (1)", 1)]
    [InlineData("INSERT INTO ia.T (a) VALUES (1), (2), (@p)", 3)]
    [InlineData("INSERT INTO ia.T DEFAULT VALUES", 1)]
    public void InsertValues_CuentaListasSinIo(string sql, long expected)
    {
        var plan = _estimator.Plan(WriteTestSql.Info(sql));
        Assert.Equal(DmlEstimationStrategy.ValuesRowCount, plan.Strategy);
        Assert.Equal(expected, plan.KnownRows);
    }

    [Theory]
    [InlineData("INSERT INTO ia.T (a) SELECT ClienteId FROM ventas.Clientes")]
    [InlineData("INSERT TOP (2) INTO ia.T (a) VALUES (1), (2), (3)")]
    [InlineData("MERGE ia.T AS t USING ia.S AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.a = s.a;")]
    [InlineData("UPDATE c SET Activo = 0 FROM ventas.Clientes c JOIN ventas.Facturas f ON f.ClienteId = c.ClienteId WHERE f.Total > 1")]
    [InlineData("DELETE TOP (5) PERCENT FROM ventas.Facturas WHERE Total > 1")]
    public void Prueba_RevertidaCuandoNoHayConteoExacto(string sql) =>
        Assert.Equal(DmlEstimationStrategy.TrialExecution, _estimator.Plan(WriteTestSql.Info(sql)).Strategy);

    [Fact]
    public void UpdateSimple_UsaConteo()
    {
        var plan = _estimator.Plan(WriteTestSql.Info("UPDATE ventas.Clientes SET Activo = 0 WHERE Saldo > @s"));
        Assert.Equal(DmlEstimationStrategy.CountQuery, plan.Strategy);
        Assert.Contains("COUNT_BIG(*)", plan.CountSql, StringComparison.Ordinal);
        Assert.Contains("@s", plan.CountSql, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertExec_NoEsEstimable() =>
        Assert.Equal(DmlEstimationStrategy.NotEstimable, _estimator.Plan(WriteTestSql.Info("INSERT INTO ia.T EXEC ventas.usp_X")).Strategy);

    [Fact]
    public void SinFragmento_UsaPruebaParaCategoriasDml()
    {
        var info = new SqlStatementInfo { Category = StatementCategory.Update };
        Assert.Equal(DmlEstimationStrategy.TrialExecution, _estimator.Plan(info).Strategy);
        Assert.Equal(DmlEstimationStrategy.NotEstimable, _estimator.Plan(new SqlStatementInfo { Category = StatementCategory.Query }).Strategy);
    }

    [Theory]
    [InlineData("UPDATE ia.T SET a = 1 OUTPUT inserted.a WHERE a = 2", true)]
    [InlineData("DELETE FROM ia.T OUTPUT deleted.* WHERE a = 2", true)]
    [InlineData("UPDATE ia.T SET a = 1 OUTPUT inserted.a INTO ia.Log (a) WHERE a = 2", false)]
    [InlineData("INSERT INTO ia.T (a) OUTPUT inserted.a VALUES (1)", true)]
    [InlineData("UPDATE ia.T SET a = 1 WHERE a = 2", false)]
    public void ReturnsOutputRows(string sql, bool expected) =>
        Assert.Equal(expected, DmlEstimator.ReturnsOutputRows(WriteTestSql.Info(sql)));
}
