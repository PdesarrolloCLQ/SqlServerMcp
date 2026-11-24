using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Metadata;

public class RoutineParameterDefaultsTests
{
    [Fact]
    public void Extract_ProcedimientoSinParentesis()
    {
        const string sql = """
            -- comentario inicial
            CREATE   PROC [ventas].[p x] @a int = 5, @b nvarchar(10) = N'x,y' OUTPUT,
                @c decimal(18,2)=-1.5 /*c*/ ,@d int OUT, @e datetime2(3) = NULL
            AS SELECT @a = 1
            """;
        var defaults = RoutineParameterDefaults.Extract(sql);

        Assert.Equal("5", defaults["@a"]);
        Assert.Equal("N'x,y'", defaults["@B"]);
        Assert.Equal("-1.5", defaults["@c"]);
        Assert.False(defaults.ContainsKey("@d"));
        Assert.Equal("NULL", defaults["@e"]);
    }

    [Fact]
    public void Extract_FuncionConParentesisYReadOnly()
    {
        const string sql = "CREATE FUNCTION dbo.f(@x int = 3, @t dbo.TT READONLY, @s varchar(5) = 'a)b') RETURNS int AS BEGIN DECLARE @z int = 9; RETURN 1 END";
        var defaults = RoutineParameterDefaults.Extract(sql);

        Assert.Equal("3", defaults["@x"]);
        Assert.False(defaults.ContainsKey("@t"));
        Assert.Equal("'a)b'", defaults["@s"]);
        Assert.False(defaults.ContainsKey("@z"));
    }

    [Fact]
    public void Extract_CreateOrAlterProcedureConParentesis()
    {
        const string sql = "CREATE OR ALTER PROCEDURE ia.p (@a int = 1, @b int) WITH RECOMPILE AS SELECT 1";
        var defaults = RoutineParameterDefaults.Extract(sql);
        Assert.Single(defaults);
        Assert.Equal("1", defaults["@a"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CREATE VIEW v AS SELECT 1 AS a")]
    [InlineData("CREATE PROCEDURE p AS SELECT 1")]
    [InlineData("SELECT 'sin cerrar")]
    public void Extract_SinParametrosONoReconocible_DevuelveVacio(string? sql) =>
        Assert.Empty(RoutineParameterDefaults.Extract(sql));

    [Fact]
    public void Extract_DefinicionGrande_NoFalla()
    {
        var body = string.Concat(Enumerable.Repeat("SELECT ((((1)))) + 1;\n", 20_000));
        var defaults = RoutineParameterDefaults.Extract("CREATE PROCEDURE p @a int = 7 AS BEGIN " + body + " END");
        Assert.Equal("7", defaults["@a"]);
    }
}
