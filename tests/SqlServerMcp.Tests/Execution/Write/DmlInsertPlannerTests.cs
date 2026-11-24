using System.Text.Json;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-07, RF-DML-08: validación de columnas contra el catálogo y SQL parametrizado.</summary>
public class DmlInsertPlannerTests
{
    private static readonly CatalogObject Table = new(42, "ventas", "Clientes", "U", "USER_TABLE");

    private static readonly IReadOnlyList<CatalogColumn> Columns =
    [
        new(1, "ClienteId", new CatalogType("int", 4, 10, 0), false, IsIdentity: true, false, false, false, false),
        new(2, "Nombre", new CatalogType("nvarchar", 200, 0, 0), false, false, false, false, false, false),
        new(3, "Email", new CatalogType("nvarchar", 400, 0, 0), true, false, false, false, false, false),
        new(4, "Saldo", new CatalogType("decimal", 9, 18, 4), false, false, false, HasDefault: true, false, false),
        new(5, "Total", new CatalogType("decimal", 9, 18, 2), true, false, IsComputed: true, false, false, false),
        new(6, "Version", new CatalogType("timestamp", 8, 0, 0), false, false, false, false, false, IsRowVersion: true),
        new(7, "Contrasena", new CatalogType("nvarchar", 200, 0, 0), true, false, false, false, false, false),
    ];

    [Fact]
    public void AgrupaPorConjuntoDeColumnas_SinDistinguirMayusculas()
    {
        var plan = DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns,
        [
            Row(("Nombre", "\"a\""), ("Saldo", "1.5")),
            Row(("nombre", "\"b\"")),
            Row(("SALDO", "2"), ("[Nombre]", "\"c\"")),
            Row(("Nombre", "\"d\""), ("Email", "null")),
        ]);

        Assert.Equal(3, plan.Groups.Count);
        Assert.Equal(["Nombre", "Saldo"], plan.Groups[0].Columns.Select(c => c.Name));
        Assert.Equal([1, 3], plan.Groups[0].RowNumbers);
        Assert.Equal("c", plan.Groups[0].Rows[1][0].GetString());
        Assert.Equal("2", plan.Groups[0].Rows[1][1].GetRawText());
        Assert.Equal(["Nombre"], plan.Groups[1].Columns.Select(c => c.Name));
        Assert.Equal(["Nombre", "Email"], plan.Groups[2].Columns.Select(c => c.Name));
    }

    [Fact]
    public void ColumnaDesconocida_EsInvalidArgumentConSuNombre()
    {
        var ex = Assert.Throws<ToolFailureException>(() => DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row(("Nombre", "\"a\""), ("NoExiste", "1"))]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("NoExiste", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ClienteId", "IDENTITY")]
    [InlineData("Total", "calculada")]
    [InlineData("Version", "rowversion")]
    public void ColumnasGeneradas_SeRechazan(string column, string reason)
    {
        var ex = Assert.Throws<ToolFailureException>(() => DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row(("Nombre", "\"a\""), (column, "1"))]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnaExcluida_NuncaEsResoluble()
    {
        var ex = Assert.Throws<ToolFailureException>(() => DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row(("Nombre", "\"a\""), ("Contrasena", "\"x\""))]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void ColumnaRepetida_EsInvalidArgument()
    {
        var ex = Assert.Throws<ToolFailureException>(() => DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row(("Nombre", "\"a\""), ("NOMBRE", "\"b\""))]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void FilaVacia_EsInvalidArgument()
    {
        var ex = Assert.Throws<ToolFailureException>(() => DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row()]));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void OutputColumns_ExcluyeColumnasExcluidas()
    {
        var plan = DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row(("Nombre", "\"a\""))]);
        Assert.DoesNotContain(plan.OutputColumns, c => c.Name == "Contrasena");
        Assert.Equal(6, plan.OutputColumns.Count);
    }

    [Fact]
    public void BuildInsertSql_UsaQuoteYParametros()
    {
        var table = new CatalogObject(1, "ia", "Ta]bla", "U", "USER_TABLE");
        var columns = new List<CatalogColumn>
        {
            new(1, "A b", new CatalogType("int", 4, 10, 0), false, false, false, false, false, false),
            new(2, "C]d", new CatalogType("int", 4, 10, 0), false, false, false, false, false, false),
        };
        var plan = DmlInsertPlanner.Plan(WriteTestSql.Profile(c => c.AllowedSchemas = ["ia"]), table, columns, [Row(("A b", "1"), ("C]d", "2"))]);

        var sql = DmlInsertPlanner.BuildInsertSql(plan, plan.Groups[0].Columns, 2, plan.OutputColumns, "rc");
        Assert.Equal(
            "INSERT INTO [ia].[Ta]]bla] ([A b], [C]]d])\nOUTPUT INSERTED.[A b], INSERTED.[C]]d]\nVALUES (@p0_0, @p0_1), (@p1_0, @p1_1);\nSELECT CAST(@@ROWCOUNT AS bigint) AS [rc];",
            sql);
        Assert.DoesNotContain("OUTPUT", DmlInsertPlanner.BuildInsertSql(plan, plan.Groups[0].Columns, 1, null, "rc"), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInsertSql_RechazaLotesDemasiadoGrandes()
    {
        var plan = DmlInsertPlanner.Plan(WriteTestSql.Profile(), Table, Columns, [Row(("Nombre", "\"a\""), ("Saldo", "1"), ("Email", "null"))]);
        Assert.Throws<ArgumentOutOfRangeException>(() => DmlInsertPlanner.BuildInsertSql(plan, plan.Groups[0].Columns, 667, null, "rc"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DmlInsertPlanner.BuildInsertSql(plan, plan.Groups[0].Columns.Take(1).ToList(), 1001, null, "rc"));
    }

    private static Dictionary<string, JsonElement> Row(params (string Column, string Json)[] values) =>
        values.ToDictionary(v => v.Column, v => JsonDocument.Parse(v.Json).RootElement.Clone());
}
