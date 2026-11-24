using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SqlServerMcp.Tools;

/// <summary>
/// Envoltura común de todas las respuestas (RF-OUT-01): correlación, perfil, base de datos, duración,
/// filas, truncamiento, advertencias y, en caso de error, código estable y sugerencia (RF-OUT-02/03).
/// </summary>
/// <typeparam name="TData">Datos específicos de la herramienta.</typeparam>
public sealed class ToolResponse<TData>
    where TData : class
{
    /// <summary>True si la operación terminó correctamente.</summary>
    public required bool Ok { get; init; }

    /// <summary>Identificador de correlación (también registrado en SESSION_CONTEXT y en la auditoría).</summary>
    public required string CorrelationId { get; init; }

    public string? Profile { get; init; }

    public string? Database { get; init; }

    public long DurationMs { get; init; }

    public long? RowsReturned { get; init; }

    public long? RowsAffected { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TData? Data { get; init; }

    public ToolError? Error { get; init; }
}

/// <summary>Error devuelto al agente con código estable (RF-OUT-03).</summary>
public sealed class ToolError
{
    /// <summary>POLICY_BLOCKED, CONFIRMATION_REQUIRED, CONFIRMATION_DENIED, CONFIRMATION_UNAVAILABLE, LIMIT_EXCEEDED, SQL_ERROR…</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>Regla de política que produjo el rechazo.</summary>
    public string? RuleId { get; init; }

    /// <summary>Sugerencia accionable para corregir la llamada.</summary>
    public string? Suggestion { get; init; }

    /// <summary>Detalle del error de SQL Server (RF-OUT-02).</summary>
    public SqlErrorInfo? Sql { get; init; }

    /// <summary>Datos adicionales seguros (por ejemplo, filas estimadas).</summary>
    public Dictionary<string, object?>? Details { get; init; }
}

/// <summary>Error de SQL Server: número, severidad, estado, línea y mensaje (RF-OUT-02).</summary>
public sealed class SqlErrorInfo
{
    public int Number { get; init; }

    public int Severity { get; init; }

    public int State { get; init; }

    public int Line { get; init; }

    public string? Procedure { get; init; }

    public required string Message { get; init; }
}

/// <summary>Opciones JSON de las herramientas: camelCase, enums como texto y sin valores numéricos para enums.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
