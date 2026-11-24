using System.Collections.Concurrent;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Infrastructure;

/// <summary>
/// Límite de invocaciones por minuto y de consultas concurrentes por sesión (RS-LIM-01). Con transporte stdio
/// cada proceso atiende una sola sesión, por lo que el estado es del proceso, por perfil.
/// </summary>
public sealed class CallRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _concurrency = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public CallRateLimiter(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Tiempo máximo de espera por un turno de ejecución concurrente.</summary>
    public TimeSpan ConcurrencyWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Registra una invocación; lanza RATE_LIMITED si se supera el máximo por minuto.</summary>
    public void RegisterCall(ProfileRuntime profile)
    {
        var queue = _calls.GetOrAdd(profile.Name, _ => new Queue<DateTimeOffset>());
        var now = _time.GetUtcNow();
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= profile.Limits.MaxCallsPerMinute)
            {
                throw new ToolFailureException(ErrorCodes.RateLimited,
                    $"Se alcanzó el máximo de {profile.Limits.MaxCallsPerMinute} invocaciones por minuto del perfil '{profile.Name}'.",
                    "Espere unos segundos y reduzca la cantidad de llamadas, por ejemplo combinando consultas.");
            }

            queue.Enqueue(now);
        }
    }

    /// <summary>Obtiene un turno de ejecución concurrente; libérelo con Dispose.</summary>
    public async Task<IDisposable> AcquireAsync(ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var semaphore = _concurrency.GetOrAdd(profile.Name, _ => new SemaphoreSlim(profile.Limits.MaxConcurrentQueries, profile.Limits.MaxConcurrentQueries));
        if (!await semaphore.WaitAsync(ConcurrencyWait, cancellationToken).ConfigureAwait(false))
        {
            throw new ToolFailureException(ErrorCodes.RateLimited,
                $"Hay {profile.Limits.MaxConcurrentQueries} operaciones en curso para el perfil '{profile.Name}' (máximo por sesión).",
                "Espere a que terminen las operaciones en curso antes de enviar otra.");
        }

        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
