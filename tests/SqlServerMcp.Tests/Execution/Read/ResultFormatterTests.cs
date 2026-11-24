using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Read;

public class ResultFormatterTests
{
    private static TabularResult Sample() => new()
    {
        Columns = [new ResultColumn("Id", "int", false), new ResultColumn("Texto", "nvarchar(50)", true), new ResultColumn("Monto", "decimal(18,2)", true)],
        Rows =
        [
            [1L, "a|b", "10.50"],
            [2L, "línea1\r\nlínea2\nfin", null],
            [3L, "con \"comillas\", y coma", "-1.00"],
            [4L, true, 1.5d],
        ],
    };

    [Fact]
    public void ToMarkdown_EscapaBarrasYSaltos()
    {
        var markdown = ResultFormatter.ToMarkdown(Sample());
        var lines = markdown.Split('\n');

        Assert.Equal("| Id | Texto | Monto |", lines[0]);
        Assert.Equal("| --- | --- | --- |", lines[1]);
        Assert.Equal(@"| 1 | a\|b | 10.50 |", lines[2]);
        Assert.Equal("| 2 | línea1<br>línea2<br>fin | NULL |", lines[3]);
        Assert.Equal("| 4 | true | 1.5 |", lines[5]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public void ToMarkdown_SinColumnas() => Assert.Equal("(sin columnas)", ResultFormatter.ToMarkdown(new TabularResult()));

    [Fact]
    public void ToCsv_Rfc4180()
    {
        var csv = ResultFormatter.ToCsv(Sample());
        var expected =
            "Id,Texto,Monto\r\n" +
            "1,a|b,10.50\r\n" +
            "2,\"línea1\r\nlínea2\nfin\",\r\n" +
            "3,\"con \"\"comillas\"\", y coma\",-1.00\r\n" +
            "4,true,1.5\r\n";
        Assert.Equal(expected, csv);
    }

    [Fact]
    public void ToCsv_EncabezadoConComa()
    {
        var result = new TabularResult { Columns = [new ResultColumn("a,b", "int", true)], Rows = [[null]] };
        Assert.Equal("\"a,b\"\r\n\r\n", ResultFormatter.ToCsv(result));
    }

    [Fact]
    public void FormatValue_Invariante()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-CO");
            Assert.Equal("1234.5", ResultFormatter.FormatValue(1234.5d));
            Assert.Equal("-42", ResultFormatter.FormatValue(-42L));
            Assert.Equal("false", ResultFormatter.FormatValue(false));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
