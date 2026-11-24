using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>
/// Arranca el servidor SqlServerMcp real como proceso stdio y lo controla con el cliente MCP oficial de C#.
/// Las credenciales se pasan por variables de entorno (credentialRef "env:"), nunca en el archivo.
/// </summary>
public sealed class McpServerHarness : IAsyncDisposable
{
    private McpServerHarness(McpClient client, string directory)
    {
        Client = client;
        Directory = directory;
    }

    public McpClient Client { get; }

    /// <summary>Directorio temporal con la configuración y la auditoría de esta instancia.</summary>
    public string Directory { get; }

    public string AuditDirectory => Path.Combine(Directory, "audit");

    /// <summary>Respuesta de elicitation que dará el "usuario" del cliente de pruebas.</summary>
    public Func<ElicitRequestParams?, ElicitResult>? OnElicit { get; set; }

    public List<string> ElicitationMessages { get; } = [];

    public static string ServerAssemblyPath
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "SqlServerMcp.dll");
            if (File.Exists(local) && File.Exists(Path.Combine(AppContext.BaseDirectory, "SqlServerMcp.runtimeconfig.json")))
            {
                return local;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlServerMcp.slnx")))
            {
                dir = dir.Parent;
            }

            var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
            return Path.Combine(dir!.FullName, "src", "SqlServerMcp", "bin", configuration, "net10.0", "SqlServerMcp.dll");
        }
    }

    /// <summary>Configuración de pruebas: perfil "desarrollo" (COMPLETO) y "lectura" (LECTURA) sobre VentasTest.</summary>
    public static JsonObject DefaultConfig(string directory, Action<JsonObject>? customizeDesarrollo = null, bool includeLectura = true)
    {
        JsonObject Connection(string user, string variable) => new()
        {
            ["server"] = IntegrationEnvironment.Server,
            ["database"] = IntegrationEnvironment.Database,
            ["authentication"] = "SqlPassword",
            ["userName"] = user,
            ["credentialRef"] = "env:" + variable,
            ["encrypt"] = "Mandatory",
            ["trustServerCertificate"] = true,
        };

        var desarrollo = new JsonObject
        {
            ["mode"] = "COMPLETO",
            ["environment"] = "Development",
            ["connection"] = Connection("mcp_lector", IntegrationEnvironment.ReaderPasswordVariable),
            ["writeConnection"] = new JsonObject { ["userName"] = "mcp_escritor", ["credentialRef"] = "env:" + IntegrationEnvironment.WriterPasswordVariable },
            ["allowedDatabases"] = new JsonArray(IntegrationEnvironment.Database),
            ["allowedSchemas"] = new JsonArray("ventas", "ia"),
            ["ddlSchemas"] = new JsonArray("ia"),
            ["excluded"] = new JsonObject
            {
                ["schemas"] = new JsonArray("seguridad"),
                ["tables"] = new JsonArray("ventas.TarjetasCredito"),
                ["columns"] = new JsonArray("*.Contrasena"),
            },
            ["limits"] = new JsonObject { ["maxCallsPerMinute"] = 600, ["maxAffectedRows"] = 1000, ["confirmAboveRows"] = 100, ["commandTimeoutSeconds"] = 30 },
            ["policy"] = new JsonObject
            {
                ["rules"] = new JsonArray(new JsonObject { ["id"] = "R-LOCAL-01", ["statement"] = "Delete", ["object"] = "ventas.Facturas", ["action"] = "BLOCK", ["reason"] = "Las facturas no se eliminan" }),
            },
            ["procedures"] = new JsonArray(
                new JsonObject { ["name"] = "ventas.usp_ClientesPorSaldo", ["kind"] = "ReadOnly" },
                new JsonObject { ["name"] = "ventas.usp_DesactivarCliente", ["kind"] = "SideEffects" }),
        };
        customizeDesarrollo?.Invoke(desarrollo);

        var profiles = new JsonObject { ["desarrollo"] = desarrollo };
        if (includeLectura)
        {
            profiles["lectura"] = new JsonObject
            {
                ["mode"] = "LECTURA",
                ["connection"] = Connection("mcp_lector", IntegrationEnvironment.ReaderPasswordVariable),
                ["allowedSchemas"] = new JsonArray("ventas"),
                ["excluded"] = new JsonObject { ["columns"] = new JsonArray("*.Contrasena") },
            };
        }

        return new JsonObject
        {
            ["defaultProfile"] = "desarrollo",
            ["profiles"] = profiles,
            ["audit"] = new JsonObject { ["path"] = Path.Combine(directory, "audit") },
        };
    }

    public static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlmcp-e2e-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static string WriteConfig(string directory, JsonObject config)
    {
        var path = Path.Combine(directory, "sqlservermcp.json");
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static Dictionary<string, string?> Environment() => new()
    {
        [IntegrationEnvironment.ReaderPasswordVariable] = System.Environment.GetEnvironmentVariable(IntegrationEnvironment.ReaderPasswordVariable),
        [IntegrationEnvironment.WriterPasswordVariable] = System.Environment.GetEnvironmentVariable(IntegrationEnvironment.WriterPasswordVariable),
        ["SQLSERVERMCP_TEST_SA_PASSWORD"] = System.Environment.GetEnvironmentVariable("SQLSERVERMCP_TEST_SA_PASSWORD"),
    };

    public static async Task<McpServerHarness> StartAsync(JsonObject? config = null, bool supportsElicitation = true, string? directory = null, string? profileArgument = null)
    {
        directory ??= NewDirectory();
        config ??= DefaultConfig(directory);
        var configPath = WriteConfig(directory, config);
        var args = new List<string> { ServerAssemblyPath, "--config", configPath };
        if (profileArgument is not null)
        {
            args.AddRange(["--profile", profileArgument]);
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sqlservermcp-e2e",
            Command = "dotnet",
            Arguments = args,
            EnvironmentVariables = Environment(),
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        });

        McpServerHarness? harness = null;
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "sqlservermcp-e2e", Version = "1.0.0" },
        };
        if (supportsElicitation)
        {
            options.Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() } };
            options.Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    harness!.ElicitationMessages.Add(request?.Message ?? string.Empty);
                    var answer = harness.OnElicit?.Invoke(request) ?? new ElicitResult { Action = "decline" };
                    return ValueTask.FromResult(answer);
                },
            };
        }

        var client = await McpClient.CreateAsync(transport, options).ConfigureAwait(false);
        harness = new McpServerHarness(client, directory);
        return harness;
    }

    public static ElicitResult Approve() => new()
    {
        Action = "accept",
        Content = new Dictionary<string, JsonElement> { ["aprobar"] = JsonDocument.Parse("true").RootElement },
    };

    public static ElicitResult Decline() => new() { Action = "decline" };

    /// <summary>Invoca una herramienta y devuelve el contenido estructurado (envoltura ToolResponse).</summary>
    public async Task<ToolCall> CallAsync(string tool, object? arguments = null)
    {
        var args = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
        var result = await Client.CallToolAsync(tool, args).ConfigureAwait(false);
        if (result.StructuredContent is { } content)
        {
            await AssertMatchesOutputSchemaAsync(tool, content).ConfigureAwait(false);
        }

        var structured = result.StructuredContent is { } s ? JsonNode.Parse(s.GetRawText())!.AsObject() : new JsonObject();
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        return new ToolCall(result.IsError == true, structured, text);
    }

    private Dictionary<string, Json.Schema.JsonSchema?>? _outputSchemas;

    /// <summary>
    /// Valida structuredContent contra el outputSchema publicado, como hacen los clientes MCP (Claude Code, VS Code…):
    /// si no coincide, el cliente descarta la respuesta completa. Incluye la validación de formatos (date-time, uuid…).
    /// </summary>
    private async Task AssertMatchesOutputSchemaAsync(string tool, JsonElement content)
    {
        if (_outputSchemas is null)
        {
            var tools = await Client.ListToolsAsync().ConfigureAwait(false);
            _outputSchemas = tools.ToDictionary(
                t => t.Name,
                t => t.ProtocolTool.OutputSchema is { } schema ? Json.Schema.JsonSchema.FromText(schema.GetRawText()) : null);
        }

        if (!_outputSchemas.TryGetValue(tool, out var outputSchema) || outputSchema is null)
        {
            return;
        }

        var evaluation = outputSchema.Evaluate(content, new Json.Schema.EvaluationOptions
        {
            OutputFormat = Json.Schema.OutputFormat.List,
            RequireFormatValidation = true,
        });
        if (!evaluation.IsValid)
        {
            var errors = (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}"))
                .Take(20);
            Assert.Fail($"structuredContent de '{tool}' no cumple su outputSchema:\n{string.Join("\n", errors)}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Lee un archivo JSON Lines que el servidor puede estar escribiendo (comparte lectura y escritura).</summary>
    public static IEnumerable<JsonObject> ReadJsonLines(string directory, string pattern)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in System.IO.Directory.GetFiles(directory, pattern))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return JsonNode.Parse(line)!.AsObject();
                }
            }
        }
    }

    /// <summary>Ejecuta el servidor como proceso y devuelve el código de salida (para pruebas de arranque).</summary>
    public static async Task<(int ExitCode, string StdErr)> RunProcessAsync(JsonObject config, string directory, TimeSpan timeout)
    {
        var configPath = WriteConfig(directory, config);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ServerAssemblyPath);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        foreach (var (k, v) in Environment())
        {
            psi.Environment[k] = v;
        }

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, await stderr.ConfigureAwait(false));
        }

        return (process.ExitCode, await stderr.ConfigureAwait(false));
    }
}

/// <summary>Resultado de una invocación: indicador de error, contenido estructurado y texto.</summary>
public sealed record ToolCall(bool IsError, JsonObject Structured, string Text)
{
    public bool Ok => Structured["ok"]?.GetValue<bool>() == true;

    public string? ErrorCode => Structured["error"]?["code"]?.GetValue<string>();

    public string? RuleId => Structured["error"]?["ruleId"]?.GetValue<string>();

    public string? ErrorMessage => Structured["error"]?["message"]?.GetValue<string>();

    public JsonNode? Data => Structured["data"];

    public string CorrelationId => Structured["correlationId"]!.GetValue<string>();

    public override string ToString() => Structured.ToJsonString();
}
