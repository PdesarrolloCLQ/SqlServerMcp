using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Execution.Read;

/// <summary>Validaciones del ejecutor que ocurren antes de abrir cualquier conexión.</summary>
public class QueryExecutorValidationTests
{
    private static readonly JsonElement One = JsonDocument.Parse("1").RootElement.Clone();

    private static ProfileRuntime Profile()
    {
        var config = new ProfileConfig { Connection = new ConnectionConfig { Server = "localhost", Database = "VentasTest" } };
        return new ProfileRuntime("unidad", config, config.Limits);
    }

    private static PolicyEvaluation Evaluation(PolicyAction action = PolicyAction.Allow) => new()
    {
        Statement = new SqlStatementInfo { Parsed = true, Category = StatementCategory.Query },
        Tool = ToolKind.Query,
        Action = action,
        RuleId = action == PolicyAction.Block ? "POL-TEST" : "ALLOW",
        Reason = action == PolicyAction.Block ? "Bloqueada por prueba." : "Permitida.",
    };

    private static (QueryExecutor Executor, FailingFactory Factory) Create()
    {
        var factory = new FailingFactory();
        return (new QueryExecutor(factory, new ResultReader(), new ParameterBinder(), new NoTransactions()), factory);
    }

    private static QueryExecutionRequest Request(string sql = "SELECT 1", PolicyEvaluation? evaluation = null, IReadOnlyList<QueryParameter>? parameters = null, int skip = 0) => new()
    {
        Profile = Profile(),
        Sql = sql,
        MaxRows = 10,
        SkipRows = skip,
        CorrelationId = "c1",
        Evaluation = evaluation ?? Evaluation(),
        Parameters = parameters ?? [],
    };

    [Fact]
    public async Task EvaluacionBloqueada_NoAbreConexion()
    {
        var (executor, factory) = Create();
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => executor.ExecuteQueryAsync(Request(evaluation: Evaluation(PolicyAction.Block)), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.PolicyBlocked, ex.Code);
        Assert.Equal("POL-TEST", ex.RuleId);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task DemasiadosParametros_InvalidArgument()
    {
        var (executor, factory) = Create();
        var parameters = Enumerable.Range(0, 101).Select(i => new QueryParameter("p" + i, null, One)).ToList();
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => executor.ExecuteQueryAsync(Request(parameters: parameters), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ParametroInvalido_NoAbreConexion()
    {
        var (executor, factory) = Create();
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => executor.ExecuteQueryAsync(Request(parameters: [new QueryParameter("x", "nvarchar(5); DROP", One)]), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SentenciaVacia(string sql)
    {
        var (executor, _) = Create();
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => executor.ExecuteQueryAsync(Request(sql), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task SkipNegativo()
    {
        var (executor, _) = Create();
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => executor.ExecuteQueryAsync(Request(skip: -1), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Constructor_ValidaDependencias()
    {
        Assert.Throws<ArgumentNullException>(() => new QueryExecutor(null!, new ResultReader(), new ParameterBinder(), new NoTransactions()));
        Assert.Throws<ArgumentNullException>(() => new ExecutionPlanService(new FailingFactory(), null!));
    }

    [Fact]
    public async Task PlanSinSentencia_InvalidArgument()
    {
        var factory = new FailingFactory();
        var service = new ExecutionPlanService(factory, new ParameterBinder());
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => service.GetEstimatedPlanAsync(Profile(), " ", [], "c", false, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData(QueryIsolationLevel.ReadCommitted, IsolationLevel.ReadCommitted)]
    [InlineData(QueryIsolationLevel.Snapshot, IsolationLevel.Snapshot)]
    [InlineData(QueryIsolationLevel.ReadUncommitted, IsolationLevel.ReadUncommitted)]
    [InlineData(QueryIsolationLevel.RepeatableRead, IsolationLevel.RepeatableRead)]
    [InlineData(QueryIsolationLevel.Serializable, IsolationLevel.Serializable)]
    public void MapIsolation(QueryIsolationLevel level, IsolationLevel expected) =>
        Assert.Equal(expected, QueryExecutor.MapIsolation(level));

    private sealed class FailingFactory : ISqlConnectionFactory
    {
        public int Calls { get; private set; }

        public Task<SqlConnection> OpenAsync(ProfileRuntime profile, ConnectionRole role, string correlationId, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No debe abrirse una conexión en esta prueba.");
        }

        public string DescribeTarget(ProfileRuntime profile) => "prueba";
    }

    private sealed class NoTransactions : ITransactionManager
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
}
