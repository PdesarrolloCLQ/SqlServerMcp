using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Metadata;

public class LikePatternsTests
{
    [Theory]
    [InlineData("Cli*", "Cli%")]
    [InlineData("*entes", "%entes")]
    [InlineData("C?iente", "C_iente")]
    [InlineData("*", "%")]
    [InlineData("a*b?c", "a%b_c")]
    public void FromWildcard_ConvierteComodines(string pattern, string expected) =>
        Assert.Equal(expected, LikePatterns.FromWildcard(pattern, containsWhenNoWildcards: true));

    [Theory]
    [InlineData("100%", "%100\\%%")]
    [InlineData("Tabla_1", "%Tabla\\_1%")]
    [InlineData("[a-z]", "%\\[a-z]%")]
    [InlineData("c:\\x", "%c:\\\\x%")]
    public void FromWildcard_SinComodines_BuscaSubcadenaEscapada(string pattern, string expected) =>
        Assert.Equal(expected, LikePatterns.FromWildcard(pattern, containsWhenNoWildcards: true));

    [Fact]
    public void FromWildcard_SinComodines_CoincidenciaExactaSiNoSePideSubcadena() =>
        Assert.Equal("Clientes", LikePatterns.FromWildcard("Clientes", containsWhenNoWildcards: false));

    [Fact]
    public void FromWildcard_IntentoDeInyeccion_SeTrataLiteralmente()
    {
        // Las comillas no se escapan aquí porque el patrón viaja siempre como parámetro (RS-INJ-01);
        // los caracteres especiales de LIKE sí se escapan para que no actúen como comodines.
        var like = LikePatterns.FromWildcard("%' OR 1=1 --", containsWhenNoWildcards: true);
        Assert.Equal("%\\%' OR 1=1 --%", like);
    }

    [Fact]
    public void FromWildcard_ClaseDeCaracteres_SeEscapa()
    {
        var like = LikePatterns.FromWildcard("[a-z]%", containsWhenNoWildcards: true);
        Assert.Equal("%\\[a-z]\\%%", like);
    }

    [Fact]
    public void FromWildcard_MezclaComodinesYEspeciales()
    {
        var like = LikePatterns.FromWildcard("v_*%", containsWhenNoWildcards: true);
        Assert.Equal("v\\_%\\%", like);
    }

    [Fact]
    public void ContainsWildcard_EnvuelveEnPorcentajes() =>
        Assert.Equal("%Tarjeta%%", LikePatterns.ContainsWildcard("Tarjeta*"));

    [Fact]
    public void EscapeChar_EsBarraInvertida() => Assert.Equal('\\', LikePatterns.EscapeChar);
}
