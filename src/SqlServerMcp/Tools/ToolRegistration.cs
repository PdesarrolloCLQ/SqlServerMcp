using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Tools;

/// <summary>Modo mínimo del perfil para publicar una herramienta (RF-TOOL-02). Sin atributo = COMPLETO (fail-closed).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MinimumModeAttribute(OperationMode mode) : Attribute
{
    public OperationMode Mode { get; } = mode;
}

/// <summary>
/// Registro de herramientas: solo se publican las del modo más amplio de los perfiles activos; las demás no se
/// registran, por lo que no aparecen en tools/list ni se pueden invocar (RF-TOOL-02, CA-25). Los esquemas de entrada
/// declaran additionalProperties=false y longitudes máximas (RF-TOOL-01).
/// </summary>
public static class ToolRegistration
{
    /// <summary>Tipos que contienen herramientas MCP.</summary>
    public static readonly Type[] ToolTypes =
    [
        typeof(ServerTools),
        typeof(MetadataTools),
        typeof(QueryTools),
        typeof(DmlTools),
        typeof(ProcedureTools),
        typeof(TransactionTools),
        typeof(DdlTools),
    ];

    /// <summary>Modo más amplio entre los perfiles activos.</summary>
    public static OperationMode PublishedMode(LoadedConfiguration config) => config.Profiles.Values.Max(p => p.Mode);

    /// <summary>Métodos de herramienta publicables para un modo.</summary>
    public static IEnumerable<MethodInfo> MethodsForMode(OperationMode mode)
    {
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
                {
                    continue;
                }

                var minimum = method.GetCustomAttribute<MinimumModeAttribute>()?.Mode ?? OperationMode.Completo;
                if (minimum <= mode)
                {
                    yield return method;
                }
            }
        }
    }

    public static IServiceCollection AddSqlServerMcpTools(this IServiceCollection services, LoadedConfiguration config)
    {
        var mode = PublishedMode(config);
        var schemaOptions = CreateSchemaOptions(config);
        var profiles = config.Profiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        foreach (var method in MethodsForMode(mode))
        {
            var toolType = method.DeclaringType!;
            var mi = method;
            services.AddSingleton<McpServerTool>(sp => new ProfileEnumTool(
                mi.IsStatic
                    ? McpServerTool.Create(mi, target: null, new McpServerToolCreateOptions { Services = sp, SchemaCreateOptions = schemaOptions, SerializerOptions = ToolJson.Options })
                    : McpServerTool.Create(mi, ctx => ActivatorUtilities.CreateInstance(ctx.Services!, toolType),
                        new McpServerToolCreateOptions { Services = sp, SchemaCreateOptions = schemaOptions, SerializerOptions = ToolJson.Options }),
                profiles));
        }

        return services;
    }

    /// <summary>
    /// Esquema estricto: additionalProperties=false en la raíz y objetos anidados; maxLength por defecto en textos;
    /// enumeración de perfiles en el parámetro "profile".
    /// </summary>
    public static AIJsonSchemaCreateOptions CreateSchemaOptions(LoadedConfiguration config)
    {
        return new AIJsonSchemaCreateOptions
        {
            TransformOptions = new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true },
            TransformSchemaNode = (ctx, node) =>
            {
                if (node is not JsonObject o)
                {
                    return node;
                }


                if (IsString(o["type"]) && !o.ContainsKey("maxLength") && !o.ContainsKey("enum") && !o.ContainsKey("format"))
                {
                    o["maxLength"] = 4000;
                }

                return node;
            },
        };
    }

    /// <summary>Hash SHA-256 de las definiciones publicadas (nombre, descripción, esquemas, anotaciones) para detectar cambios (RS-SDL-05).</summary>
    public static string ComputeDefinitionsHash(IEnumerable<McpServerTool> tools)
    {
        var sb = new StringBuilder();
        foreach (var tool in tools.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal))
        {
            sb.Append(JsonSerializer.Serialize(tool.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static bool IsString(JsonNode? type) => type switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) && s == "string",
        JsonArray a => a.Any(e => e is JsonValue v2 && v2.TryGetValue<string>(out var s2) && s2 == "string"),
        _ => false,
    };
}

/// <summary>
/// Publica en el parámetro "profile" la enumeración de perfiles configurados (RF-CON-04: el agente solo puede elegir
/// perfiles definidos). El resto de la definición de la herramienta no cambia.
/// </summary>
internal sealed class ProfileEnumTool : DelegatingMcpServerTool
{
    private readonly ModelContextProtocol.Protocol.Tool _tool;

    public ProfileEnumTool(McpServerTool inner, IReadOnlyList<string> profiles)
        : base(inner)
    {
        var node = JsonSerializer.SerializeToNode(inner.ProtocolTool, ModelContextProtocol.McpJsonUtilities.DefaultOptions)!.AsObject();
        if (node["inputSchema"]?["properties"]?["profile"] is JsonObject profile)
        {
            var values = new JsonArray();
            foreach (var name in profiles)
            {
                values.Add(name);
            }

            values.Add(null);
            profile.Remove("maxLength");
            profile["enum"] = values;
        }

        // El esquema de salida debe coincidir con lo serializado: los clientes rechazan la respuesta si no valida.
        OutputSchemas.Normalize(node["outputSchema"]);

        _tool =node.Deserialize<ModelContextProtocol.Protocol.Tool>(ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
    }

    public override ModelContextProtocol.Protocol.Tool ProtocolTool => _tool;
}
