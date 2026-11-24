namespace SqlServerMcp.Execution;

/// <summary>
/// Temporizador de inactividad de una transacción explícita (RF-DML-10, RS-LIM-03). Se basa en <see cref="TimeProvider"/>
/// para poder probarse con un reloj simulado. Es seguro para hilos: <see cref="Touch"/> puede llamarse desde varias
/// solicitudes concurrentes y la devolución de llamada nunca se invoca mientras se mantiene el bloqueo interno.
/// </summary>
internal sealed class TransactionIdleTimer : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly Action _onExpired;
    private readonly Lock _sync = new();
    private readonly ITimer _timer;
    private DateTimeOffset _lastActivityUtc;
    private bool _expired;
    private bool _disposed;

    /// <param name="timeProvider">Reloj (el del sistema en producción).</param>
    /// <param name="idleTimeout">Inactividad máxima (<see cref="Configuration.LimitsConfig.TransactionIdleSeconds"/>).</param>
    /// <param name="onExpired">Acción al vencer; se invoca una sola vez hasta que se llame a <see cref="Rearm"/>.</param>
    public TransactionIdleTimer(TimeProvider timeProvider, TimeSpan idleTimeout, Action onExpired)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onExpired);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        _timeProvider = timeProvider;
        _idleTimeout = idleTimeout;
        _onExpired = onExpired;
        _lastActivityUtc = timeProvider.GetUtcNow();
        _timer = timeProvider.CreateTimer(static state => ((TransactionIdleTimer)state!).OnTick(), this, idleTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Momento de la última actividad registrada.</summary>
    public DateTimeOffset LastActivityUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastActivityUtc;
            }
        }
    }

    /// <summary>True si venció y aún no se rearmó.</summary>
    public bool IsExpired
    {
        get
        {
            lock (_sync)
            {
                return _expired;
            }
        }
    }

    /// <summary>Registra actividad y reinicia la cuenta de inactividad.</summary>
    public void Touch()
    {
        lock (_sync)
        {
            if (_disposed || _expired)
            {
                return;
            }

            _lastActivityUtc = _timeProvider.GetUtcNow();
            _timer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Vuelve a armar el temporizador tras un vencimiento que no pudo atenderse (por ejemplo, una sentencia en curso).
    /// </summary>
    public void Rearm()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _expired = false;
            _lastActivityUtc = _timeProvider.GetUtcNow();
            _timer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
    }

    private void OnTick()
    {
        lock (_sync)
        {
            if (_disposed || _expired)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - _lastActivityUtc;
            if (elapsed < _idleTimeout)
            {
                // Hubo actividad mientras el temporizador estaba en curso: se reprograma por el tiempo restante.
                _timer.Change(_idleTimeout - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }

            _expired = true;
        }

        _onExpired();
    }
}
