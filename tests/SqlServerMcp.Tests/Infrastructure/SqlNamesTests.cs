using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests.Infrastructure;

public class SqlNamesTests
{
    [Theory]
    [InlineData("[dbo]", "dbo")]
    [InlineData("\"dbo\"", "dbo")]
    [InlineData("[a]]b]", "a]b")]
    [InlineData("  ventas ", "ventas")]
    public void Normalize_QuitaDelimitadores(string input, string expected) =>
        Assert.Equal(expected, SqlNames.Normalize(input));

    [Fact]
    public void Quote_EscapaCorchetes() => Assert.Equal("[a]]b]", SqlNames.Quote("a]b"));

    [Theory]
    [InlineData("ventas.Clientes", "ventas", "Clientes")]
    [InlineData("[ventas].[Cli.entes]", "ventas", "Cli.entes")]
    [InlineData("Clientes", null, "Clientes")]
    public void TryParseSchemaObject_SeparaPartes(string input, string? schema, string name)
    {
        Assert.True(SqlNames.TryParseSchemaObject(input, out var s, out var n));
        Assert.Equal(schema, s);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData("a.b.c")]
    [InlineData("")]
    [InlineData("[sin cierre")]
    public void TryParseSchemaObject_RechazaInvalidos(string input) =>
        Assert.False(SqlNames.TryParseSchemaObject(input, out _, out _));

    [Theory]
    [InlineData("*password*", "UserPassword", true)]
    [InlineData("*contrasena*", "Contrasena", true)]
    [InlineData("ventas.*", "ventas.x", true)]
    [InlineData("Token", "token", true)]
    [InlineData("Token", "tokens", false)]
    public void WildcardMatch(string pattern, string value, bool expected) =>
        Assert.Equal(expected, SqlNames.WildcardMatch(pattern, value));
}
