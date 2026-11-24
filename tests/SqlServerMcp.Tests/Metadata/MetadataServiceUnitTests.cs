using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Metadata;

/// <summary>Validaciones de <see cref="MetadataService"/> que deben ocurrir antes de abrir ninguna conexión.</summary>
public class MetadataServiceUnitTests
{
    private readonly NoConnectionFactory _factory = new();

    private MetadataService CreateService() => new(_factory, new CatalogLookup(_factory));

    [Fact]
    public void Constructor_ValidaDependencias()
    {
        Assert.Throws<ArgumentNullException>(() => new MetadataService(null!, new CatalogLookup(_factory)));
        Assert.Throws<ArgumentNullException>(() => new MetadataService(_factory, null!));
        Assert.Throws<ArgumentNullException>(() => new CatalogLookup(null!));
    }

    [Fact]
    public async Task BaseNoPermitida_PolicyBlockedSinConectar()
    {
        var service = CreateService();
        var profile = MetadataTestProfiles.Create();
        var ct = TestContext.Current.CancellationToken;

        var calls = new Func<Task>[]
        {
            () => service.ListSchemasAsync(profile, "OtraBD", "c", ct),
            () => service.ListTablesAsync(profile, "OtraBD", null, null, true, 0, 10, "c", ct),
            () => service.DescribeTableAsync(profile, "OtraBD", "ventas", "Clientes", "c", ct),
            () => service.ListRoutinesAsync(profile, "OtraBD", null, null, RoutineTypeFilter.Any, 0, 10, "c", ct),
            () => service.DescribeRoutineAsync(profile, "OtraBD", "ventas", "p", "c", ct),
            () => service.GetObjectDefinitionAsync(profile, "OtraBD", "ventas", "v", "c", ct),
            () => service.ListIndexesAsync(profile, "OtraBD", "ventas", "Clientes", "c", ct),
            () => service.GetRelationshipsAsync(profile, "OtraBD", null, null, "c", ct),
            () => service.GetDependenciesAsync(profile, "OtraBD", "ventas", "Clientes", DependencyDirection.Both, "c", ct),
            () => service.SearchObjectsAsync(profile, "OtraBD", "x", SearchScope.Both, SearchObjectType.Any, 0, 10, "c", ct),
            () => service.GetTableStatsAsync(profile, "OtraBD", null, null, 0, 10, "c", ct),
            () => service.ListObjectsAsync(profile, "master", CatalogObjectKind.Sequence, null, 0, 10, "c", ct),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<ToolFailureException>(call);
            Assert.Equal(ErrorCodes.PolicyBlocked, ex.Code);
        }

        Assert.Equal(0, _factory.OpenCalls);
    }

    [Fact]
    public async Task ArgumentosInvalidos_InvalidArgumentSinConectar()
    {
        var service = CreateService();
        var profile = MetadataTestProfiles.Create();
        var ct = TestContext.Current.CancellationToken;

        var calls = new Func<Task>[]
        {
            () => service.DescribeTableAsync(profile, null, "ventas", "", "c", ct),
            () => service.DescribeTableAsync(profile, null, new string('s', 129), "Clientes", "c", ct),
            () => service.ListTablesAsync(profile, null, null, new string('*', 129), true, 0, 10, "c", ct),
            () => service.SearchObjectsAsync(profile, null, "", SearchScope.Both, SearchObjectType.Any, 0, 10, "c", ct),
            () => service.SearchObjectsAsync(profile, null, new string('x', 300), SearchScope.Both, SearchObjectType.Any, 0, 10, "c", ct),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<ToolFailureException>(call);
            Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        }

        Assert.Equal(0, _factory.OpenCalls);
    }

    [Fact]
    public async Task CatalogLookup_NombresNoResolubles_DevuelveNullSinConectar()
    {
        var lookup = new CatalogLookup(_factory);
        var profile = MetadataTestProfiles.Create();
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await lookup.FindObjectAsync(profile, new SqlObjectName("srv", "VentasTest", "ventas", "Clientes"), ct));
        Assert.Null(await lookup.FindObjectAsync(profile, new SqlObjectName(null, "OtraBD", "ventas", "Clientes"), ct));
        Assert.Null(await lookup.FindObjectAsync(profile, new SqlObjectName(null, null, "seguridad", "Claves"), ct));
        Assert.Null(await lookup.FindObjectAsync(profile, new SqlObjectName(null, null, "dbo", "X"), ct));
        Assert.Null(await lookup.FindObjectAsync(profile, new SqlObjectName(null, null, "ventas", "TarjetasCredito"), ct));
        Assert.Null(await lookup.FindObjectAsync(profile, new SqlObjectName(null, null, "temp", "Cualquiera"), ct));
        Assert.Equal(0, _factory.OpenCalls);
    }

    [Theory]
    [InlineData(1, "SQL Server")]
    [InlineData(3, "SQL Server")]
    [InlineData(5, "Azure SQL Database")]
    [InlineData(8, "Azure SQL Managed Instance")]
    [InlineData(99, "Desconocida")]
    public void DescribeEngineEdition_DescribeLaPlataforma(int edition, string expected) =>
        Assert.Equal(expected, MetadataService.DescribeEngineEdition(edition));

    [Fact]
    public void NotFound_MensajeUniforme()
    {
        var a = MetadataService.NotFound("ventas", "NoExiste");
        var b = MetadataService.NotFound("seguridad", "Claves");
        Assert.Equal(ErrorCodes.NotFound, a.Code);
        Assert.Equal(a.Message.Replace("ventas.NoExiste", "{x}", StringComparison.Ordinal), b.Message.Replace("seguridad.Claves", "{x}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ventas.Clientes", true, false, true)]
    [InlineData("[OtraBD].[dbo].[Algo]", false, false, false)]
    [InlineData("Historico.ventas.Algo", true, false, true)]
    [InlineData("Servidor.Base.dbo.Tabla", false, true, false)]
    [InlineData("dbo.Algo", true, false, false)]
    public void DescribeSynonymTarget_ClasificaElDestino(string baseObject, bool inside, bool fourPart, bool allowed)
    {
        var profile = MetadataTestProfiles.Create();
        var details = MetadataService.DescribeSynonymTarget(profile, "ia", baseObject, ExclusionText.TextPatterns(profile));
        Assert.NotNull(details);
        Assert.Equal(!inside, details.PointsOutsideAllowedDatabases);
        Assert.Equal(fourPart, details.IsFourPartName);
        Assert.Equal(allowed, details.IsTargetAllowed);
    }

    [Theory]
    [InlineData("ventas.TarjetasCredito")]
    [InlineData("[seguridad].[Claves]")]
    [InlineData("temp.Cualquiera")]
    public void DescribeSynonymTarget_DestinoExcluido_SeOculta(string baseObject)
    {
        var profile = MetadataTestProfiles.Create();
        Assert.Null(MetadataService.DescribeSynonymTarget(profile, "ia", baseObject, ExclusionText.TextPatterns(profile)));
    }

    [Fact]
    public void FormatSnippet_ColapsaEspacios()
    {
        Assert.Equal("…SELECT a FROM t…", MetadataService.FormatSnippet("SELECT  a\r\n\tFROM t", truncatedAtStart: true));
        Assert.Equal("x…", MetadataService.FormatSnippet(" x ", truncatedAtStart: false));
        Assert.Null(MetadataService.FormatSnippet(null, false));
    }

    [Theory]
    [InlineData("nvarchar", 200, 0, 0, "nvarchar(100)", 100, null, null)]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)", -1, null, null)]
    [InlineData("varbinary", 64, 0, 0, "varbinary(64)", 64, null, null)]
    [InlineData("decimal", 9, 18, 4, "decimal(18,4)", null, 18, 4)]
    [InlineData("datetime2", 7, 23, 3, "datetime2(3)", null, null, 3)]
    [InlineData("int", 4, 10, 0, "int", null, null, null)]
    [InlineData("sysname", 256, 0, 0, "sysname", null, null, null)]
    public void FormatoDeTipos(string type, int maxLength, int precision, int scale, string declaration, int? length, int? p, int? s)
    {
        Assert.Equal(declaration, CatalogQueries.FormatDataType(type, "sys", false, maxLength, precision, scale));
        Assert.Equal((length, p, s), CatalogQueries.Dimensions(type, maxLength, precision, scale));
    }

    [Fact]
    public void FormatoDeTipos_DeUsuario()
    {
        Assert.Equal("ventas.Telefono", CatalogQueries.FormatDataType("Telefono", "ventas", true, 40, 0, 0));
        Assert.Equal("nvarchar(20)", CatalogQueries.FormatBaseType("nvarchar", 40, 0, 0));
        Assert.Null(CatalogQueries.FormatBaseType(null, 40, 0, 0));
    }

    [Theory]
    [InlineData("P", "procedure")]
    [InlineData("PC", "procedure")]
    [InlineData("FN", "scalar_function")]
    [InlineData("IF", "table_function")]
    [InlineData("TF", "table_function")]
    [InlineData("U", null)]
    public void RoutineKind_MapeaTipos(string code, string? expected) => Assert.Equal(expected, CatalogQueries.RoutineKind(code));
}
