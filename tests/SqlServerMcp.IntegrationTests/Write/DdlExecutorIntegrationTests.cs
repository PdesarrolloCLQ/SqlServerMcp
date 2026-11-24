using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Audit;
using SqlServerMcp.Execution;
using SqlServerMcp.Policy;

namespace SqlServerMcp.IntegrationTests.Write;

/// <summary>RF-DDL-05..08, RS-AUD-05: DDL en el esquema de trabajo con registro de cambios real.</summary>
public class DdlExecutorIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CrearAlterarYEliminarTabla_RegistraCambios()
    {
        await using var h = WriteHarness.Create();
        var name = "D_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var create = await Execute(h, $"CREATE TABLE ia.{name} (Id int NOT NULL PRIMARY KEY, Nombre nvarchar(20) NULL)");
            Assert.True(create.Executed);
            Assert.True(create.Committed);
            Assert.NotNull(create.SchemaChangeId);
            Assert.False(create.Impact!.TargetExists);
            IntegrationEnvironment.ExecuteAdmin($"INSERT INTO ia.{name} (Id, Nombre) VALUES (1, N'a'), (2, N'b');");

            var alter = await Execute(h, $"ALTER TABLE ia.{name} ADD Saldo decimal(18,2) NULL");
            Assert.True(alter.Impact!.TargetExists);
            Assert.Equal("USER_TABLE", alter.Impact.TargetObjectType);
            Assert.Contains("\"name\":\"Nombre\"", alter.Impact.CurrentDefinition, StringComparison.Ordinal);

            var impact = await h.Ddl.AnalyzeImpactAsync(h.Profile, WriteSql.Evaluate($"DROP TABLE ia.{name}", ToolKind.Ddl), "impacto", Ct);
            Assert.True(impact.TargetExists);
            Assert.Equal(2, impact.ApproximateRows);
            Assert.Contains("\"name\":\"Saldo\"", impact.CurrentDefinition, StringComparison.Ordinal);

            var drop = await Execute(h, $"DROP TABLE ia.{name}");
            Assert.True(drop.Executed);
            Assert.Equal(0, ScratchTable.Scalar<int>($"SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'ia.{name}')"));

            var changes = h.ReadSchemaChanges();
            Assert.Equal(6, changes.Count);
            Assert.Equal(["pending", "applied", "pending", "applied", "pending", "applied"], changes.Select(c => c.GetProperty("status").GetString()));
            Assert.Equal(["CREATE", "CREATE", "ALTER", "ALTER", "DROP", "DROP"], changes.Select(c => c.GetProperty("operation").GetString()));
            Assert.False(changes[0].TryGetProperty("previousDefinition", out _));
            Assert.Contains("Nombre", changes[2].GetProperty("previousDefinition").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Saldo", changes[2].GetProperty("previousDefinition").GetString(), StringComparison.Ordinal);
            Assert.Contains("Saldo", changes[4].GetProperty("previousDefinition").GetString(), StringComparison.Ordinal);
            Assert.Equal(drop.SchemaChangeId, changes[5].GetProperty("changeId").GetString());
            Assert.Equal($"ia.{name}", changes[5].GetProperty("objectName").GetString());
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin($"DROP TABLE IF EXISTS ia.{name};");
        }
    }

    [Fact]
    public async Task CreateOrAlterVista_RespaldaDefinicionPreviaYDependientes()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(3);
        var view = "V_" + table.Name;
        var dependent = "VD_" + table.Name;
        try
        {
            await Execute(h, $"CREATE OR ALTER VIEW ia.{view} AS SELECT Id FROM {table.Qualified}");
            IntegrationEnvironment.ExecuteAdmin($"CREATE VIEW ia.{dependent} AS SELECT Id FROM ia.{view};");

            var second = await Execute(h, $"CREATE OR ALTER VIEW ia.{view} AS SELECT Id, Valor FROM {table.Qualified}");
            Assert.Contains($"ia.{dependent}", second.Impact!.Dependents);
            Assert.Contains(second.Warnings, w => w.Contains(dependent, StringComparison.Ordinal));

            var changes = h.ReadSchemaChanges();
            var backup = changes.Last(c => c.GetProperty("status").GetString() == "pending");
            Assert.Equal("CREATE OR ALTER", backup.GetProperty("operation").GetString());
            Assert.Contains("SELECT Id FROM", backup.GetProperty("previousDefinition").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin($"DROP VIEW IF EXISTS ia.{dependent}; DROP VIEW IF EXISTS ia.{view};");
        }
    }

    [Fact]
    public async Task ErrorDeEjecucion_RegistraFailedYNoAplica()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(3);

        await Assert.ThrowsAsync<SqlException>(() => Execute(h, $"ALTER TABLE {table.Qualified} ADD CONSTRAINT CK_{table.Name} CHECK (Valor > 100)"));

        var changes = h.ReadSchemaChanges();
        Assert.Equal(["pending", "failed"], changes.Select(c => c.GetProperty("status").GetString()));
        Assert.Equal(0, table.Value<int>($"SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_{table.Name}'"));
    }

    [Fact]
    public async Task RegistroNoDisponible_NoEjecuta()
    {
        await using var h = WriteHarness.Create();
        var ddl = new DdlExecutor(h.Factory, h.Catalog, new FailingSchemaChangeLog(), h.Transactions, NullLogger<DdlExecutor>.Instance);
        var name = "F_" + Guid.NewGuid().ToString("N")[..12];
        var sql = $"CREATE TABLE ia.{name} (Id int NOT NULL)";
        try
        {
            await Assert.ThrowsAsync<AuditWriteException>(() => ddl.ExecuteAsync(h.Profile, WriteSql.Evaluate(sql, ToolKind.Ddl), sql, null, "fail-closed", Ct));
            Assert.Equal(0, ScratchTable.Scalar<int>($"SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'ia.{name}')"));
        }
        finally
        {
            IntegrationEnvironment.ExecuteAdmin($"DROP TABLE IF EXISTS ia.{name};");
        }
    }

    [Fact]
    public async Task TruncateDentroDeTransaccionExplicita_SeRevierteConElla()
    {
        await using var h = WriteHarness.Create();
        using var table = ScratchTable.WithNumbers(25);

        await h.Transactions.BeginAsync(h.Profile, "tx-ddl", Ct);
        var impact = await h.Ddl.AnalyzeImpactAsync(h.Profile, WriteSql.Evaluate($"TRUNCATE TABLE {table.Qualified}", ToolKind.Ddl), "imp", Ct);
        Assert.Equal(25, impact.ApproximateRows);

        var result = await Execute(h, $"TRUNCATE TABLE {table.Qualified}");
        Assert.True(result.Executed);
        Assert.False(result.Committed);
        Assert.Contains(result.Warnings, w => w.Contains("transacción explícita", StringComparison.Ordinal));

        await h.Transactions.RollbackAsync(h.Profile, "prueba", Ct);
        Assert.Equal(25, table.Value<int>("SELECT COUNT(*) FROM {T}"));
    }

    [Fact]
    public async Task TextoCompletoDentroDeTransaccionExplicita_EsTransactionError()
    {
        await using var h = WriteHarness.Create();
        await h.Transactions.BeginAsync(h.Profile, "tx-ft", Ct);
        var ex = await Assert.ThrowsAsync<SqlServerMcp.Infrastructure.ToolFailureException>(() => Execute(h, "CREATE FULLTEXT CATALOG ftc_prueba"));
        Assert.Equal(SqlServerMcp.Infrastructure.ErrorCodes.TransactionError, ex.Code);
        await h.Transactions.RollbackAsync(h.Profile, "prueba", Ct);
    }

    private static Task<DdlExecutionResult> Execute(WriteHarness h, string sql) =>
        h.Ddl.ExecuteAsync(h.Profile, WriteSql.Evaluate(sql, ToolKind.Ddl), sql, null, "ddl-" + Guid.NewGuid().ToString("N")[..8], Ct);
}
