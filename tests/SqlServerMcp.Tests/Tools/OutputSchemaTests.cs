using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;
using SqlServerMcp.Tools;

namespace SqlServerMcp.Tests.Tools;

/// <summary>
/// El outputSchema publicado debe aceptar todo lo que el servidor serializa: los clientes MCP validan
/// structuredContent y descartan la respuesta si no cumple (null omitidos, fechas sin zona horaria, textos largos).
/// </summary>
public sealed class OutputSchemaTests
{
    [Fact]
    public void Normalize_RelajaRequeridosNulables_YQuitaMaxLengthYFormat()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "maxLength": 4000 },
                "format": { "type": ["string", "null"], "maxLength": 4000 },
                "createDate": { "type": "string", "format": "date-time" },
                "anything": {},
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": { "id": { "type": "integer" }, "note": { "type": ["string", "null"] } },
                    "required": ["id", "note"]
                  }
                }
              },
              "required": ["name", "format", "createDate", "anything", "items"]
            }
            """)!;

        OutputSchemas.Normalize(schema);

        var properties = schema["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("format"), "una propiedad llamada 'format' no es la palabra clave");
        Assert.Null(properties["name"]!["maxLength"]);
        Assert.Null(properties["createDate"]!["format"]);
        Assert.Equal(["name", "createDate", "items"], Names(schema["required"]));
        Assert.Equal(["id"], Names(properties["items"]!["items"]!["required"]));
    }

    [Fact]
    public void Normalize_SinRequeridosNoNulables_EliminaRequired()
    {
        var schema = JsonNode.Parse("""{ "type": "object", "properties": { "a": { "type": ["integer", "null"] } }, "required": ["a"] }""")!;
        OutputSchemas.Normalize(schema);
        Assert.Null(schema["required"]);
    }

    [Fact]
    public void HerramientasPublicadas_EsquemasDeSalidaCompatiblesConLaSerializacion()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "sqlservermcp.example.json");
        var loaded = ConfigLoader.Load(example, null, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        using var provider = new ServiceCollection().AddSqlServerMcpTools(loaded).BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToList();
        Assert.Equal(26, tools.Count);

        foreach (var tool in tools)
        {
            var output = tool.ProtocolTool.OutputSchema;
            Assert.True(output.HasValue, $"{tool.ProtocolTool.Name} no publica outputSchema");
            var problems = new List<string>();
            Inspect(JsonNode.Parse(output.Value.GetRawText()), "#", problems);
            Assert.True(problems.Count == 0, $"{tool.ProtocolTool.Name}: {string.Join("; ", problems)}");
        }

        // Los límites de longitud siguen aplicando a las entradas.
        var runQuery = tools.Single(t => t.ProtocolTool.Name == "run_query");
        Assert.Contains("maxLength", runQuery.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
    }

    private static void Inspect(JsonNode? node, string path, List<string> problems)
    {
        switch (node)
        {
            case JsonObject obj:
                var isSchema = obj["type"] is JsonValue or JsonArray;
                if (isSchema)
                {
                    if (obj.ContainsKey("maxLength"))
                    {
                        problems.Add($"{path}: maxLength");
                    }

                    if (obj.ContainsKey("format"))
                    {
                        problems.Add($"{path}: format");
                    }

                    if (obj["required"] is JsonArray required && obj["properties"] is JsonObject properties)
                    {
                        foreach (var name in Names(required))
                        {
                            if (properties[name] is not JsonObject p || OutputSchemas.AllowsNull(p))
                            {
                                problems.Add($"{path}: '{name}' es requerida pero admite null");
                            }
                        }
                    }
                }

                foreach (var (key, child) in obj)
                {
                    Inspect(child, $"{path}/{key}", problems);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Inspect(array[i], $"{path}/{i}", problems);
                }

                break;
        }
    }

    private static string[] Names(JsonNode? required) =>
        required is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToArray() : [];
}
