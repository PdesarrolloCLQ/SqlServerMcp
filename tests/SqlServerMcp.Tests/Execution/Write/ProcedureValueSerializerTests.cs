using System.Data.SqlTypes;
using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-PRC-01: los parámetros de salida usan las mismas reglas que los resultados.</summary>
public class ProcedureValueSerializerTests
{
    [Fact]
    public void Primitivos()
    {
        Assert.Null(Serialize(null));
        Assert.Null(Serialize(DBNull.Value));
        Assert.Null(Serialize(SqlInt32.Null));
        Assert.Equal(7L, Serialize(7));
        Assert.Equal(7L, Serialize((short)7));
        Assert.Equal(7L, Serialize(new SqlInt32(7)));
        Assert.Equal(true, Serialize(true));
        Assert.Equal(1.5d, Serialize(1.5f));
        Assert.Equal("NaN", Serialize(double.NaN));
    }

    [Fact]
    public void DecimalComoTextoInvariante()
    {
        Assert.Equal("1234.5600", Serialize(1234.5600m));
        Assert.Equal("-0.1", Serialize(new SqlDecimal(-0.1m)));
    }

    [Fact]
    public void FechasIso()
    {
        var dt = new DateTime(2026, 11, 1, 8, 30, 15, 120, DateTimeKind.Unspecified);
        Assert.Equal("2026-11-01T08:30:15.12", Serialize(dt));
        Assert.Equal("2026-11-01", ProcedureValueSerializer.Serialize(dt, "date", 4000, out _));
        Assert.Equal("2026-11-01T08:30:15.12-05:00", Serialize(new DateTimeOffset(dt, TimeSpan.FromHours(-5))));
        Assert.Equal("01:02:03", Serialize(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void GuidYBinario()
    {
        var g = Guid.Parse("8c1e0e43-5c5e-4f43-9d0a-a0a7c34e3b11");
        Assert.Equal("8c1e0e43-5c5e-4f43-9d0a-a0a7c34e3b11", Serialize(g));
        Assert.Equal("AQID", Serialize(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void TruncaTextoYBinario()
    {
        var text = ProcedureValueSerializer.Serialize(new string('x', 100), "nvarchar", 20, out var truncated);
        Assert.True(truncated);
        Assert.Equal(20, ((string)text!).Length);

        var bin = ProcedureValueSerializer.Serialize(new byte[100], "varbinary", 20, out var binTruncated);
        Assert.True(binTruncated);
        Assert.True(((string)bin!).Length <= 20);
    }

    private static object? Serialize(object? value) => ProcedureValueSerializer.Serialize(value, null, 4000, out _);
}
