using System.Text.Json;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.IntegrationTests.Write;

/// <summary>RF-DML-07, RF-DML-08: insert_rows con validación de catálogo, parámetros y SqlBulkCopy.</summary>
public class InsertRowsIntegrationTests
{
    private const string Columns =
        "Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, Nombre nvarchar(50) NOT NULL, Saldo decimal(18,2) NOT NULL CONSTRAINT DF_{0} DEFAULT (5), " +
        "Alta datetime2(3) NULL, Doble AS (Saldo * 2), Version rowversion, Contrasena nvarchar(50) NULL, CONSTRAINT CK_{0} CHECK (Saldo >= 0)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConjuntosDeColumnasDistintos_EnUnaTransaccion()
    {
        await using var h = WriteHarness.Create();
        using var table = CreateTable();

        var result = await h.Dml.InsertRowsAsync(Request(h, table,
            Row(("Nombre", "\"Ana\""), ("Saldo", "10.50")),
            Row(("nombre", "\"Luis\"")),
            Row(("Nombre", "\"Eva\""), ("Alta", "\"2026-11-01T10:00:00\""), ("Saldo", "\"1.25\""))), Ct);

        Assert.Equal(3, result.RowsAffected);
        Assert.True(result.Committed);
        Assert.Equal(3, table.Value<int>("SELECT COUNT(*) FROM {T}"));
        Assert.Equal(5m, table.Value<decimal>("SELECT Saldo FROM {T} WHERE Nombre = N'Luis'"));
        Assert.Equal(1.25m, table.Value<decimal>("SELECT Saldo FROM {T} WHERE Nombre = N'Eva'"));
    }

    [Fact]
    public async Task ReturnInserted_DevuelveFilasSinColumnasExcluidas()
    {
        await using var h = WriteHarness.Create();
        using var table = CreateTable();

        var result = await h.Dml.InsertRowsAsync(Request(h, table, returnInserted: true,
            Row(("Nombre", "\"Ana\"")),
            Row(("Nombre", "\"Luis\""), ("Saldo", "3"))), Ct);

        Assert.NotNull(result.Output);
        Assert.Equal(2, result.Output.Rows.Count);
        Assert.Contains(result.Output.Columns, c => c.Name == "Id");
        Assert.DoesNotContain(result.Output.Columns, c => c.Name == "Contrasena");
    }

    [Fact]
    public async Task Identity_SeRechazaSinInsertarNada()
    {
        await using var h = WriteHarness.Create();
        using var table = CreateTable();

        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => h.Dml.InsertRowsAsync(Request(h, table,
            Row(("Nombre", "\"Ana\"")),
            Row(("Id", "99"), ("Nombre", "\"Luis\""))), Ct));

        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("IDENTITY", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Theory]
    [InlineData("NoExiste")]
    [InlineData("Contrasena")]
    [InlineData("Doble")]
    [InlineData("Version")]
    public async Task ColumnasNoPermitidas_SonInvalidArgument(string column)
    {
        await using var h = WriteHarness.Create();
        using var table = CreateTable();

        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => h.Dml.InsertRowsAsync(Request(h, table, Row(("Nombre", "\"Ana\""), (column, "\"x\""))), Ct));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains(column, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quinientas_ConSqlBulkCopy()
    {
        await using var h = WriteHarness.Create();
        using var table = CreateTable();
        var rows = Enumerable.Range(1, 500)
            .Select(i => Row(("Nombre", JsonSerializer.Serialize("Cliente " + i)), ("Saldo", (i / 4m).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Alta", "null")))
            .ToArray();

        var result = await h.Dml.InsertRowsAsync(Request(h, table, rows), Ct);

        Assert.Equal(500, result.RowsAffected);
        Assert.True(result.Committed);
        Assert.Equal(500, table.Value<int>("SELECT COUNT(*) FROM {T}"));
        Assert.Equal(125m, table.Value<decimal>("SELECT Saldo FROM {T} WHERE Nombre = N'Cliente 500'"));
        // KeepNulls: el null explícito se conserva igual que en la ruta parametrizada.
        Assert.Equal(500, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Alta IS NULL"));
    }

    [Fact]
    public async Task BulkCopyConViolacionDeRestriccion_NoInsertaNada()
    {
        await using var h = WriteHarness.Create();
        using var table = CreateTable();
        var rows = Enumerable.Range(1, 300)
            .Select(i => Row(("Nombre", JsonSerializer.Serialize("C" + i)), ("Saldo", i == 300 ? "-1" : "1")))
            .ToArray();

        await Assert.ThrowsAnyAsync<Exception>(() => h.Dml.InsertRowsAsync(Request(h, table, rows), Ct));
        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task MasFilasQueElMaximo_EsLimitExceeded()
    {
        await using var h = WriteHarness.Create(c => c.Limits.MaxInsertRows = 5);
        using var table = CreateTable();
        var rows = Enumerable.Range(1, 6).Select(i => Row(("Nombre", "\"x\""))).ToArray();

        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => h.Dml.InsertRowsAsync(Request(h, table, rows), Ct));
        Assert.Equal(ErrorCodes.LimitExceeded, ex.Code);
    }

    [Fact]
    public async Task TablaExcluidaOVista_NoSeAdmiten()
    {
        await using var h = WriteHarness.Create();
        var excluded = await Assert.ThrowsAsync<ToolFailureException>(() => h.Dml.InsertRowsAsync(new InsertRowsRequest
        {
            Profile = h.Profile,
            Table = new SqlObjectName(null, null, "ventas", "TarjetasCredito"),
            Rows = [Row(("TarjetaId", "9"), ("ClienteId", "1"), ("Numero", "\"1\""))],
            CorrelationId = "t",
        }, Ct));
        Assert.Equal(ErrorCodes.NotFound, excluded.Code);

        var view = await Assert.ThrowsAsync<ToolFailureException>(() => h.Dml.InsertRowsAsync(new InsertRowsRequest
        {
            Profile = h.Profile,
            Table = new SqlObjectName(null, null, "ventas", "vClientesActivos"),
            Rows = [Row(("Nombre", "\"x\""))],
            CorrelationId = "t",
        }, Ct));
        Assert.Equal(ErrorCodes.InvalidArgument, view.Code);
    }

    private static ScratchTable CreateTable()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ScratchTable.Create(string.Format(System.Globalization.CultureInfo.InvariantCulture, Columns, suffix));
    }

    private static InsertRowsRequest Request(WriteHarness h, ScratchTable table, params IReadOnlyDictionary<string, JsonElement>[] rows) =>
        Request(h, table, false, rows);

    private static InsertRowsRequest Request(WriteHarness h, ScratchTable table, bool returnInserted, params IReadOnlyDictionary<string, JsonElement>[] rows) => new()
    {
        Profile = h.Profile,
        Table = new SqlObjectName(null, null, "ia", table.Name),
        Rows = rows,
        CorrelationId = "insert-" + Guid.NewGuid().ToString("N")[..8],
        ReturnInserted = returnInserted,
    };

    private static Dictionary<string, JsonElement> Row(params (string Column, string Json)[] values) =>
        values.ToDictionary(v => v.Column, v => JsonDocument.Parse(v.Json).RootElement.Clone());
}
