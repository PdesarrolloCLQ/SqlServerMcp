using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tools;

/// <summary>Parámetro con nombre para sentencias parametrizadas (RF-QRY-02).</summary>
public sealed class QueryParameterInput
{
    [Description("Nombre del parámetro tal como aparece en la sentencia, con o sin '@' (por ejemplo '@clienteId').")]
    [MaxLength(129)]
    public required string Name { get; init; }

    [Description("Valor: texto, número, booleano o null. Fechas en ISO 8601, binarios en Base64, GUID como texto.")]
    public JsonElement Value { get; init; }

    [Description("Tipo SQL opcional: int, bigint, bit, decimal(18,2), nvarchar(50), varchar(max), date, datetime2, datetimeoffset, uniqueidentifier, varbinary(max)…")]
    [MaxLength(64)]
    public string? Type { get; init; }

    [Description("Si es true, el valor no se registra en la auditoría.")]
    public bool Sensitive { get; init; }

    public QueryParameter ToQueryParameter() => new(Name, Type, Value.ValueKind == JsonValueKind.Undefined ? NullElement : Value, Sensitive);

    internal static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    public static IReadOnlyList<QueryParameter> Convert(IReadOnlyList<QueryParameterInput>? inputs)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return [];
        }

        if (inputs.Count > 100)
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "Se admiten como máximo 100 parámetros por invocación.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<QueryParameter>(inputs.Count);
        foreach (var input in inputs)
        {
            var name = input.Name.StartsWith('@') ? input.Name[1..] : input.Name;
            if (!names.Add(name))
            {
                throw new ToolFailureException(ErrorCodes.InvalidArgument, $"El parámetro '@{name}' está repetido.");
            }

            result.Add(input.ToQueryParameter());
        }

        return result;
    }
}

/// <summary>Argumento de procedimiento almacenado (RF-PRC-01).</summary>
public sealed class ProcedureArgumentInput
{
    [Description("Nombre del parámetro del procedimiento, con o sin '@'.")]
    [MaxLength(129)]
    public required string Name { get; init; }

    [Description("Valor de entrada (texto, número, booleano o null). Para parámetros solo de salida use null.")]
    public JsonElement Value { get; init; }

    [Description("Marcar como parámetro de salida (OUTPUT).")]
    public bool Output { get; init; }

    [Description("Si es true, el valor no se registra en la auditoría.")]
    public bool Sensitive { get; init; }
}

/// <summary>Herramienta destino para validate_sql.</summary>
public enum ValidationTarget
{
    Any = 0,
    RunQuery = 1,
    ExecuteDml = 2,
    ExecuteDdl = 3,
}
