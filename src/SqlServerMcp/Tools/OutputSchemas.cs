using System.Text.Json.Nodes;

namespace SqlServerMcp.Tools;

/// <summary>
/// Ajusta el esquema de salida publicado (outputSchema) para que describa exactamente lo que el servidor serializa.
/// Los clientes MCP validan structuredContent contra este esquema y rechazan la respuesta completa si no coincide.
/// <list type="bullet">
/// <item>Las propiedades que admiten null no son obligatorias: la serialización omite los null (WhenWritingNull),
/// pero el exportador las marca como requeridas cuando son parámetros de constructor.</item>
/// <item>Se eliminan "maxLength" (límite pensado para las entradas; definiciones y valores pueden ser más largos) y
/// "format" (las fechas de SQL Server no llevan zona horaria y no cumplen el formato date-time de RFC 3339).</item>
/// </list>
/// </summary>
public static class OutputSchemas
{
    /// <summary>Palabras clave cuyo valor es un mapa nombre → esquema.</summary>
    private static readonly string[] SchemaMaps = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"];

    /// <summary>Palabras clave cuyo valor es un esquema.</summary>
    private static readonly string[] SchemaValues = ["items", "additionalProperties", "additionalItems", "unevaluatedItems", "unevaluatedProperties", "contains", "propertyNames", "not", "if", "then", "else"];

    /// <summary>Palabras clave cuyo valor es una lista de esquemas.</summary>
    private static readonly string[] SchemaLists = ["anyOf", "allOf", "oneOf", "prefixItems"];

    public static void Normalize(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
        {
            return;
        }

        obj.Remove("maxLength");
        obj.Remove("format");
        RelaxRequired(obj);

        foreach (var keyword in SchemaMaps)
        {
            if (obj[keyword] is JsonObject map)
            {
                foreach (var (_, child) in map)
                {
                    Normalize(child);
                }
            }
        }

        foreach (var keyword in SchemaValues)
        {
            Normalize(obj[keyword]);
        }

        foreach (var keyword in SchemaLists)
        {
            if (obj[keyword] is JsonArray list)
            {
                foreach (var child in list)
                {
                    Normalize(child);
                }
            }
        }
    }

    /// <summary>Deja como obligatorias solo las propiedades cuyo esquema declara un tipo que no admite null.</summary>
    private static void RelaxRequired(JsonObject obj)
    {
        if (obj["required"] is not JsonArray required)
        {
            return;
        }

        var properties = obj["properties"] as JsonObject;
        var kept = new JsonArray();
        foreach (var name in required.Select(r => r?.GetValue<string>()).Where(n => n is not null))
        {
            if (properties?[name!] is JsonObject property && !AllowsNull(property))
            {
                kept.Add(name);
            }
        }

        if (kept.Count == 0)
        {
            obj.Remove("required");
        }
        else
        {
            obj["required"] = kept;
        }
    }

    /// <summary>
    /// True si el valor puede ser null u omitirse con seguridad: tipo con "null", sin tipo declarado
    /// (cualquier valor, $ref, anyOf…) o con "nullable".
    /// </summary>
    internal static bool AllowsNull(JsonObject schema) => schema["type"] switch
    {
        JsonValue v => v.TryGetValue<string>(out var t) && t == "null",
        JsonArray a => a.Any(e => e is JsonValue ev && ev.TryGetValue<string>(out var et) && et == "null"),
        _ => true,
    } || schema["nullable"] is JsonValue n && n.TryGetValue<bool>(out var b) && b;
}
