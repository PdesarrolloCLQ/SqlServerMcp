using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace SqlServerMcp.Configuration;

/// <summary>Genera el JSON Schema del archivo de configuración a partir del modelo (RF-CFG-01), para editores y revisión.</summary>
public static class ConfigSchema
{
    public static string Generate()
    {
        var options = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
                if (type.IsEnum)
                {
                    var values = new JsonArray();
                    foreach (var name in EnumAliases.AcceptedValues(type))
                    {
                        values.Add(name);
                    }

                    var enumNode = new JsonObject { ["type"] = "string", ["enum"] = values };
                    if (Nullable.GetUnderlyingType(context.TypeInfo.Type) is not null)
                    {
                        enumNode["type"] = new JsonArray("string", "null");
                        values.Add(null);
                    }

                    return enumNode;
                }

                if (node is JsonObject obj && obj["properties"] is JsonObject)
                {
                    obj["additionalProperties"] = false;
                }

                return node;
            },
        };

        var schema = ConfigLoader.SerializerOptions.GetJsonSchemaAsNode(typeof(ServerConfig), options).AsObject();
        schema.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        schema.Insert(1, "title", "Configuración de SqlServerMcp");
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
