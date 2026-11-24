using System.Data;
using System.Data.SqlTypes;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-07: DataTable tipada para SqlBulkCopy a partir de los tipos del catálogo.</summary>
public class DmlBulkCopyTableBuilderTests
{
    [Theory]
    [InlineData("int", typeof(int))]
    [InlineData("bigint", typeof(long))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("nvarchar", typeof(string))]
    [InlineData("datetime2", typeof(DateTime))]
    [InlineData("datetimeoffset", typeof(DateTimeOffset))]
    [InlineData("uniqueidentifier", typeof(Guid))]
    [InlineData("varbinary", typeof(byte[]))]
    [InlineData("bit", typeof(bool))]
    [InlineData("sql_variant", typeof(object))]
    public void MapClrType(string sqlType, Type expected) =>
        Assert.Equal(expected, DmlBulkCopyTableBuilder.MapClrType(new CatalogType(sqlType, 0, 0, 0)));

    [Fact]
    public void AliasUsaTipoBase() =>
        Assert.Equal(typeof(string), DmlBulkCopyTableBuilder.MapClrType(new CatalogType("Telefono", 40, 0, 0, IsUserDefined: true, BaseTypeName: "varchar")));

    [Fact]
    public void Build_ConvierteConElEnlazador()
    {
        var columns = new List<CatalogColumn>
        {
            new(1, "Id", new CatalogType("int", 4, 10, 0), false, false, false, false, false, false),
            new(2, "Monto", new CatalogType("decimal", 9, 18, 2), true, false, false, false, false, false),
        };
        var rows = new List<JsonElement[]>
        {
            new[] { Json("1"), Json("\"10.25\"") },
            new[] { Json("2"), Json("null") },
        };

        using var table = DmlBulkCopyTableBuilder.Build(columns, rows, [1, 2], new SimpleBinder());
        Assert.Equal(typeof(int), table.Columns[0].DataType);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(10.25m, table.Rows[0][1]);
        Assert.Equal(DBNull.Value, table.Rows[1][1]);
    }

    [Fact]
    public void ConvertValue_DesenvuelveSqlTypesYRechazaIncompatibles()
    {
        Assert.Equal(5, DmlBulkCopyTableBuilder.ConvertValue(new SqlInt64(5), typeof(int), "c", 1));
        Assert.Equal(DBNull.Value, DmlBulkCopyTableBuilder.ConvertValue(SqlString.Null, typeof(string), "c", 1));
        var guid = Guid.NewGuid();
        Assert.Equal(guid, DmlBulkCopyTableBuilder.ConvertValue(guid.ToString(), typeof(Guid), "c", 1));
        var ex = Assert.Throws<ToolFailureException>(() => DmlBulkCopyTableBuilder.ConvertValue("abc", typeof(int), "Cantidad", 3));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("Cantidad", ex.Message, StringComparison.Ordinal);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Enlazador mínimo: números como decimal o int según el tipo, textos como string.</summary>
    private sealed class SimpleBinder : IParameterBinder
    {
        public SqlParameter Bind(QueryParameter parameter) => throw new NotSupportedException();

        public SqlParameter BindForCatalogType(string parameterName, CatalogType type, JsonElement value)
        {
            object v = value.ValueKind switch
            {
                JsonValueKind.Null => DBNull.Value,
                JsonValueKind.String when type.TypeName == "decimal" => decimal.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.Number when type.TypeName == "int" => value.GetInt32(),
                _ => value.ToString(),
            };
            return new SqlParameter(parameterName, SqlDbType.Variant) { Value = v };
        }
    }
}
