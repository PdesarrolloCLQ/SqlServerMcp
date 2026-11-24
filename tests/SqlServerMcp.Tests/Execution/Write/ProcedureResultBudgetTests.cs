using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-PRC-01: los conjuntos de resultados no superan en total el tamaño máximo de respuesta.</summary>
public class ProcedureResultBudgetTests
{
    [Fact]
    public void DentroDelPresupuesto_NoCambia()
    {
        var set = Set(10, approximateBytes: 100);
        ProcedureExecutor.TrimToBudget(set, 1000);
        Assert.Equal(10, set.Rows.Count);
        Assert.False(set.Truncated);
    }

    [Fact]
    public void FueraDelPresupuesto_RecortaFilasFinales()
    {
        var set = Set(100, approximateBytes: 100_000);
        ProcedureExecutor.TrimToBudget(set, 200);
        Assert.InRange(set.Rows.Count, 1, 99);
        Assert.True(set.ApproximateBytes <= 200);
        Assert.True(set.Truncated);
        Assert.Equal("maxResponseBytes", set.TruncationReason);
        Assert.Equal(0L, set.Rows[0][0]);
    }

    [Fact]
    public void SinPresupuesto_QuedaVacio()
    {
        var set = Set(5, approximateBytes: 500);
        ProcedureExecutor.TrimToBudget(set, 0);
        Assert.Empty(set.Rows);
        Assert.True(set.Truncated);
    }

    private static TabularResult Set(int rows, long approximateBytes) => new()
    {
        Columns = [new ResultColumn("Id", "int", false), new ResultColumn("Nombre", "nvarchar", true)],
        Rows = Enumerable.Range(0, rows).Select(i => new object?[] { (long)i, "Cliente " + i }).ToList(),
        ApproximateBytes = approximateBytes,
    };
}
