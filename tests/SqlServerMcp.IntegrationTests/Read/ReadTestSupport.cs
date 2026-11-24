using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Policy;
using SqlServerMcp.Security;

namespace SqlServerMcp.IntegrationTests.Read;

/// <summary>Las pruebas de lectura se ejecutan en serie: miden memoria, tiempos y estado del servidor por aplicación.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReadIntegrationCollection
{
    public const string Name = "lectura-integracion";
}

/// <summary>Utilidades compartidas por las pruebas de integración del módulo de lectura.</summary>
internal static class ReadTestSupport
{
    public static SqlConnectionFactory CreateFactory() => new(new SecretResolver(), NullLogger<SqlConnectionFactory>.Instance);

    /// <summary>Nombre de aplicación único para aislar el pool y consultar el estado de las sesiones en las DMV.</summary>
    public static string NewApplicationName(string prefix) => $"SqlServerMcp-{prefix}-{Guid.NewGuid():N}";

    public static ProfileRuntime Profile(string applicationName, Action<ProfileConfig>? customize = null) =>
        IntegrationEnvironment.CreateProfile(
            OperationMode.Lectura,
            config =>
            {
                config.Connection.ApplicationName = applicationName;
                customize?.Invoke(config);
            },
            name: "lectura-" + Guid.NewGuid().ToString("N")[..12]);

    public static PolicyEvaluation Allow(params int[] maskOrdinals) => new()
    {
        Statement = new SqlStatementInfo { Parsed = true, Category = StatementCategory.Query, Operation = "SELECT" },
        Tool = ToolKind.Query,
        Action = PolicyAction.Allow,
        RuleId = "PRUEBA",
        Reason = "Permitida en prueba.",
        MaskOrdinals = [.. maskOrdinals],
    };

    public static QueryExecutor Executor(ISqlConnectionFactory factory, ITransactionManager? transactions = null) =>
        new(factory, new ResultReader(), new ParameterBinder(), transactions ?? new NoTransactionManager());

    public static Task<QueryExecutionResult> RunAsync(
        IQueryExecutor executor,
        ProfileRuntime profile,
        string sql,
        int maxRows = 500,
        int skipRows = 0,
        IReadOnlyList<QueryParameter>? parameters = null,
        PolicyEvaluation? evaluation = null) =>
        executor.ExecuteQueryAsync(
            new QueryExecutionRequest
            {
                Profile = profile,
                Sql = sql,
                MaxRows = maxRows,
                SkipRows = skipRows,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Evaluation = evaluation ?? Allow(),
                Parameters = parameters ?? [],
            },
            TestContext.Current.CancellationToken);

    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Solicitudes en curso, transacciones abiertas y bloqueos (distintos de DATABASE) de las sesiones de la aplicación.</summary>
    public static (int Requests, int OpenTransactions, int Locks) ServerState(string applicationName)
    {
        using var connection = IntegrationEnvironment.OpenAdminConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.program_name = @app),
              (SELECT ISNULL(SUM(s.open_transaction_count), 0) FROM sys.dm_exec_sessions s WHERE s.program_name = @app),
              (SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id
                 WHERE s.program_name = @app AND l.resource_type <> N'DATABASE')
            """;
        command.Parameters.Add(new SqlParameter("@app", System.Data.SqlDbType.NVarChar, 128) { Value = applicationName });
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>Espera (hasta 10 s) a que el servidor termine cualquier solicitud de la aplicación.</summary>
    public static async Task<(int Requests, int OpenTransactions, int Locks)> WaitForIdleAsync(string applicationName)
    {
        var state = ServerState(applicationName);
        for (var i = 0; i < 50 && (state.Requests > 0 || state.OpenTransactions > 0 || state.Locks > 0); i++)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            state = ServerState(applicationName);
        }

        return state;
    }

    public sealed class NoTransactionManager : ITransactionManager
    {
        public ActiveTransaction? GetActive(ProfileRuntime profile) => null;

        public Task<TransactionInfo> BeginAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransactionInfo> CommitAsync(ProfileRuntime profile, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransactionInfo> RollbackAsync(ProfileRuntime profile, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Touch(ProfileRuntime profile)
        {
        }

        public Task RollbackAllAsync(string reason) => Task.CompletedTask;
    }

    /// <summary>Administrador mínimo con una transacción activa fija (la implementación real es de otro módulo).</summary>
    public sealed class FixedTransactionManager(ActiveTransaction active) : ITransactionManager
    {
        public int Touches { get; private set; }

        public ActiveTransaction? GetActive(ProfileRuntime profile) => active;

        public Task<TransactionInfo> BeginAsync(ProfileRuntime profile, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransactionInfo> CommitAsync(ProfileRuntime profile, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TransactionInfo> RollbackAsync(ProfileRuntime profile, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Touch(ProfileRuntime profile) => Touches++;

        public Task RollbackAllAsync(string reason) => Task.CompletedTask;
    }
}
