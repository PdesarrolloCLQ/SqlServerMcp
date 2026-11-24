using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SqlServerMcp.Configuration;

/// <summary>Configuración cargada, validada y lista para usar.</summary>
public sealed class LoadedConfiguration
{
    public required ServerConfig Config { get; init; }

    public required string FilePath { get; init; }

    /// <summary>SHA-256 del archivo, registrado en auditoría al iniciar (RF-CFG-05).</summary>
    public required string FileHash { get; init; }

    public required IReadOnlyDictionary<string, ProfileRuntime> Profiles { get; init; }

    public required string DefaultProfile { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Variables de entorno que sobrescribieron valores (solo nombres).</summary>
    public IReadOnlyList<string> AppliedOverrides { get; init; } = [];
}

public sealed class ConfigurationException(IReadOnlyList<string> errors)
    : Exception("Configuración inválida: " + string.Join(" | ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Carga el archivo JSON de configuración (RF-CFG-01). Deserialización estricta: propiedades desconocidas,
/// tipos incorrectos o valores no admitidos impiden el arranque.
/// </summary>
public static class ConfigLoader
{
    public const string ConfigEnvVar = "SQLSERVERMCP_CONFIG";
    public const string OverridePrefix = "SQLSERVERMCP__";
    public const string DefaultFileName = "sqlservermcp.json";

    public static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new FlexibleEnumConverterFactory());
        return options;
    }

    /// <summary>Resuelve la ruta: --config, variable SQLSERVERMCP_CONFIG, junto al ejecutable o %LOCALAPPDATA%\SqlServerMcp.</summary>
    public static string ResolvePath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length)
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[i + 1]));
            }

            if (args[i].StartsWith("--config=", StringComparison.Ordinal))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[i]["--config=".Length..]));
            }
        }

        var env = Environment.GetEnvironmentVariable(ConfigEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(env));
        }

        var local = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        if (File.Exists(local))
        {
            return local;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlServerMcp", DefaultFileName);
    }

    /// <summary>Lee el perfil forzado por línea de comandos (--profile), si existe.</summary>
    public static string? ResolveProfileArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--profile=", StringComparison.Ordinal))
            {
                return args[i]["--profile=".Length..];
            }
        }

        return null;
    }

    public static LoadedConfiguration Load(string path, string? forcedProfile = null, IDictionary<string, string?>? environment = null)
    {
        if (!File.Exists(path))
        {
            throw new ConfigurationException([$"No se encontró el archivo de configuración '{Path.GetFileName(path)}'. Use --config <ruta> o la variable {ConfigEnvVar}."]);
        }

        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes, nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = true }, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException([$"JSON inválido (línea {ex.LineNumber + 1}): {ex.Message}"]);
        }

        if (root is not JsonObject rootObject)
        {
            throw new ConfigurationException(["La raíz de la configuración debe ser un objeto JSON."]);
        }

        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);

        var overrideErrors = new List<string>();
        var applied = EnvironmentOverrides.Apply(rootObject, environment, overrideErrors);
        if (overrideErrors.Count > 0)
        {
            throw new ConfigurationException(overrideErrors);
        }

        ServerConfig? config;
        try
        {
            config = rootObject.Deserialize<ServerConfig>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            var location = string.IsNullOrEmpty(ex.Path) ? string.Empty : $" en '{ex.Path}'";
            throw new ConfigurationException([$"Error de esquema{location}: {SanitizeJsonMessage(ex.Message)}"]);
        }

        if (config is null)
        {
            throw new ConfigurationException(["La configuración está vacía."]);
        }

        var result = ConfigValidator.Validate(config, forcedProfile);
        if (result.Errors.Count > 0)
        {
            throw new ConfigurationException(result.Errors);
        }

        return new LoadedConfiguration
        {
            Config = config,
            FilePath = path,
            FileHash = hash,
            Profiles = result.Profiles,
            DefaultProfile = result.DefaultProfile!,
            Warnings = result.Warnings,
            AppliedOverrides = applied,
        };
    }

    private static string SanitizeJsonMessage(string message)
    {
        // Los mensajes de System.Text.Json pueden incluir el nombre completo del tipo .NET; se recortan.
        var idx = message.IndexOf(" Path:", StringComparison.Ordinal);
        return idx > 0 ? message[..idx] : message;
    }
}

/// <summary>
/// Sobrescritura de parámetros NO sensibles mediante variables de entorno (RF-CFG-06):
/// SQLSERVERMCP__profiles__desarrollo__limits__maxRows=200. Las rutas sensibles (conexión, credenciales,
/// modo, políticas, exclusiones, máximos absolutos) no se pueden sobrescribir.
/// </summary>
public static class EnvironmentOverrides
{
    public static IReadOnlyList<string> Apply(JsonObject root, IDictionary<string, string?> environment, List<string> errors)
    {
        var applied = new List<string>();
        foreach (var (key, value) in environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!key.StartsWith(ConfigLoader.OverridePrefix, StringComparison.OrdinalIgnoreCase) || value is null)
            {
                continue;
            }

            var path = key[ConfigLoader.OverridePrefix.Length..].Split("__", StringSplitOptions.RemoveEmptyEntries);
            if (!IsAllowedPath(path))
            {
                errors.Add($"La variable de entorno '{key}' intenta sobrescribir un parámetro no permitido. Solo se admiten límites, logging, idioma, retención de auditoría y perfil por defecto.");
                continue;
            }

            if (!SetValue(root, path, value))
            {
                errors.Add($"La variable de entorno '{key}' no corresponde a un parámetro existente.");
                continue;
            }

            applied.Add(key);
        }

        return applied;
    }

    private static bool IsAllowedPath(string[] path)
    {
        if (path.Length == 1)
        {
            return path[0].Equals("defaultProfile", StringComparison.OrdinalIgnoreCase)
                || path[0].Equals("language", StringComparison.OrdinalIgnoreCase);
        }

        if (path.Length == 2 && path[0].Equals("logging", StringComparison.OrdinalIgnoreCase))
        {
            return path[1].Equals("minimumLevel", StringComparison.OrdinalIgnoreCase)
                || path[1].Equals("filePath", StringComparison.OrdinalIgnoreCase);
        }

        if (path.Length == 2 && path[0].Equals("audit", StringComparison.OrdinalIgnoreCase))
        {
            return path[1].Equals("retentionDays", StringComparison.OrdinalIgnoreCase);
        }

        return path.Length == 4
            && path[0].Equals("profiles", StringComparison.OrdinalIgnoreCase)
            && path[2].Equals("limits", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SetValue(JsonObject root, string[] path, string raw)
    {
        JsonObject current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var existing = FindKey(current, path[i]);
            if (existing is null)
            {
                // Solo se crean objetos intermedios bajo un perfil existente (limits) o logging/audit.
                if (i == 0 && path[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (i == 1 && path[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var created = new JsonObject();
                current[path[i]] = created;
                current = created;
                continue;
            }

            if (current[existing] is not JsonObject child)
            {
                return false;
            }

            current = child;
        }

        var leaf = FindKey(current, path[^1]) ?? path[^1];
        current[leaf] = ParseScalar(raw);
        return true;
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? ParseScalar(string raw)
    {
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        if (bool.TryParse(raw, out var b))
        {
            return JsonValue.Create(b);
        }

        return JsonValue.Create(raw);
    }
}
