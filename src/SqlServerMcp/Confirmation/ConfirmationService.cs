using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Confirmation;

public enum ConfirmationStatus
{
    Approved = 0,
    Denied = 1,
    Cancelled = 2,
    Expired = 3,
    Unavailable = 4,
}

/// <summary>Datos que se muestran al usuario para aprobar una operación (RS-HIL-02).</summary>
public sealed class ConfirmationRequest
{
    public required string Tool { get; init; }

    public required ProfileRuntime Profile { get; init; }

    public required string Database { get; init; }

    /// <summary>Sentencia completa normalizada (o descripción de la operación estructurada).</summary>
    public required string Statement { get; init; }

    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    public required string Operation { get; init; }

    public IReadOnlyList<string> AffectedObjects { get; init; } = [];

    public long? EstimatedRows { get; init; }

    public RiskLevel Risk { get; init; }

    public required string RuleId { get; init; }

    public required string Reason { get; init; }

    /// <summary>Objetos dependientes (DROP / cambios de firma).</summary>
    public IReadOnlyList<string> Dependents { get; init; } = [];
}

/// <summary>Aprobación de un solo uso vinculada al hash de la sentencia y sus parámetros (RS-HIL-03).</summary>
public sealed class ConfirmationResult
{
    public required ConfirmationStatus Status { get; init; }

    /// <summary>Hash SHA-256 de herramienta + perfil + base + sentencia + parámetros.</summary>
    public required string BindingHash { get; init; }

    public DateTimeOffset DecidedAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public long? ApprovedRows { get; init; }

    public string AuditValue => Status switch
    {
        ConfirmationStatus.Approved => "approved",
        ConfirmationStatus.Denied => "denied",
        ConfirmationStatus.Cancelled => "cancelled",
        ConfirmationStatus.Expired => "expired",
        _ => "unavailable",
    };
}

/// <summary>Confirmación humana mediante elicitation de MCP (RS-HIL-01..07).</summary>
public interface IConfirmationService
{
    /// <summary>
    /// Solicita la aprobación del usuario a través del cliente MCP. No existe ningún argumento de herramienta que la
    /// omita (RS-HIL-04). Si el cliente no soporta elicitation devuelve <see cref="ConfirmationStatus.Unavailable"/>.
    /// </summary>
    Task<ConfirmationResult> RequestAsync(McpServer server, ConfirmationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Consume la aprobación justo antes de ejecutar: debe existir, no haber expirado, no haberse usado y coincidir
    /// con el hash recalculado de la sentencia que se va a ejecutar. Devuelve false en cualquier otro caso.
    /// </summary>
    bool TryConsume(ConfirmationResult approval, ConfirmationRequest request);

    /// <summary>Hash de vinculación de una solicitud.</summary>
    string ComputeBindingHash(ConfirmationRequest request);
}

public sealed class ConfirmationService : IConfirmationService
{
    private const string ApproveField = "aprobar";
    private const int MaxStatementChars = 6000;

    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger<ConfirmationService> _logger;

    public ConfirmationService(TimeProvider time, ILogger<ConfirmationService> logger)
    {
        _time = time;
        _logger = logger;
    }

    public string ComputeBindingHash(ConfirmationRequest request)
    {
        var sb = new StringBuilder();
        sb.Append(request.Tool).Append('\n')
          .Append(request.Profile.Name).Append('\n')
          .Append(request.Database).Append('\n')
          .Append(request.Statement).Append('\n');
        foreach (var p in request.Parameters.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(p.Name).Append(':').Append(p.Type).Append('=').Append(p.Value.GetRawText()).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async Task<ConfirmationResult> RequestAsync(McpServer server, ConfirmationRequest request, CancellationToken cancellationToken)
    {
        var hash = ComputeBindingHash(request);
        var now = _time.GetUtcNow();
        var expiry = TimeSpan.FromSeconds(request.Profile.Limits.ConfirmationExpirySeconds);
        PurgeExpired(now);

        if (server.ClientCapabilities?.Elicitation?.Form is null)
        {
            return new ConfirmationResult { Status = ConfirmationStatus.Unavailable, BindingHash = hash, DecidedAtUtc = now, ExpiresAtUtc = now };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(expiry);
        ConfirmationStatus status;
        try
        {
            var result = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = BuildMessage(request),
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        [ApproveField] = new ElicitRequestParams.BooleanSchema
                        {
                            Title = "Aprobar ejecución",
                            Description = "Marque para ejecutar exactamente la operación mostrada. Cualquier cambio requerirá una nueva aprobación.",
                            Default = false,
                        },
                    },
                    Required = [ApproveField],
                },
            }, cts.Token).ConfigureAwait(false);

            status = result.Action switch
            {
                "accept" when result.Content is not null
                    && result.Content.TryGetValue(ApproveField, out var value)
                    && value.ValueKind == JsonValueKind.True => ConfirmationStatus.Approved,
                "accept" or "decline" => ConfirmationStatus.Denied,
                _ => ConfirmationStatus.Cancelled,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            status = ConfirmationStatus.Expired;
        }
        catch (InvalidOperationException)
        {
            status = ConfirmationStatus.Unavailable;
        }
        catch (McpException ex)
        {
            _logger.LogWarning("La solicitud de confirmación falló en el cliente MCP ({Type}).", ex.GetType().Name);
            status = ConfirmationStatus.Unavailable;
        }

        var decidedAt = _time.GetUtcNow();
        var approval = new ConfirmationResult
        {
            Status = status,
            BindingHash = hash,
            DecidedAtUtc = decidedAt,
            ExpiresAtUtc = decidedAt + expiry,
            ApprovedRows = status == ConfirmationStatus.Approved ? request.EstimatedRows : null,
        };

        if (status == ConfirmationStatus.Approved)
        {
            _approvals[hash + "|" + decidedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)] = new PendingApproval(hash, approval.ExpiresAtUtc);
        }

        return approval;
    }

    public bool TryConsume(ConfirmationResult approval, ConfirmationRequest request)
    {
        if (approval.Status != ConfirmationStatus.Approved)
        {
            return false;
        }

        var key = approval.BindingHash + "|" + approval.DecidedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);
        if (!_approvals.TryRemove(key, out var pending))
        {
            return false; // ya usada o inexistente (un solo uso)
        }

        if (_time.GetUtcNow() > pending.ExpiresAtUtc)
        {
            return false;
        }

        return string.Equals(pending.Hash, ComputeBindingHash(request), StringComparison.Ordinal);
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var (key, value) in _approvals)
        {
            if (now > value.ExpiresAtUtc)
            {
                _approvals.TryRemove(key, out _);
            }
        }
    }

    internal static string BuildMessage(ConfirmationRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SqlServerMcp solicita su aprobación para ejecutar una operación de riesgo.");
        sb.AppendLine();
        sb.Append("Perfil: ").Append(r.Profile.Name).Append(" (").Append(r.Profile.Config.Environment).Append(", modo ").Append(r.Profile.Mode).AppendLine(")");
        sb.Append("Base de datos: ").AppendLine(r.Database);
        sb.Append("Herramienta: ").AppendLine(r.Tool);
        sb.Append("Operación: ").AppendLine(r.Operation);
        if (r.AffectedObjects.Count > 0)
        {
            sb.Append("Objetos afectados: ").AppendLine(string.Join(", ", r.AffectedObjects.Take(20)));
        }

        sb.Append("Filas estimadas: ").AppendLine(r.EstimatedRows?.ToString("N0", CultureInfo.GetCultureInfo("es-CO")) ?? "desconocido");
        sb.Append("Nivel de riesgo: ").AppendLine(r.Risk switch
        {
            RiskLevel.Critical => "CRÍTICO",
            RiskLevel.High => "ALTO",
            RiskLevel.Medium => "MEDIO",
            _ => "BAJO",
        });
        sb.Append("Regla: ").Append(r.RuleId).Append(" — ").AppendLine(r.Reason);
        if (r.Dependents.Count > 0)
        {
            sb.Append("Objetos dependientes que podrían quedar inválidos: ").AppendLine(string.Join(", ", r.Dependents.Take(30)));
        }

        if (r.Parameters.Count > 0)
        {
            sb.Append("Parámetros: ").AppendLine(string.Join(", ", r.Parameters.Select(p => p.Sensitive ? $"{p.Name}=****" : $"{p.Name}={Truncate(p.Value.GetRawText(), 80)}")));
        }

        sb.AppendLine();
        sb.AppendLine("Sentencia:");
        sb.AppendLine(Truncate(r.Statement, MaxStatementChars));
        sb.AppendLine();
        sb.Append("La aprobación es de un solo uso y expira en ").Append(r.Profile.Limits.ConfirmationExpirySeconds).Append(" segundos.");
        return sb.ToString();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "… (recortado)";

    private sealed record PendingApproval(string Hash, DateTimeOffset ExpiresAtUtc);
}
