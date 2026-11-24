namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>
/// Reloj manual para pruebas: el tiempo solo avanza con <see cref="Advance"/>, que dispara en orden los temporizadores
/// vencidos. Los callbacks se invocan sin mantener el bloqueo interno (pueden llamar a Change o Dispose).
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public int ActiveTimerCount
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(t => !t.IsDisposed && t.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        lock (_sync)
        {
            _now += delta;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_sync)
            {
                due = _timers
                    .Where(t => !t.IsDisposed && t.DueAt is { } at && at <= _now)
                    .OrderBy(t => t.DueAt)
                    .FirstOrDefault();
                due?.ArmNextLocked();
            }

            if (due is null)
            {
                return;
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (IsDisposed)
                {
                    return false;
                }

                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }
        }

        public void ArmNextLocked() =>
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._sync)
            {
                IsDisposed = true;
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
