using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests.Execution.Read;

public class SqlErrorMapperTests
{
    [Fact]
    public void Map_ErrorSql_IncluyeNumeroSeveridadEstadoLineaYMensaje()
    {
        var failure = SqlErrorMapper.Map([new SqlErrorInfo(8134, 16, 1, 3, null, "Divide by zero error encountered.")]);

        Assert.Equal(ErrorCodes.SqlError, failure.Code);
        Assert.Contains("8134", failure.Message, StringComparison.Ordinal);
        Assert.Contains("línea 3", failure.Message, StringComparison.Ordinal);
        Assert.Equal(8134, failure.Details["number"]);
        Assert.Equal(16, failure.Details["severity"]);
        Assert.Equal(1, failure.Details["state"]);
        Assert.Equal(3, failure.Details["line"]);
        Assert.Null(failure.Details["procedure"]);
        Assert.Equal("Divide by zero error encountered.", failure.Details["message"]);
        Assert.Contains("NULLIF", failure.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_Timeout()
    {
        var failure = SqlErrorMapper.Map([new SqlErrorInfo(-2, 11, 0, 0, null, "Execution Timeout Expired.")], 30);
        Assert.Equal(ErrorCodes.Timeout, failure.Code);
        Assert.Contains("30 s", failure.Message, StringComparison.Ordinal);
        Assert.Equal(-2, failure.Details["number"]);
    }

    [Theory]
    [InlineData(18456)]
    [InlineData(4060)]
    [InlineData(53)]
    [InlineData(-1)]
    [InlineData(10054)]
    public void Map_ErroresDeConexion(int number)
    {
        var failure = SqlErrorMapper.Map([new SqlErrorInfo(number, 14, 1, 0, null, "Login failed for user 'mcp_lector'. Password=abc")]);
        Assert.Equal(ErrorCodes.ConnectionError, failure.Code);
        Assert.DoesNotContain("abc", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", (string)failure.Details["message"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_LoginNoRevelaElUsuario()
    {
        var failure = SqlErrorMapper.Map([new SqlErrorInfo(18456, 14, 1, 0, null, "Login failed for user 'mcp_lector'.")]);
        Assert.DoesNotContain("mcp_lector", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_lector", (string)failure.Details["message"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_SeveridadFatalEsErrorDeConexion() =>
        Assert.Equal(ErrorCodes.ConnectionError, SqlErrorMapper.Map([new SqlErrorInfo(0, 20, 0, 0, null, "A transport-level error has occurred.")]).Code);

    [Fact]
    public void Map_VariosErrores_UsaElPrincipalYListaTodos()
    {
        var failure = SqlErrorMapper.Map(
        [
            new SqlErrorInfo(5701, 0, 2, 1, null, "Changed database context."),
            new SqlErrorInfo(208, 16, 1, 1, "usp_x", "Invalid object name 'ventas.NoExiste'."),
        ]);

        Assert.Equal(ErrorCodes.SqlError, failure.Code);
        Assert.Equal(208, failure.Details["number"]);
        Assert.Equal("usp_x", failure.Details["procedure"]);
        var errors = Assert.IsType<List<Dictionary<string, object?>>>(failure.Details["errors"]);
        Assert.Equal(2, errors.Count);
        Assert.Contains("list_tables", failure.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_PermisoShowplanSugiereAlDba()
    {
        var failure = SqlErrorMapper.Map([new SqlErrorInfo(262, 14, 1, 1, null, "SHOWPLAN permission denied in database 'VentasTest'.")]);
        Assert.Equal(ErrorCodes.SqlError, failure.Code);
        Assert.Contains("GRANT SHOWPLAN", failure.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=x;Password=Secreta123;User ID=sa", "Secreta123")]
    [InlineData("pwd = 'abc def'; otro", "abc def")]
    [InlineData("PWD=\"x;y\"", "x;y")]
    [InlineData("password={a;b}", "a;b")]
    [InlineData("No se pudo abrir C:\\Datos\\secreto.mdf", "C:\\Datos")]
    [InlineData("Ruta \\\\servidor\\recurso\\archivo.bak", "servidor")]
    public void Sanitize_OcultaSecretosYRutas(string input, string forbidden)
    {
        var sanitized = SqlErrorMapper.Sanitize(input);
        Assert.DoesNotContain(forbidden, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ConservaMensajesNormalesYAcota()
    {
        Assert.Equal("Invalid column name 'x'.", SqlErrorMapper.Sanitize("Invalid column name 'x'."));
        Assert.Equal(string.Empty, SqlErrorMapper.Sanitize(null));
        Assert.True(SqlErrorMapper.Sanitize(new string('a', 5000)).Length <= 2001);
    }
}
