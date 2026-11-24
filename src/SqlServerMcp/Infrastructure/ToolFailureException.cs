using SqlServerMcp.Configuration;

namespace SqlServerMcp.Infrastructure;

/// <summary>
/// Error controlado de una herramienta. Su mensaje se devuelve al agente tal cual, por lo que
/// nunca debe contener secretos, cadenas de conexión ni rutas internas (RF-OUT-04).
/// </summary>
public sealed class ToolFailureException : Exception
{
    public ToolFailureException(string code, string message, string? suggestion = null, string? ruleId = null, AlertSeverity? alert = null)
        : base(message)
    {
        Code = code;
        Suggestion = suggestion;
        RuleId = ruleId;
        Alert = alert;
    }

    public string Code { get; }

    public string? Suggestion { get; }

    public string? RuleId { get; }

    public AlertSeverity? Alert { get; }

    /// <summary>Datos adicionales seguros para el agente (por ejemplo, filas estimadas).</summary>
    public Dictionary<string, object?> Details { get; } = [];
}
