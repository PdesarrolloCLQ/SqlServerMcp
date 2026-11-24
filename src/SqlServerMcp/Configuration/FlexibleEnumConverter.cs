using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlServerMcp.Configuration;

/// <summary>
/// Convierte enumeraciones desde texto sin distinguir mayúsculas ni guiones bajos, con alias en español
/// ("LECTURA", "ESCRITURA_DATOS", "PERMITIR", "BLOQUEAR"…). Rechaza valores numéricos y desconocidos.
/// </summary>
public sealed class FlexibleEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum || Nullable.GetUnderlyingType(typeToConvert)?.IsEnum == true;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        var converterType = typeof(FlexibleEnumConverter<>).MakeGenericType(enumType);
        var converter = (JsonConverter)Activator.CreateInstance(converterType)!;
        if (Nullable.GetUnderlyingType(typeToConvert) is not null)
        {
            var nullableType = typeof(NullableConverter<>).MakeGenericType(enumType);
            return (JsonConverter)Activator.CreateInstance(nullableType, converter)!;
        }

        return converter;
    }

    private sealed class NullableConverter<T>(JsonConverter<T> inner) : JsonConverter<T?>
        where T : struct, Enum
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : inner.Read(ref reader, typeof(T), options);

        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                inner.Write(writer, value.Value, options);
            }
        }
    }
}

public sealed class FlexibleEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Se esperaba un texto para {typeof(T).Name}.");
        }

        var raw = reader.GetString() ?? string.Empty;
        if (TryParse(raw, out var value))
        {
            return value;
        }

        throw new JsonException($"Valor '{raw}' no válido para {typeof(T).Name}. Valores admitidos: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public static bool TryParse(string raw, out T value)
    {
        // Primero el nombre exacto del enum (sin guiones bajos ni mayúsculas); luego los alias en español.
        if (TryMatch(raw.Trim(), out value))
        {
            return true;
        }

        return EnumAliases.Map.TryGetValue(raw.Trim(), out var alias) && TryMatch(alias, out value);
    }

    private static bool TryMatch(string candidate, out T value)
    {
        var compact = candidate.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, compact, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse<T>(name);
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Alias en español/abreviados admitidos para los valores de enumeración de la configuración.</summary>
public static class EnumAliases
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PERMITIR"] = "Allow",
        ["ALERTAR"] = "Alert",
        ["CONFIRMAR"] = "Confirm",
        ["BLOQUEAR"] = "Block",
        ["READ"] = "Lectura",
        ["READONLY"] = "Lectura",
        ["DATAWRITE"] = "EscrituraDatos",
        ["FULL"] = "Completo",
        ["ENMASCARAR"] = "Mask",
        ["DESARROLLO"] = "Development",
        ["PRUEBAS"] = "Test",
        ["PRODUCCION"] = "Production",
        ["PRODUCCIÓN"] = "Production",
        ["INTEGRATED"] = "WindowsIntegrated",
        ["WINDOWS"] = "WindowsIntegrated",
        ["SQL"] = "SqlPassword",
    };

    /// <summary>Valores aceptados para un enum: nombre, NOMBRE_EN_MAYÚSCULAS y alias.</summary>
    public static IEnumerable<string> AcceptedValues(Type enumType)
    {
        var names = Enum.GetNames(enumType);
        var result = new List<string>();
        foreach (var name in names)
        {
            result.Add(name);
            var snake = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])([A-Z])", "_$1").ToUpperInvariant();
            if (!result.Contains(snake))
            {
                result.Add(snake);
            }
        }

        foreach (var (alias, target) in Map)
        {
            if (names.Contains(target, StringComparer.OrdinalIgnoreCase) && !result.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(alias);
            }
        }

        return result;
    }
}
