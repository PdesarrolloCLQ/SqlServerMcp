using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-10: reglas del administrador de transacciones que no requieren base de datos.</summary>
public class TransactionManagerTests
{
    [Fact]
    public void SinTransaccion_GetActiveDevuelveNull()
    {
        var manager = CreateManager(out _);
        Assert.Null(manager.GetActive(WriteTestSql.Profile()));
    }

    [Fact]
    public async Task CommitSinTransaccion_EsTransactionError()
    {
        var manager = CreateManager(out _);
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => manager.CommitAsync(WriteTestSql.Profile(), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.TransactionError, ex.Code);
    }

    [Fact]
    public async Task RollbackSinTransaccion_EsTransactionError()
    {
        var manager = CreateManager(out _);
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => manager.RollbackAsync(WriteTestSql.Profile(), "prueba", TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.TransactionError, ex.Code);
    }

    [Fact]
    public async Task FalloAlConectar_LiberaElTurnoDelPerfil()
    {
        var manager = CreateManager(out var factory);
        var profile = WriteTestSql.Profile();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginAsync(profile, "c1", TestContext.Current.CancellationToken));
        // Si el turno no se liberara, el segundo intento devolvería TRANSACTION_ERROR en lugar de volver a conectar.
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginAsync(profile, "c2", TestContext.Current.CancellationToken));
        Assert.Equal(2, factory.Calls);
        Assert.Null(manager.GetActive(profile));
    }

    [Fact]
    public async Task TouchYRollbackAllSinTransacciones_NoFallan()
    {
        var manager = CreateManager(out _);
        manager.Touch(WriteTestSql.Profile());
        await manager.RollbackAllAsync("prueba");
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task TrasDispose_NoAdmiteNuevasTransacciones()
    {
        var manager = CreateManager(out _);
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.BeginAsync(WriteTestSql.Profile(), "c", TestContext.Current.CancellationToken));
    }

    private static TransactionManager CreateManager(out FailingConnectionFactory factory)
    {
        factory = new FailingConnectionFactory();
        return new TransactionManager(factory, new NullAuditLog(), NullLogger<TransactionManager>.Instance, new ManualTimeProvider());
    }

    private sealed class FailingConnectionFactory : ISqlConnectionFactory
    {
        public int Calls { get; private set; }

        public Task<SqlConnection> OpenAsync(ProfileRuntime profile, ConnectionRole role, string correlationId, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("sin servidor");
        }

        public string DescribeTarget(ProfileRuntime profile) => "prueba";
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public bool IsHealthy => true;

        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
