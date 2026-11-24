using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tools;

/// <summary>
/// Filtros de invocación de herramientas: el más externo captura cualquier excepción y la sanea (RF-OUT-04);
/// el segundo valida estrictamente los argumentos contra el inputSchema publicado (RF-TOOL-01, RS-INJ-02, CA-24),
/// porque el SDK ignora propiedades desconocidas y no aplica longitudes ni rangos.
/// </summary>
public static class ToolFilters
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Sanitize(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not McpProtocolException)
            {
                var correlationId = Guid.CreateVersion7().ToString("N");
                var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("SqlServerMcp.Tools");
                logger?.LogError("Excepción no controlada en la herramienta {Tool} ({Type}); correlación {CorrelationId}.",
                    context.Params?.Name, ex.GetType().FullName, correlationId);
                return ErrorResult(ErrorCodes.InternalError, $"Error interno del servidor MCP. Correlación: {correlationId}.", null, correlationId);
            }
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> StrictArguments(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is not McpServerTool tool)
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            var errors = new List<string>();
            var arguments = context.Params?.Arguments;
            ValidateObject(tool.ProtocolTool.InputSchema, arguments, "", errors);
            if (errors.Count > 0)
            {
                return ErrorResult(ErrorCodes.InvalidArgument,
                    "Argumentos inválidos: " + string.Join("; ", errors.Take(10)),
                    "Revise el esquema de entrada de la herramienta. No se admiten propiedades adicionales (por ejemplo, 'confirm'): la confirmación solo la otorga el usuario desde el cliente MCP.",
                    Guid.CreateVersion7().ToString("N"));
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        };

    internal static CallToolResult ErrorResult(string code, string message, string? suggestion, string correlationId)
    {
        var response = new ToolResponse<object>
        {
            Ok = false,
            CorrelationId = correlationId,
            Error = new ToolError { Code = code, Message = message, Suggestion = suggestion },
        };
        var structured = JsonSerializer.SerializeToElement(response, ToolJson.Options);
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        };
    }

    /// <summary>Valida un objeto de argumentos contra un subconjunto de JSON Schema (type, properties, required, additionalProperties, enum, maxLength, minLength, minimum, maximum, items, minItems, maxItems).</summary>
    internal static void ValidateObject(JsonElement schema, IDictionary<string, JsonElement>? arguments, string path, List<string> errors)
    {
        arguments ??= new Dictionary<string, JsonElement>();
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

        foreach (var (key, value) in arguments)
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(key, out var propertySchema))
            {
                errors.Add($"argumento desconocido '{path}{key}'");
                continue;
            }

            ValidateValue(propertySchema, value, path + key, errors);
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in required.EnumerateArray())
            {
                var name = r.GetString();
                if (name is not null && (!arguments.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null))
                {
                    errors.Add($"falta el argumento obligatorio '{path}{name}'");
                }
            }
        }
    }

    internal static void ValidateValue(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("type", out var typeNode) && !TypeMatches(typeNode, value))
        {
            errors.Add($"'{path}' tiene un tipo inválido (se esperaba {DescribeType(typeNode)})");
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (schema.TryGetProperty("enum", out var enumNode) && enumNode.ValueKind == JsonValueKind.Array)
        {
            var match = enumNode.EnumerateArray().Any(e =>
                (e.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), value.GetString(), StringComparison.Ordinal))
                || (e.ValueKind == JsonValueKind.Null && value.ValueKind == JsonValueKind.Null)
                || (e.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number && e.GetRawText() == value.GetRawText()));
            if (!match)
            {
                var allowed = string.Join(", ", enumNode.EnumerateArray().Where(e => e.ValueKind != JsonValueKind.Null).Select(e => e.GetRawText()));
                errors.Add($"'{path}' debe ser uno de [{allowed}]");
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var length = value.GetString()!.Length;
                if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maxLength) && length > maxLength)
                {
                    errors.Add($"'{path}' supera la longitud máxima ({maxLength})");
                }

                if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var minLength) && length < minLength)
                {
                    errors.Add($"'{path}' es más corto que el mínimo ({minLength})");
                }

                break;
            }

            case JsonValueKind.Number:
            {
                var number = value.GetDouble();
                if (schema.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.Number && number < min.GetDouble())
                {
                    errors.Add($"'{path}' es menor que el mínimo ({min.GetRawText()})");
                }

                if (schema.TryGetProperty("maximum", out var max) && max.ValueKind == JsonValueKind.Number && number > max.GetDouble())
                {
                    errors.Add($"'{path}' es mayor que el máximo ({max.GetRawText()})");
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var count = value.GetArrayLength();
                if (schema.TryGetProperty("maxItems", out var maxItems) && maxItems.TryGetInt32(out var mx) && count > mx)
                {
                    errors.Add($"'{path}' tiene más de {mx} elementos");
                }

                if (schema.TryGetProperty("minItems", out var minItems) && minItems.TryGetInt32(out var mn) && count < mn)
                {
                    errors.Add($"'{path}' tiene menos de {mn} elementos");
                }

                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        ValidateValue(items, item, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", errors);
                        if (errors.Count > 20)
                        {
                            return;
                        }

                        i++;
                    }
                }

                break;
            }

            case JsonValueKind.Object:
            {
                if (schema.TryGetProperty("properties", out _))
                {
                    var dict = value.EnumerateObject().ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
                    ValidateObject(schema, dict, path + ".", errors);
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        ValidateValue(additional, property.Value, $"{path}.{property.Name}", errors);
                    }
                }

                break;
            }
        }
    }

    private static bool TypeMatches(JsonElement typeNode, JsonElement value)
    {
        if (typeNode.ValueKind == JsonValueKind.Array)
        {
            return typeNode.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && SingleTypeMatches(t.GetString()!, value));
        }

        return typeNode.ValueKind != JsonValueKind.String || SingleTypeMatches(typeNode.GetString()!, value);
    }

    private static bool SingleTypeMatches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string DescribeType(JsonElement typeNode) =>
        typeNode.ValueKind == JsonValueKind.Array
            ? string.Join(" o ", typeNode.EnumerateArray().Select(t => t.GetString()))
            : typeNode.GetString() ?? "?";
}
