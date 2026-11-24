using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.IntegrationTests.Write;

/// <summary>RF-DML-03/04/06/09, RS-HIL-07, CA-12, CA-13, CA-21 contra VentasTest con el login escritor de mínimo privilegio.</summary>
public class DmlExecutorIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CA13_Estimacion_UpdateDe250FilasConUmbral100()
    {
        await using var h = WriteHarness.Create(c => c.Limits.ConfirmAboveRows = 100);
        using var table = ScratchTable.WithNumbers(300);

        var request = h.DmlRequest($"UPDATE {table.Qualified} SET Valor = 1 WHERE Id <= @hasta", parameters: Param("hasta", 250));
        var estimate = await h.Dml.EstimateAffectedRowsAsync(request, Ct);

        Assert.Equal(250, estimate);
        Assert.True(estimate > h.Profile.Limits.ConfirmAboveRows);
        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
    }

    [Fact]
    public async Task Estimacion_DeleteConJoin_EjecucionDePruebaRevertida()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(300);
        using var other = ScratchTable.WithNumbers(40);

        var request = h.DmlRequest($"DELETE t FROM {table.Qualified} t JOIN {other.Qualified} o ON o.Id = t.Id WHERE o.Valor = 0");
        Assert.Equal(40, await h.Dml.EstimateAffectedRowsAsync(request, Ct));
        Assert.Equal(300, table.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task Estimacion_TopPercentEInsertSelectYValues()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(200);
        using var target = ScratchTable.Create("Id int NOT NULL, Valor int NOT NULL");

        Assert.Equal(20, await h.Dml.EstimateAffectedRowsAsync(h.DmlRequest($"UPDATE TOP (10) PERCENT {table.Qualified} SET Valor = 9 WHERE Id > 0"), Ct));
        Assert.Equal(150, await h.Dml.EstimateAffectedRowsAsync(h.DmlRequest($"INSERT INTO {target.Qualified} (Id, Valor) SELECT Id, Valor FROM {table.Qualified} WHERE Id > 50"), Ct));
        Assert.Equal(3, await h.Dml.EstimateAffectedRowsAsync(h.DmlRequest($"INSERT INTO {target.Qualified} (Id, Valor) VALUES (1, 1), (2, 2), (3, 3)"), Ct));
        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
        Assert.Equal(0, target.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task CA12_Update5000FilasConMaximo1000_SeRevierte()
    {
        await using var h = WriteHarness.Create(c => c.Limits.MaxAffectedRows = 1000);
        using var table = ScratchTable.WithNumbers(5000);

        var result = await h.Dml.ExecuteAsync(h.DmlRequest($"UPDATE {table.Qualified} SET Valor = 7 WHERE Id > 0"), Ct);

        Assert.True(result.RolledBack);
        Assert.False(result.Committed);
        Assert.Equal(ErrorCodes.LimitExceeded, result.RollbackReason);
        Assert.Equal(5000, result.RowsAffected);
        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
    }

    [Fact]
    public async Task RSHIL07_Aprobadas250Reales400_SeRevierte()
    {
        await using var h = WriteHarness.Create(c => c.Limits.AffectedRowsMarginPercent = 10);
        using var table = ScratchTable.WithNumbers(500);

        var result = await h.Dml.ExecuteAsync(h.DmlRequest($"UPDATE {table.Qualified} SET Valor = 1 WHERE Id <= 400", approvedRows: 250), Ct);

        Assert.True(result.RolledBack);
        Assert.Equal(ErrorCodes.LimitExceeded, result.RollbackReason);
        Assert.Equal(400, result.RowsAffected);
        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
    }

    [Fact]
    public async Task RSHIL07_DentroDelMargen_SeConfirma()
    {
        await using var h = WriteHarness.Create(c => c.Limits.AffectedRowsMarginPercent = 10);
        using var table = ScratchTable.WithNumbers(500);

        var result = await h.Dml.ExecuteAsync(h.DmlRequest($"UPDATE {table.Qualified} SET Valor = 1 WHERE Id <= 270", approvedRows: 250), Ct);

        Assert.True(result.Committed);
        Assert.False(result.RolledBack);
        Assert.Equal(270, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor = 1"));
    }

    [Fact]
    public async Task VistaPrevia_NuncaPersisteYDevuelveMuestra()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(20);

        var result = await h.Dml.ExecuteAsync(
            h.DmlRequest($"DELETE FROM {table.Qualified} OUTPUT deleted.Id, deleted.Valor WHERE Id <= 5", preview: true),
            Ct);

        Assert.True(result.RolledBack);
        Assert.Equal("preview", result.RollbackReason);
        Assert.Equal(5, result.RowsAffected);
        Assert.NotNull(result.Output);
        Assert.Equal(5, result.Output.Rows.Count);
        Assert.Equal(20, table.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task Output_DevuelveFilasLimitadasYCuentaExacta()
    {
        await using var h = WriteHarness.Create(c => c.Limits.MaxRows = 10);
        using var table = ScratchTable.WithNumbers(30);

        var result = await h.Dml.ExecuteAsync(
            h.DmlRequest($"UPDATE {table.Qualified} SET Valor = Valor + 1 OUTPUT inserted.Id, deleted.Valor AS Anterior, inserted.Valor AS Nuevo WHERE Id <= 25"),
            Ct);

        Assert.True(result.Committed);
        Assert.Equal(25, result.RowsAffected);
        Assert.NotNull(result.Output);
        Assert.Equal(["Id", "Anterior", "Nuevo"], result.Output.Columns.Select(c => c.Name));
        Assert.Equal(10, result.Output.Rows.Count);
        Assert.True(result.Output.Truncated);
        Assert.Equal(25, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor = 1"));
    }

    [Fact]
    public async Task ConteoNoSeFalsificaConOutputYNoCuentaDesencadenadores()
    {
        await using var h = WriteHarness.Create(c => c.Limits.MaxAffectedRows = 10);
        using var plain = ScratchTable.WithNumbers(50);
        using var table = ScratchTable.WithNumbers(50);
        using var log = ScratchTable.Create("Id int NOT NULL");

        // El agente intenta imitar el conjunto de conteo con una columna de OUTPUT: no debe evitar el límite.
        var spoof = await h.Dml.ExecuteAsync(
            h.DmlRequest($"UPDATE {plain.Qualified} SET Valor = 1 OUTPUT CAST(0 AS bigint) AS mcp_rowcount WHERE Id <= 40"),
            Ct);
        Assert.Equal(40, spoof.RowsAffected);
        Assert.Equal(ErrorCodes.LimitExceeded, spoof.RollbackReason);
        Assert.Equal(0, plain.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));

        IntegrationEnvironment.ExecuteAdmin(
            $"CREATE TRIGGER ia.TR_{table.Name} ON {table.Qualified} AFTER UPDATE AS BEGIN " +
            $"INSERT INTO {log.Qualified} (Id) SELECT Id FROM inserted; INSERT INTO {log.Qualified} (Id) SELECT Id FROM inserted; END");

        // Con desencadenador que inserta 2 filas por fila actualizada, las filas afectadas siguen siendo las de la sentencia.
        var result = await h.Dml.ExecuteAsync(h.DmlRequest($"UPDATE {table.Qualified} SET Valor = 2 WHERE Id <= 5"), Ct);
        Assert.Equal(5, result.RowsAffected);
        Assert.True(result.Committed);
        Assert.Equal(10, log.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task MergeConPuntoYComaFinalYEstimacion()
    {
        await using var h = WriteHarness.Create();
        using var target = ScratchTable.WithNumbers(10);
        using var source = ScratchTable.WithNumbers(15);

        var sql = $"MERGE {target.Qualified} AS t USING {source.Qualified} AS s ON t.Id = s.Id " +
                  "WHEN MATCHED THEN UPDATE SET t.Valor = 3 WHEN NOT MATCHED BY TARGET THEN INSERT (Id, Valor) VALUES (s.Id, 4);";
        Assert.Equal(15, await h.Dml.EstimateAffectedRowsAsync(h.DmlRequest(sql), Ct));
        Assert.Equal(10, target.Value<int>("SELECT COUNT(*) FROM {T}"));

        var result = await h.Dml.ExecuteAsync(h.DmlRequest(sql), Ct);
        Assert.Equal(15, result.RowsAffected);
        Assert.Equal(15, target.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task ErrorSql_SePropagaYNoDejaCambios()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(10);

        await Assert.ThrowsAsync<SqlException>(() => h.Dml.ExecuteAsync(h.DmlRequest($"UPDATE {table.Qualified} SET Id = 1 WHERE Id <= 2"), Ct));
        Assert.Equal(55, table.Value<int>("SELECT SUM(Id) FROM {T}"));
    }

    [Fact]
    public async Task CA21_TiempoAgotadoDuranteDml_NoDejaTransaccionNiBloqueos()
    {
        var app = "SqlServerMcp-timeout-" + Guid.NewGuid().ToString("N")[..8];
        await using var h = WriteHarness.Create(c =>
        {
            c.Connection.ApplicationName = app;
            c.Limits.CommandTimeoutSeconds = 2;
            c.Limits.LockTimeoutMs = 60_000;
        });
        using var table = ScratchTable.WithNumbers(10);

        // Otra sesión bloquea la fila 10: el UPDATE modifica 1..9 y queda esperando hasta agotar el tiempo.
        using (var blocker = IntegrationEnvironment.OpenAdminConnection())
        {
            using var blockerTransaction = blocker.BeginTransaction();
            using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.Transaction = blockerTransaction;
                lockCommand.CommandText = $"UPDATE {table.Qualified} SET Valor = 100 WHERE Id = 10;";
                lockCommand.ExecuteNonQuery();
            }

            var ex = await Assert.ThrowsAsync<ToolFailureException>(() => h.Dml.ExecuteAsync(h.DmlRequest($"UPDATE {table.Qualified} SET Valor = Valor + 1 WHERE Id >= 1"), Ct));
            Assert.Equal(ErrorCodes.Timeout, ex.Code);
            blockerTransaction.Rollback();
        }

        // Sin bloqueos residuales: otra sesión con LOCK_TIMEOUT corto modifica las filas 1..9 sin esperar.
        using (var probe = IntegrationEnvironment.OpenAdminConnection())
        using (var command = probe.CreateCommand())
        {
            command.CommandText = $"SET LOCK_TIMEOUT 2000; BEGIN TRAN; UPDATE {table.Qualified} SET Valor = Valor WHERE Id <= 9; ROLLBACK;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, table.Value<int>("SELECT COUNT(*) FROM {T} WHERE Valor <> 0"));
        Assert.Equal(0, ScratchTable.Scalar<int>(
            $"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = N'{app}' AND open_transaction_count > 0"));
    }

    private static QueryParameter Param(string name, object value) =>
        new(name, null, JsonSerializer.SerializeToElement(value));
}
