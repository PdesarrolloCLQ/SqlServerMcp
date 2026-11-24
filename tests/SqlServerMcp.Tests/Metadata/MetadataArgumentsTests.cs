using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Metadata;

public class MetadataArgumentsTests
{
    [Fact]
    public void ResolveDatabase_NullDevuelveLaInicial() =>
        Assert.Equal("VentasTest", MetadataArguments.ResolveDatabase(MetadataTestProfiles.Create(), null));

    [Theory]
    [InlineData("ventastest", "VentasTest")]
    [InlineData("[Historico]", "Historico")]
    [InlineData("HISTORICO", "Historico")]
    public void ResolveDatabase_DevuelveElNombreCanonico(string input, string expected) =>
        Assert.Equal(expected, MetadataArguments.ResolveDatabase(MetadataTestProfiles.Create(), input));

    [Theory]
    [InlineData("OtraBD")]
    [InlineData("master")]
    [InlineData("tempdb")]
    public void ResolveDatabase_NoPermitida_PolicyBlocked(string database)
    {
        var ex = Assert.Throws<ToolFailureException>(() => MetadataArguments.ResolveDatabase(MetadataTestProfiles.Create(), database));
        Assert.Equal(ErrorCodes.PolicyBlocked, ex.Code);
    }

    [Fact]
    public void ResolveDatabase_NombreInvalido_InvalidArgument()
    {
        var ex = Assert.Throws<ToolFailureException>(() => MetadataArguments.ResolveDatabase(MetadataTestProfiles.Create(), new string('x', 129)));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0000b")]
    public void RequireIdentifier_RechazaValoresInvalidos(string? value)
    {
        var ex = Assert.Throws<ToolFailureException>(() => MetadataArguments.RequireIdentifier(value, "name"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void RequireIdentifier_RechazaMasDe128Caracteres() =>
        Assert.Throws<ToolFailureException>(() => MetadataArguments.RequireIdentifier(new string('a', 129), "name"));

    [Fact]
    public void RequireIdentifier_QuitaDelimitadores() =>
        Assert.Equal("Mi Tabla", MetadataArguments.RequireIdentifier("[Mi Tabla]", "name"));

    [Theory]
    [InlineData(null, "ventas.Clientes", "ventas", "Clientes")]
    [InlineData("ventas", "Clientes", "ventas", "Clientes")]
    [InlineData(null, "Clientes", "dbo", "Clientes")]
    [InlineData(null, "[ventas].[Cli.entes]", "ventas", "Cli.entes")]
    [InlineData("", "[a.b]", "dbo", "a.b")]
    public void ResolveObjectName_InterpretaEsquemaYNombre(string? schema, string name, string expectedSchema, string expectedName)
    {
        var (s, n) = MetadataArguments.ResolveObjectName(schema, name);
        Assert.Equal(expectedSchema, s);
        Assert.Equal(expectedName, n);
    }

    [Fact]
    public void OptionalPattern_ValidaLongitud()
    {
        Assert.Null(MetadataArguments.OptionalPattern("  ", "namePattern"));
        Assert.Equal("Cli*", MetadataArguments.OptionalPattern(" Cli* ", "namePattern"));
        Assert.Throws<ToolFailureException>(() => MetadataArguments.OptionalPattern(new string('*', 200), "namePattern"));
    }

    [Fact]
    public void RequireSearchText_ValidaLongitudYControl()
    {
        Assert.Equal("Saldo >= 0", MetadataArguments.RequireSearchText("Saldo >= 0"));
        Assert.Throws<ToolFailureException>(() => MetadataArguments.RequireSearchText(""));
        Assert.Throws<ToolFailureException>(() => MetadataArguments.RequireSearchText(new string('a', 257)));
        Assert.Throws<ToolFailureException>(() => MetadataArguments.RequireSearchText("a\nb"));
    }

    [Theory]
    [InlineData(null, RoutineTypeFilter.Any)]
    [InlineData("scalar_function", RoutineTypeFilter.ScalarFunction)]
    [InlineData("TableFunction", RoutineTypeFilter.TableFunction)]
    [InlineData("PROCEDURE", RoutineTypeFilter.Procedure)]
    public void ParseRoutineType_AceptaNombresJsonYMiembros(string? value, RoutineTypeFilter expected) =>
        Assert.Equal(expected, MetadataArgumentParser.ParseRoutineType(value));

    [Fact]
    public void Parse_ValorDesconocido_InvalidArgument()
    {
        var ex = Assert.Throws<ToolFailureException>(() => MetadataArgumentParser.ParseSearchScope("todo"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("definition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseObjectKind_EsObligatorio()
    {
        Assert.Throws<ToolFailureException>(() => MetadataArgumentParser.ParseObjectKind(null));
        Assert.Equal(CatalogObjectKind.Synonym, MetadataArgumentParser.ParseObjectKind("synonym"));
        Assert.Equal(DependencyDirection.Referenced, MetadataArgumentParser.ParseDependencyDirection("referenced"));
        Assert.Equal(SearchObjectType.View, MetadataArgumentParser.ParseSearchObjectType("view"));
    }

    [Fact]
    public void Enumeraciones_SeSerializanEnFormatoSnakeCase()
    {
        Assert.Equal("\"scalar_function\"", System.Text.Json.JsonSerializer.Serialize(RoutineTypeFilter.ScalarFunction));
        Assert.Equal(DependencyDirection.Referencing, System.Text.Json.JsonSerializer.Deserialize<DependencyDirection>("\"referencing\""));
    }
}
