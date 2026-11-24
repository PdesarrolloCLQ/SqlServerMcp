using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-04, RS-HIL-07: margen sobre las filas aprobadas y tamaño de lotes.</summary>
public class DmlRowLimitsTests
{
    [Theory]
    [InlineData(250, 10, 275)]
    [InlineData(100, 10, 110)]
    [InlineData(101, 10, 112)] // techo(10,1) = 11
    [InlineData(1, 10, 2)] // techo(0,1) = 1
    [InlineData(0, 10, 0)]
    [InlineData(250, 0, 250)]
    [InlineData(999, 50, 1499)] // techo(499,5) = 500
    [InlineData(-5, 10, 0)]
    public void MaxAllowedForApproval_AplicaElTecho(long approved, int margin, long expected) =>
        Assert.Equal(expected, DmlRowLimits.MaxAllowedForApproval(approved, margin));

    [Theory]
    [InlineData(275, 250L, 10, false)]
    [InlineData(276, 250L, 10, true)]
    [InlineData(400, 250L, 10, true)]
    [InlineData(1_000_000, null, 10, false)]
    [InlineData(1, 0L, 10, true)]
    public void ExceedsApproval(long actual, long? approved, int margin, bool expected) =>
        Assert.Equal(expected, DmlRowLimits.ExceedsApproval(actual, approved, margin));

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 1000)]
    [InlineData(3, 666)]
    [InlineData(7, 285)]
    [InlineData(2000, 1)]
    [InlineData(4000, 1)]
    public void RowsPerInsertStatement_RespetaFilasYParametros(int columns, int expected)
    {
        var rows = DmlRowLimits.RowsPerInsertStatement(columns);
        Assert.Equal(expected, rows);
        Assert.True(rows <= DmlRowLimits.MaxRowsPerInsertStatement);
        Assert.True(rows * columns <= DmlRowLimits.MaxParametersPerInsertStatement || rows == 1);
    }

    [Theory]
    [InlineData(200, false, false)]
    [InlineData(201, false, true)]
    [InlineData(5000, true, false)]
    public void ShouldUseBulkCopy(int rows, bool returnInserted, bool expected) =>
        Assert.Equal(expected, DmlRowLimits.ShouldUseBulkCopy(rows, returnInserted));
}
