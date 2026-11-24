using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Metadata;

public class MetadataPagingTests
{
    [Theory]
    [InlineData(0, 100, 0, 100)]
    [InlineData(-5, 100, 0, 100)]
    [InlineData(10, 0, 10, 1)]
    [InlineData(10, -3, 10, 1)]
    [InlineData(0, 500, 0, 500)]
    [InlineData(0, 501, 0, 500)]
    [InlineData(0, int.MaxValue, 0, 500)]
    [InlineData(int.MinValue, int.MinValue, 0, 1)]
    public void Normalize_AcotaDesplazamientoYLimite(int offset, int limit, int expectedOffset, int expectedLimit)
    {
        var (o, l) = MetadataPaging.Normalize(offset, limit);
        Assert.Equal(expectedOffset, o);
        Assert.Equal(expectedLimit, l);
    }

    [Fact]
    public void Page_CalculaHasMore()
    {
        var page = MetadataPaging.Page("db", ["a", "b"], 5, 0, 2);
        Assert.True(page.HasMore);
        Assert.Equal(5, page.TotalCount);

        var last = MetadataPaging.Page("db", ["e"], 5, 4, 2);
        Assert.False(last.HasMore);
    }

    [Fact]
    public void Page_TotalesMayoresQueIntSeAcotan()
    {
        var page = MetadataPaging.Page("db", Array.Empty<string>(), long.MaxValue, 0, 10);
        Assert.Equal(int.MaxValue, page.TotalCount);
    }

    [Fact]
    public void PageInMemory_DevuelveLaVentana()
    {
        var all = Enumerable.Range(1, 7).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var page = MetadataPaging.PageInMemory("db", all, 5, 5);
        Assert.Equal(["6", "7"], page.Items);
        Assert.Equal(7, page.TotalCount);
        Assert.False(page.HasMore);
        Assert.Equal("db", page.Database);
    }
}
