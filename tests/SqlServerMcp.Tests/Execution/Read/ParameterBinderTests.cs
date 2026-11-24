using System.Data;
using System.Data.SqlTypes;
using System.Text.Json;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests.Execution.Read;

public class ParameterBinderTests
{
    private readonly ParameterBinder _binder = new();

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private Microsoft.Data.SqlClient.SqlParameter Bind(string name, string? type, string json) =>
        _binder.Bind(new QueryParameter(name, type, Json(json)));

    private static void AssertInvalid(Action action, string? fragment = null)
    {
        var ex = Assert.Throws<ToolFailureException>(action);
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        if (fragment is not null)
        {
            Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("id", "@id")]
    [InlineData("@id", "@id")]
    [InlineData("_x1", "@_x1")]
    [InlineData("@Cliente_Id2", "@Cliente_Id2")]
    public void Bind_NormalizaNombre(string name, string expected) =>
        Assert.Equal(expected, Bind(name, null, "1").ParameterName);

    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("1abc")]
    [InlineData("@a b")]
    [InlineData("@a;DROP")]
    [InlineData("@a nvarchar(10)")]
    [InlineData("@@x")]
    [InlineData("@año")]
    public void Bind_RechazaNombresInvalidos(string name) =>
        AssertInvalid(() => Bind(name, null, "1"));

    [Fact]
    public void Bind_RechazaNombreDemasiadoLargo()
    {
        Assert.Equal(128, Bind(new string('a', 127), null, "1").ParameterName.Length);
        AssertInvalid(() => Bind(new string('a', 128), null, "1"));
    }

    [Fact]
    public void Inferencia_CadenaCortaYLarga()
    {
        var shortParam = Bind("s", null, "\"hola\"");
        Assert.Equal(SqlDbType.NVarChar, shortParam.SqlDbType);
        Assert.Equal(4000, shortParam.Size);
        Assert.Equal("hola", shortParam.Value);

        var longParam = Bind("s", null, "\"" + new string('x', 4001) + "\"");
        Assert.Equal(-1, longParam.Size);
    }

    [Fact]
    public void Inferencia_EnteroBooleanoYNull()
    {
        var integer = Bind("i", null, "42");
        Assert.Equal(SqlDbType.BigInt, integer.SqlDbType);
        Assert.Equal(42L, integer.Value);

        var boolean = Bind("b", null, "true");
        Assert.Equal(SqlDbType.Bit, boolean.SqlDbType);
        Assert.Equal(true, boolean.Value);

        var nil = Bind("n", null, "null");
        Assert.Equal(SqlDbType.NVarChar, nil.SqlDbType);
        Assert.Equal(DBNull.Value, nil.Value);
    }

    [Fact]
    public void Inferencia_DecimalExactoSinPerdida()
    {
        var p = Bind("d", null, "12345678901234567890.123456789");
        Assert.Equal(SqlDbType.Decimal, p.SqlDbType);
        Assert.Equal(29, p.Precision);
        Assert.Equal(9, p.Scale);
        Assert.Equal("12345678901234567890.123456789", ((SqlDecimal)p.Value).ToString());
    }

    [Fact]
    public void Inferencia_EnteroMayorQueBigintEsDecimal()
    {
        var p = Bind("d", null, "123456789012345678901234");
        Assert.Equal(SqlDbType.Decimal, p.SqlDbType);
        Assert.Equal("123456789012345678901234", ((SqlDecimal)p.Value).ToString());
    }

    [Fact]
    public void Inferencia_ExponenteSeConvierteExacto()
    {
        var p = Bind("d", null, "1.5e-3");
        Assert.Equal(SqlDbType.Decimal, p.SqlDbType);
        Assert.Equal("0.0015", ((SqlDecimal)p.Value).ToString());
    }

    [Fact]
    public void Inferencia_RechazaNumeroFueraDeDecimal38() =>
        AssertInvalid(() => Bind("d", null, "1" + new string('0', 40) + ".5"), "decimal(38)");

    [Theory]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    public void Inferencia_RechazaValoresNoEscalares(string json) => AssertInvalid(() => Bind("x", null, json), "escalares");

    [Theory]
    [InlineData("int", "5", 5)]
    [InlineData("int", "\"-7\"", -7)]
    [InlineData("INT", "2147483647", 2147483647)]
    [InlineData("int", "3.0", 3)]
    public void Int_ConvierteValores(string type, string json, int expected)
    {
        var p = Bind("x", type, json);
        Assert.Equal(SqlDbType.Int, p.SqlDbType);
        Assert.Equal(expected, p.Value);
    }

    [Theory]
    [InlineData("int", "2147483648")]
    [InlineData("tinyint", "256")]
    [InlineData("tinyint", "-1")]
    [InlineData("smallint", "40000")]
    [InlineData("int", "1.5")]
    [InlineData("bigint", "\"12a\"")]
    [InlineData("int", "true")]
    public void Enteros_RechazanFueraDeRangoYFormato(string type, string json) =>
        AssertInvalid(() => Bind("x", type, json), "'@x'");

    [Theory]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("\"1\"", true)]
    [InlineData("\"FALSE\"", false)]
    public void Bit_AceptaFormasEstrictas(string json, bool expected) =>
        Assert.Equal(expected, Bind("b", "bit", json).Value);

    [Theory]
    [InlineData("2")]
    [InlineData("\"si\"")]
    public void Bit_RechazaOtrosValores(string json) => AssertInvalid(() => Bind("b", "bit", json));

    [Fact]
    public void Decimal_RespetaPrecisionYEscala()
    {
        var p = Bind("d", "decimal(18, 2)", "\"1234.5\"");
        Assert.Equal(SqlDbType.Decimal, p.SqlDbType);
        Assert.Equal(18, p.Precision);
        Assert.Equal(2, p.Scale);
        Assert.Equal("1234.50", ((SqlDecimal)p.Value).ToString());

        var trailing = Bind("d", "numeric(5,2)", "1.230");
        Assert.Equal("1.23", ((SqlDecimal)trailing.Value).ToString());

        AssertInvalid(() => Bind("d", "decimal(18,2)", "1.234"), "decimal(18,2)");
        AssertInvalid(() => Bind("d", "decimal(4,2)", "123.4"));
        AssertInvalid(() => Bind("d", "decimal(18,2)", "\"1,5\""));
    }

    [Fact]
    public void Money_ValidaEscalaYRango()
    {
        Assert.Equal(12.3456m, Bind("m", "money", "12.3456").Value);
        AssertInvalid(() => Bind("m", "money", "1.23456"));
        AssertInvalid(() => Bind("m", "smallmoney", "300000"));
        Assert.Equal(-214748.3648m, Bind("m", "smallmoney", "-214748.3648").Value);
    }

    [Fact]
    public void FloatYReal()
    {
        Assert.Equal(1.5d, Bind("f", "float", "1.5").Value);
        Assert.Equal(2.25d, Bind("f", "float", "\"2.25\"").Value);
        Assert.Equal(SqlDbType.Real, Bind("f", "float(10)", "1").SqlDbType);
        Assert.Equal(1.5f, Bind("f", "real", "1.5").Value);
        AssertInvalid(() => Bind("f", "float", "\"NaN\""));
        AssertInvalid(() => Bind("f", "real", "1e300"));
    }

    [Fact]
    public void Fechas_ISO8601Estrictas()
    {
        var date = Bind("d", "date", "\"2024-02-29\"");
        Assert.Equal(SqlDbType.Date, date.SqlDbType);
        Assert.Equal(new DateTime(2024, 2, 29), date.Value);
        AssertInvalid(() => Bind("d", "date", "\"29/02/2024\""));
        AssertInvalid(() => Bind("d", "date", "\"2023-02-29\""));

        var dt2 = Bind("d", "datetime2(3)", "\"2024-01-02T03:04:05.123\"");
        Assert.Equal(SqlDbType.DateTime2, dt2.SqlDbType);
        Assert.Equal(3, dt2.Scale);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, 123), dt2.Value);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 0), Bind("d", "datetime", "\"2024-01-02 03:04\"").Value);
        Assert.Equal(new DateTime(2024, 1, 2), Bind("d", "datetime2", "\"2024-01-02\"").Value);

        AssertInvalid(() => Bind("d", "datetime2", "\"2024-01-02T03:04:05Z\""), "datetimeoffset");
        AssertInvalid(() => Bind("d", "datetime", "\"1700-01-01\""), "1753");
        AssertInvalid(() => Bind("d", "smalldatetime", "\"2080-01-01\""));
        AssertInvalid(() => Bind("d", "datetime", "20240101"));
    }

    [Fact]
    public void DateTimeOffset_RequiereDesplazamiento()
    {
        var p = Bind("d", "datetimeoffset(7)", "\"2024-01-02T03:04:05.5-05:00\"");
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, 500, TimeSpan.FromHours(-5)), p.Value);
        var zulu = Bind("d", "datetimeoffset", "\"2024-01-02T03:04:05Z\"");
        Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)zulu.Value).Offset);
        AssertInvalid(() => Bind("d", "datetimeoffset", "\"2024-01-02T03:04:05\""));
    }

    [Fact]
    public void Time_Valida()
    {
        var p = Bind("t", "time(3)", "\"13:45:10.25\"");
        Assert.Equal(SqlDbType.Time, p.SqlDbType);
        Assert.Equal(new TimeSpan(0, 13, 45, 10, 250), p.Value);
        Assert.Equal(new TimeSpan(8, 30, 0), Bind("t", "time", "\"08:30\"").Value);
        AssertInvalid(() => Bind("t", "time", "\"25:00\""));
    }

    [Fact]
    public void Guid_Base64_Xml()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(guid, Bind("g", "uniqueidentifier", $"\"{guid}\"").Value);
        AssertInvalid(() => Bind("g", "uniqueidentifier", "\"no-es-guid\""));

        var bin = Bind("b", "varbinary(4)", "\"AQID\"");
        Assert.Equal(new byte[] { 1, 2, 3 }, bin.Value);
        Assert.Equal(4, bin.Size);
        AssertInvalid(() => Bind("b", "varbinary(2)", "\"AQID\""), "bytes");
        AssertInvalid(() => Bind("b", "binary(8)", "\"%%%\""), "Base64");
        Assert.Equal(-1, Bind("b", "varbinary(max)", "\"AQID\"").Size);
        Assert.Equal(8000, Bind("b", "varbinary", "\"AQID\"").Size);

        var xml = Bind("x", "xml", "\"<a>1</a>\"");
        Assert.Equal(SqlDbType.Xml, xml.SqlDbType);
        Assert.Equal("<a>1</a>", xml.Value);
    }

    [Fact]
    public void Cadenas_ValidanLongitud()
    {
        var p = Bind("s", "nvarchar(5)", "\"abcde\"");
        Assert.Equal(5, p.Size);
        AssertInvalid(() => Bind("s", "nvarchar(5)", "\"abcdef\""), "5");
        AssertInvalid(() => Bind("s", "char", "\"ab\""));
        Assert.Equal("123.5", Bind("s", "varchar(10)", "123.5").Value);
        Assert.Equal(-1, Bind("s", "nvarchar", "\"" + new string('a', 4001) + "\"").Size);
        Assert.Equal(4000, Bind("s", "nvarchar", "\"a\"").Size);
        Assert.Equal(-1, Bind("s", "varchar(MAX)", "\"a\"").Size);
        AssertInvalid(() => Bind("s", "nvarchar(10)", "true"));
    }

    [Fact]
    public void NullConTipoExplicito()
    {
        var p = Bind("x", "int", "null");
        Assert.Equal(SqlDbType.Int, p.SqlDbType);
        Assert.Equal(DBNull.Value, p.Value);
        var s = Bind("s", "nvarchar", "null");
        Assert.Equal(4000, s.Size);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("sql_variant")]
    [InlineData("geography")]
    [InlineData("nvarchar(4001)")]
    [InlineData("varchar(0)")]
    [InlineData("decimal(39,2)")]
    [InlineData("decimal(5,6)")]
    [InlineData("int(4)")]
    [InlineData("datetime2(8)")]
    [InlineData("nvarchar(10); DROP TABLE x")]
    [InlineData("nvarchar(10,2)")]
    [InlineData("char(max)")]
    public void TiposInvalidos(string type) => AssertInvalid(() => Bind("x", type, "null"), "tipo");

    [Fact]
    public void BindForCatalogType_UsaTipoBaseYLongitudEnBytes()
    {
        var nvarchar = _binder.BindForCatalogType("@Nombre", new CatalogType("nvarchar", 100, 0, 0), Json("\"Ana\""));
        Assert.Equal(SqlDbType.NVarChar, nvarchar.SqlDbType);
        Assert.Equal(50, nvarchar.Size);
        AssertInvalid(() => _binder.BindForCatalogType("@Nombre", new CatalogType("nvarchar", 4, 0, 0), Json("\"Ana\"")));

        var alias = _binder.BindForCatalogType("Telefono", new CatalogType("TipoTelefono", 20, 0, 0, true, "varchar"), Json("\"3001234567\""));
        Assert.Equal("@Telefono", alias.ParameterName);
        Assert.Equal(SqlDbType.VarChar, alias.SqlDbType);
        Assert.Equal(20, alias.Size);

        var dec = _binder.BindForCatalogType("@Saldo", new CatalogType("decimal", 9, 18, 4), Json("10.5"));
        Assert.Equal(18, dec.Precision);
        Assert.Equal(4, dec.Scale);
        Assert.Equal("10.5000", ((SqlDecimal)dec.Value).ToString());

        var max = _binder.BindForCatalogType("@Notas", new CatalogType("nvarchar", -1, 0, 0), Json("\"x\""));
        Assert.Equal(-1, max.Size);

        var dt2 = _binder.BindForCatalogType("@F", new CatalogType("datetime2", 7, 23, 3), Json("\"2024-01-01T10:00:00.5\""));
        Assert.Equal(3, dt2.Scale);

        var unicodeName = _binder.BindForCatalogType("@Año", new CatalogType("int", 4, 10, 0), Json("2024"));
        Assert.Equal("@Año", unicodeName.ParameterName);

        AssertInvalid(() => _binder.BindForCatalogType("@G", new CatalogType("geography", -1, 0, 0, true), Json("null")), "no se admite");
        AssertInvalid(() => _binder.BindForCatalogType("@a b", new CatalogType("int", 4, 10, 0), Json("1")));
    }

    [Fact]
    public void GetDeclaration_SoloTipo()
    {
        Assert.Equal("decimal(29,9)", ParameterBinder.GetDeclaration(Bind("d", null, "12345678901234567890.123456789")));
        Assert.Equal("nvarchar(4000)", ParameterBinder.GetDeclaration(Bind("s", null, "\"x\"")));
        Assert.Equal("varbinary(max)", ParameterBinder.GetDeclaration(Bind("b", "varbinary(max)", "null")));
        Assert.Equal("datetime2(3)", ParameterBinder.GetDeclaration(Bind("d", "datetime2(3)", "null")));
        Assert.Equal("time(0)", ParameterBinder.GetDeclaration(Bind("t", "time(0)", "null")));
        Assert.Equal("bigint", ParameterBinder.GetDeclaration(Bind("i", null, "1")));
    }

    [Fact]
    public void BindAll_LimitaCantidadYDuplicados()
    {
        var many = Enumerable.Range(0, ParameterBinder.MaxParameters + 1).Select(i => new QueryParameter("p" + i, null, Json("1"))).ToList();
        AssertInvalid(() => QueryParameterBinding.BindAll(_binder, many), "100");

        var exact = many.Take(ParameterBinder.MaxParameters).ToList();
        Assert.Equal(ParameterBinder.MaxParameters, QueryParameterBinding.BindAll(_binder, exact).Length);

        AssertInvalid(() => QueryParameterBinding.BindAll(_binder, [new QueryParameter("a", null, Json("1")), new QueryParameter("@A", null, Json("2"))]), "repetido");
        Assert.Empty(QueryParameterBinding.BindAll(_binder, []));
    }
}
