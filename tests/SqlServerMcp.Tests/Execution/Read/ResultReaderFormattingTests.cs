using System.Data.SqlTypes;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Read;

public class ResultReaderFormattingTests
{
    [Fact]
    public void Fechas_ISO8601()
    {
        Assert.Equal("2024-03-05", ResultReader.FormatDate(new DateTime(2024, 3, 5, 10, 0, 0)));
        Assert.Equal("2024-03-05T10:11:12", ResultReader.FormatDateTime(new DateTime(2024, 3, 5, 10, 11, 12)));
        Assert.Equal("2024-03-05T10:11:12.123", ResultReader.FormatDateTime(new DateTime(2024, 3, 5, 10, 11, 12, 123)));
        Assert.Equal("2024-03-05T10:11:12.1234567", ResultReader.FormatDateTime(new DateTime(2024, 3, 5, 10, 11, 12).AddTicks(1234567)));
        Assert.Equal("2024-03-05T10:11:12-05:00", ResultReader.FormatDateTimeOffset(new DateTimeOffset(2024, 3, 5, 10, 11, 12, TimeSpan.FromHours(-5))));
        Assert.Equal("2024-03-05T10:11:12.5+00:00", ResultReader.FormatDateTimeOffset(new DateTimeOffset(2024, 3, 5, 10, 11, 12, 500, TimeSpan.Zero)));
    }

    [Fact]
    public void Horas()
    {
        Assert.Equal("08:30:00", ResultReader.FormatTime(new TimeSpan(8, 30, 0)));
        Assert.Equal("23:59:59.9999999", ResultReader.FormatTime(new TimeSpan(0, 23, 59, 59).Add(TimeSpan.FromTicks(9_999_999))));
        Assert.Equal("00:00:01.25", ResultReader.FormatTime(TimeSpan.FromMilliseconds(1250)));
    }

    [Fact]
    public void FechasInvariantesConCulturaDistinta()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("2024-03-05T10:11:12", ResultReader.FormatDateTime(new DateTime(2024, 3, 5, 10, 11, 12)));
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-CO");
            Assert.Equal("1234.5600", ResultReader.FormatMoney(new SqlMoney(1234.56m)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Numeros()
    {
        Assert.Equal(1.1d, ResultReader.FormatReal(1.1f));
        Assert.Equal("NaN", ResultReader.FormatDouble(double.NaN));
        Assert.Equal("Infinity", ResultReader.FormatDouble(double.PositiveInfinity));
        Assert.Equal("-Infinity", ResultReader.FormatReal(float.NegativeInfinity));
        Assert.Equal(2.5d, ResultReader.FormatDouble(2.5d));
        Assert.Equal("-922337203685477.5808", ResultReader.FormatMoney(SqlMoney.MinValue));
    }

    [Fact]
    public void TruncateText_NoPartePares()
    {
        Assert.Equal("abc", ResultReader.TruncateText("abc", 3));
        Assert.Equal("ab…", ResultReader.TruncateText("abcd", 2));
        var emoji = "a\U0001F600b";
        Assert.Equal("a…", ResultReader.TruncateText(emoji, 2));
    }

    [Fact]
    public void Binarios_Base64Recortado()
    {
        Assert.Equal(3000, ResultReader.MaxBinaryBytes(4000));
        Assert.Equal(6, ResultReader.MaxBinaryBytes(10));
        Assert.Equal(3, ResultReader.MaxBinaryBytes(1));

        var (full, fullTruncated) = ResultReader.FormatBinary(new byte[] { 1, 2, 3 }, 6);
        Assert.Equal("AQID", full);
        Assert.False(fullTruncated);

        var (cut, cutTruncated) = ResultReader.FormatBinary(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, 6);
        Assert.Equal("AQIDBAUG…", cut);
        Assert.True(cutTruncated);
    }

    [Fact]
    public void FormatClrValue_TiposDeSqlVariant()
    {
        Assert.Equal((null, false), ResultReader.FormatClrValue(DBNull.Value, 10));
        Assert.Equal((null, false), ResultReader.FormatClrValue(SqlInt32.Null, 10));
        Assert.Equal(5L, ResultReader.FormatClrValue(new SqlInt32(5), 10).Value);
        Assert.Equal(7L, ResultReader.FormatClrValue((byte)7, 10).Value);
        Assert.Equal("12345678901234567890.123456789", ResultReader.FormatClrValue(SqlDecimal.Parse("12345678901234567890.123456789"), 10).Value);
        Assert.Equal("1.5000", ResultReader.FormatClrValue(new SqlMoney(1.5m), 10).Value);
        Assert.Equal("2024-01-02T03:00:00", ResultReader.FormatClrValue(new SqlDateTime(2024, 1, 2, 3, 0, 0), 10).Value);
        Assert.Equal(true, ResultReader.FormatClrValue(SqlBoolean.True, 10).Value);
        var guid = Guid.NewGuid();
        Assert.Equal(guid.ToString("D"), ResultReader.FormatClrValue(new SqlGuid(guid), 10).Value);
        Assert.Equal(("abcdefghij…", true), ResultReader.FormatClrValue(new SqlString("abcdefghijk"), 10));
        Assert.Equal("AQI=", ResultReader.FormatClrValue(new SqlBinary([1, 2]), 10).Value);
        Assert.Equal("1.5", ResultReader.FormatClrValue(1.5m, 10).Value);
    }

    [Fact]
    public void EstimateJsonBytes()
    {
        Assert.Equal(4, ResultReader.EstimateJsonBytes(null));
        Assert.Equal(3, ResultReader.EstimateJsonBytes(123L));
        Assert.Equal(5, ResultReader.EstimateJsonBytes("abc"));
        Assert.Equal(5, ResultReader.EstimateJsonBytes("añ"));
        Assert.Equal(6, ResultReader.EstimateJsonBytes("a\"b"));
    }

    [Fact]
    public void IsClrUdtType()
    {
        Assert.True(ResultReader.IsClrUdtType("geography"));
        Assert.True(ResultReader.IsClrUdtType("hierarchyid"));
        Assert.True(ResultReader.IsClrUdtType("dbo.MiTipo"));
        Assert.False(ResultReader.IsClrUdtType("nvarchar(50)"));
        Assert.False(ResultReader.IsClrUdtType("decimal(18,2)"));
    }

    [Fact]
    public void BuildWarnings_DescribeTruncamientoMascarasYClr()
    {
        var limits = new LimitsConfig { MaxCellChars = 100, MaxResponseBytes = 2048 };
        var result = new TabularResult
        {
            Columns = [new ResultColumn("Email", "nvarchar(200)", true, Masked: true), new ResultColumn("Ubicacion", "geography", true)],
            Rows = [["****", "AAA="]],
            Truncated = true,
            TruncationReason = ResultReader.ReasonMaxRows,
            TruncatedCells = 2,
        };

        var warnings = ResultReader.BuildWarnings(result, limits);

        Assert.Contains(warnings, w => w.StartsWith("Resultado truncado: se devolvieron 1 filas", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("2 celda(s)", StringComparison.Ordinal) && w.Contains("100", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("enmascaradas", StringComparison.Ordinal) && w.Contains("Email", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'Ubicacion' (geography)", StringComparison.Ordinal));

        var bytes = new TabularResult { Truncated = true, TruncationReason = ResultReader.ReasonMaxResponseBytes };
        Assert.Contains(ResultReader.BuildWarnings(bytes, limits), w => w.Contains("2048 bytes", StringComparison.Ordinal));

        var forJson = new TabularResult { Columns = [new ResultColumn(ResultReader.ForJsonColumnName, "nvarchar(max)", true)], Rows = [["[{"], ["}]"]] };
        Assert.Contains(ResultReader.BuildWarnings(forJson, limits), w => w.Contains("FOR JSON", StringComparison.Ordinal));
        Assert.Empty(ResultReader.BuildWarnings(new TabularResult(), limits));
    }
}
