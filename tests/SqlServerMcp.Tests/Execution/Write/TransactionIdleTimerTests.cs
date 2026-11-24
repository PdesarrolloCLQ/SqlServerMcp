using SqlServerMcp.Execution;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>RF-DML-10, RS-LIM-03: reversión automática por inactividad con un reloj simulado.</summary>
public class TransactionIdleTimerTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);

    [Fact]
    public void VencePasadaLaInactividad()
    {
        var time = new ManualTimeProvider();
        var expirations = 0;
        using var timer = new TransactionIdleTimer(time, Idle, () => expirations++);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, expirations);
        Assert.False(timer.IsExpired);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, expirations);
        Assert.True(timer.IsExpired);
    }

    [Fact]
    public void TouchReiniciaLaCuenta()
    {
        var time = new ManualTimeProvider();
        var expirations = 0;
        using var timer = new TransactionIdleTimer(time, Idle, () => expirations++);

        time.Advance(TimeSpan.FromSeconds(50));
        timer.Touch();
        Assert.Equal(time.GetUtcNow(), timer.LastActivityUtc);
        time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(0, expirations);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, expirations);
    }

    [Fact]
    public void SoloVenceUnaVezHastaRearmar()
    {
        var time = new ManualTimeProvider();
        var expirations = 0;
        using var timer = new TransactionIdleTimer(time, Idle, () => expirations++);

        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(600));
        timer.Touch();
        time.Advance(TimeSpan.FromSeconds(600));
        Assert.Equal(1, expirations);

        timer.Rearm();
        Assert.False(timer.IsExpired);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, expirations);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, expirations);
    }

    [Fact]
    public void TrasDisposeNoVence()
    {
        var time = new ManualTimeProvider();
        var expirations = 0;
        var timer = new TransactionIdleTimer(time, Idle, () => expirations++);
        timer.Dispose();
        timer.Touch();
        timer.Rearm();

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, expirations);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public void ActividadConcurrenteNoProvocaVencimientoPrematuro()
    {
        var time = new ManualTimeProvider();
        var expirations = 0;
        using var timer = new TransactionIdleTimer(time, Idle, () => Interlocked.Increment(ref expirations));

        Parallel.For(0, 200, _ => timer.Touch());
        time.Advance(TimeSpan.FromSeconds(30));
        Parallel.For(0, 200, _ => timer.Touch());
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, Volatile.Read(ref expirations));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref expirations));
    }

    [Fact]
    public void RechazaInactividadNoPositiva() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransactionIdleTimer(new ManualTimeProvider(), TimeSpan.Zero, () => { }));
}
