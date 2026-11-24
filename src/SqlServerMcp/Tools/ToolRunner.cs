using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;
using SqlServerMcp.Security;

namespace SqlServerMcp.Tools;

/// <summary>Requisitos de una herramienta para el pipeline común.</summary>
/// <param name="RequiredMode">Modo mínimo del perfil (RF-TOOL-02).</param>
/// <param name="IsWrite">Herramienta de escritura: se suspende ante intentos críticos (RS-DCL-04) y exige auditoría previa (RS-AUD-03).</param>
/// <param name="Format">Formato del texto devuelto (RF-QRY-11).</param>
/// <param name="RequiresConnection">Si requiere un perfil verificado (conexión y permisos).</param>
/// <param name="CountsAgainstConcurrency">Si ocupa un turno de ejecución concurrente.</param>
public sealed record ToolSpec(
    OperationMode RequiredMode,
    bool IsWrite = false,
    OutputFormat Format = OutputFormat.Json,
    bool RequiresConnection = true,
    bool CountsAgainstConcurrency = true);

/// <summary>Resultado de un manejador de herramienta.</summary>
public sealed class ToolOutcome<TData>
    where TData : class
{
    public TData? Data { get; init; }

    public long? RowsReturned { get; init; }

    public long? RowsAffected { get; init; }

    public bool Truncated { get; init; }

    /// <summary>Base de datos a informar si difiere de la inicial del perfil.</summary>
    public string? Database { get; init; }

    /// <summary>Tabla principal para el formato Markdown/CSV y la detección de texto sospechoso.</summary>
    public TabularResult? Table { get; init; }

    /// <summary>Error "suave" (por ejemplo LIMIT_EXCEEDED tras revertir) que se devuelve junto con los datos.</summary>
    public ToolError? Error { get; init; }

    public static implicit operator ToolOutcome<TData>(TData data) => new() { Data = data };
}

/// <summary>Contexto de una invocación: correlación, perfil y datos para la auditoría.</summary>
public sealed class ToolContext
{
    private readonly ToolRunner _runner;

    internal ToolContext(ToolRunner runner, McpServer server, string toolName, ProfileRuntime profile, string correlationId, ToolSpec spec)
    {
        _runner = runner;
        Server = server;
        ToolName = toolName;
        Profile = profile;
        CorrelationId = correlationId;
        Spec = spec;
    }

    public McpServer Server { get; }

    public string ToolName { get; }

    public ProfileRuntime Profile { get; }

    public string CorrelationId { get; }

    public ToolSpec Spec { get; }

    public List<string> Warnings { get; } = [];

    public string? Statement { get; set; }

    public string? StatementHash { get; set; }

    public IReadOnlyList<string>? ParameterNames { get; set; }

    public string? Category { get; set; }

    public string? Decision { get; set; }

    public string? RuleId { get; set; }

    public string? Reason { get; set; }

    public string? Confirmation { get; set; }

    public DateTimeOffset? ConfirmationAtUtc { get; set; }

    public long? EstimatedRows { get; set; }

    internal bool PreExecutionAudited { get; private set; }

    /// <summary>Copia la decisión de política al contexto de auditoría y agrega sus advertencias.</summary>
    public void ApplyEvaluation(PolicyEvaluation evaluation)
    {
        Statement = string.IsNullOrEmpty(evaluation.Statement.NormalizedSql) ? Statement : evaluation.Statement.NormalizedSql;
        StatementHash = string.IsNullOrEmpty(evaluation.Statement.Hash) ? StatementHash : evaluation.Statement.Hash;
        Category = evaluation.Statement.Category.ToString();
        Decision = evaluation.Action.ToString().ToUpperInvariant();
        RuleId = evaluation.RuleId;
        Reason = evaluation.Reason;
        foreach (var w in evaluation.Warnings)
        {
            if (!Warnings.Contains(w))
            {
                Warnings.Add(w);
            }
        }
    }

    public void SetParameters(IEnumerable<QueryParameter> parameters) =>
        ParameterNames = parameters.Select(p => p.Name.StartsWith('@') ? p.Name : "@" + p.Name).ToList();

    /// <summary>
    /// Registro de auditoría previo a una operación de escritura. Si no se puede escribir, la operación se rechaza
    /// (fail-closed, RS-AUD-03, CA-27).
    /// </summary>
    public async Task WritePreExecutionAuditAsync(CancellationToken cancellationToken)
    {
        await _runner.WriteAuditAsync(this, "pre_execution", outcome: "pending", null, null, null, null, null, cancellationToken, throwOnFailure: true).ConfigureAwait(false);
        PreExecutionAudited = true;
    }
}

/// <summary>
/// Pipeline común de todas las herramientas: correlación, perfil, modo, suspensión, límites de tasa, verificación del
/// perfil, mapeo seguro de errores, auditoría y construcción de la respuesta MCP (RF-OUT-01..04, RS-AUD-01).
/// </summary>
public sealed class ToolRunner
{
    private static readonly int[] ConnectionErrorNumbers = [-1, 2, 53, 64, 233, 4060, 10053, 10054, 10060, 10061, 11001, 18452, 18456, 40613, 40615, 42108, 42109];

    private readonly LoadedConfiguration _config;
    private readonly IProfileVerifier _verifier;
    private readonly IAuditLog _audit;
    private readonly IAlertService _alerts;
    private readonly ISecurityMonitor _monitor;
    private readonly CallRateLimiter _limiter;
    private readonly ISqlConnectionFactory _connections;
    private readonly SessionInfo _session;
    private readonly ILogger<ToolRunner> _logger;

    public ToolRunner(
        LoadedConfiguration config,
        IProfileVerifier verifier,
        IAuditLog audit,
        IAlertService alerts,
        ISecurityMonitor monitor,
        CallRateLimiter limiter,
        ISqlConnectionFactory connections,
        SessionInfo session,
        ILogger<ToolRunner> logger)
    {
        _config = config;
        _verifier = verifier;
        _audit = audit;
        _alerts = alerts;
        _monitor = monitor;
        _limiter = limiter;
        _connections = connections;
        _session = session;
        _logger = logger;
    }

    /// <summary>Resuelve el perfil indicado por el agente; solo perfiles configurados (RF-CON-04).</summary>
    public ProfileRuntime ResolveProfile(string? name)
    {
        var key = string.IsNullOrWhiteSpace(name) ? _config.DefaultProfile : name.Trim();
        if (_config.Profiles.TryGetValue(key, out var profile))
        {
            return profile;
        }

        throw new ToolFailureException(ErrorCodes.ProfileNotFound,
            $"El perfil '{Truncate(key, 64)}' no existe o no está habilitado.",
            $"Perfiles disponibles: {string.Join(", ", _config.Profiles.Keys)}. Consulte get_server_policy.");
    }

    public async Task<CallToolResult> RunAsync<TData>(
        McpServer server,
        string toolName,
        string? profileName,
        ToolSpec spec,
        Func<ToolContext, Task<ToolOutcome<TData>>> handler,
        CancellationToken cancellationToken)
        where TData : class
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = NewCorrelationId();
        ToolContext? context = null;
        ProfileRuntime? profile = null;
        _monitor.IncrementMetric("calls." + toolName);

        try
        {
            profile = ResolveProfile(profileName);
            context = new ToolContext(this, server, toolName, profile, correlationId, spec);

            if (!profile.ModeAllows(spec.RequiredMode))
            {
                throw new ToolFailureException(ErrorCodes.ModeNotAllowed,
                    $"La herramienta '{toolName}' requiere el modo {ModeName(spec.RequiredMode)} y el perfil '{profile.Name}' está en modo {ModeName(profile.Mode)}.",
                    "Use un perfil con el modo adecuado (consulte get_server_policy).");
            }

            if (spec.IsWrite && _monitor.IsWriteSuspended)
            {
                throw new ToolFailureException(ErrorCodes.SessionSuspended, _monitor.SuspensionReason ?? "Las herramientas de escritura están suspendidas.",
                    "Un operador debe revisar las alertas y reiniciar el servidor MCP.");
            }

            _limiter.RegisterCall(profile);
            using var slot = spec.CountsAgainstConcurrency ? await _limiter.AcquireAsync(profile, cancellationToken).ConfigureAwait(false) : null;

            if (spec.RequiresConnection)
            {
                await _verifier.EnsureVerifiedAsync(profile, cancellationToken).ConfigureAwait(false);
            }

            var outcome = await handler(context).ConfigureAwait(false);
            stopwatch.Stop();
            _monitor.IncrementMetric("latency_ms_total." + toolName, stopwatch.ElapsedMilliseconds);

            var warnings = context.Warnings;
            if (outcome.Table is not null && PromptInjectionDetector.ContainsSuspiciousText(outcome.Table.Rows))
            {
                warnings.Add(PromptInjectionDetector.Warning);
                _monitor.IncrementMetric("prompt_injection_flags");
            }

            var response = new ToolResponse<TData>
            {
                Ok = outcome.Error is null,
                CorrelationId = correlationId,
                Profile = profile.Name,
                Database = outcome.Database ?? profile.InitialDatabase,
                DurationMs = stopwatch.ElapsedMilliseconds,
                RowsReturned = outcome.RowsReturned,
                RowsAffected = outcome.RowsAffected,
                Truncated = outcome.Truncated,
                Warnings = warnings,
                Data = outcome.Data,
                Error = outcome.Error,
            };

            await WriteAuditAsync(context, "invocation", outcome.Error is null ? "success" : "rolled_back",
                outcome.RowsReturned, outcome.RowsAffected, stopwatch.ElapsedMilliseconds, outcome.Error?.Code, null, cancellationToken,
                throwOnFailure: false).ConfigureAwait(false);

            return BuildResult(response, spec.Format, outcome.Table);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (context is not null)
            {
                await WriteAuditAsync(context, "invocation", "cancelled", null, null, stopwatch.ElapsedMilliseconds, null, null, CancellationToken.None, false).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var error = MapException(ex, toolName, correlationId);
            if (context is not null)
            {
                var outcome = error.Code switch
                {
                    ErrorCodes.PolicyBlocked or ErrorCodes.LimitExceeded or ErrorCodes.ModeNotAllowed or ErrorCodes.SessionSuspended => "blocked",
                    ErrorCodes.ConfirmationDenied or ErrorCodes.ConfirmationUnavailable or ErrorCodes.ConfirmationExpired => "denied",
                    _ => "error",
                };
                await WriteAuditAsync(context, "invocation", outcome, null, null, stopwatch.ElapsedMilliseconds, error.Code,
                    error.Sql?.Number, CancellationToken.None, throwOnFailure: false).ConfigureAwait(false);
            }

            if (ex is ToolFailureException { Alert: { } severity } failure)
            {
                await RaiseAlertAsync(severity, failure.RuleId ?? failure.Code, failure.Message, context, profile).ConfigureAwait(false);
                if (severity == AlertSeverity.Critical && failure.Code == ErrorCodes.PolicyBlocked && profile is not null)
                {
                    _monitor.RecordCriticalAttempt(profile.Name, failure.RuleId ?? failure.Code, correlationId);
                }
            }

            if (error.Code == ErrorCodes.PolicyBlocked)
            {
                _monitor.IncrementMetric("blocked." + (error.RuleId ?? "unknown"));
            }

            var response = new ToolResponse<TData>
            {
                Ok = false,
                CorrelationId = correlationId,
                Profile = profile?.Name,
                Database = profile?.InitialDatabase,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Warnings = context?.Warnings ?? [],
                Error = error,
            };
            return BuildResult(response, OutputFormat.Json, null);
        }
    }

    internal async Task RaiseAlertAsync(AlertSeverity severity, string code, string message, ToolContext? context, ProfileRuntime? profile)
    {
        try
        {
            await _alerts.RaiseAsync(new AlertEvent
            {
                Severity = severity,
                Code = code,
                Message = message,
                CorrelationId = context?.CorrelationId,
                Profile = profile?.Name ?? context?.Profile.Name,
                Tool = context?.ToolName,
                Statement = context?.Statement is { } s ? Truncate(s, 4000) : null,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuditWriteException or IOException)
        {
            _logger.LogError("No fue posible emitir la alerta {Code} ({Type}).", code, ex.GetType().Name);
        }
    }

    internal async Task WriteAuditAsync(
        ToolContext context,
        string eventName,
        string outcome,
        long? rowsReturned,
        long? rowsAffected,
        long? durationMs,
        string? errorCode,
        int? sqlErrorNumber,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        var record = new AuditRecord
        {
            Event = eventName,
            CorrelationId = context.CorrelationId,
            SessionId = context.Server.SessionId ?? _session.SessionId,
            ClientName = context.Server.ClientInfo?.Name,
            ClientVersion = context.Server.ClientInfo?.Version,
            OsUser = _session.OsUser,
            Profile = context.Profile.Name,
            Server = _connections.DescribeTarget(context.Profile),
            Database = context.Profile.InitialDatabase,
            Tool = context.ToolName,
            Statement = context.Statement is { } s ? Truncate(s, 16_384) : null,
            StatementHash = context.StatementHash,
            ParameterNames = context.ParameterNames,
            Category = context.Category,
            Decision = context.Decision,
            RuleId = context.RuleId,
            Reason = context.Reason,
            Confirmation = context.Confirmation,
            ConfirmationAtUtc = context.ConfirmationAtUtc,
            EstimatedRows = context.EstimatedRows,
            RowsReturned = rowsReturned,
            RowsAffected = rowsAffected,
            DurationMs = durationMs,
            Outcome = outcome,
            ErrorCode = errorCode,
            SqlErrorNumber = sqlErrorNumber,
        };

        try
        {
            await _audit.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditWriteException ex)
        {
            _logger.LogError("No fue posible escribir la auditoría de {Tool} (correlación {CorrelationId}): {Reason}", context.ToolName, context.CorrelationId, ex.Message);
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    private ToolError MapException(Exception ex, string toolName, string correlationId)
    {
        switch (ex)
        {
            case ToolFailureException f:
                return new ToolError
                {
                    Code = f.Code,
                    Message = f.Message,
                    RuleId = f.RuleId,
                    Suggestion = f.Suggestion,
                    Details = f.Details.Count > 0 ? f.Details : null,
                };
            case SqlException sql:
                return MapSqlException(sql);
            case AuditWriteException:
                return new ToolError
                {
                    Code = ErrorCodes.AuditUnavailable,
                    Message = "No es posible escribir en el registro de auditoría; las operaciones de escritura se rechazan (fail-closed).",
                    Suggestion = "Un administrador debe revisar el directorio de auditoría (espacio, permisos).",
                };
            case SecretResolutionException secret:
                return new ToolError { Code = ErrorCodes.ConnectionError, Message = "No fue posible obtener la credencial del perfil: " + secret.Message };
            case OperationCanceledException:
                return new ToolError { Code = ErrorCodes.Timeout, Message = "La operación fue cancelada por exceder el tiempo máximo." };
            case TimeoutException:
                return new ToolError { Code = ErrorCodes.Timeout, Message = "La operación superó el tiempo máximo." };
            default:
                _logger.LogError("Error interno en {Tool} (correlación {CorrelationId}): {Type}", toolName, correlationId, ex.GetType().FullName);
                _logger.LogDebug(ex, "Detalle del error interno (correlación {CorrelationId}).", correlationId);
                return new ToolError
                {
                    Code = ErrorCodes.InternalError,
                    Message = $"Error interno del servidor MCP. Correlación: {correlationId}.",
                };
        }
    }

    /// <summary>Convierte un error de SQL Server en un error de herramienta sin exponer secretos (RF-OUT-02, RF-OUT-04).</summary>
    internal static ToolError MapSqlException(SqlException sql)
    {
        var first = sql.Errors.Count > 0 ? sql.Errors[0] : null;
        var number = first?.Number ?? sql.Number;
        var message = SanitizeMessage(first?.Message ?? sql.Message);
        var info = new SqlErrorInfo
        {
            Number = number,
            Severity = first?.Class ?? sql.Class,
            State = first?.State ?? sql.State,
            Line = first?.LineNumber ?? sql.LineNumber,
            Procedure = string.IsNullOrEmpty(first?.Procedure) ? null : first!.Procedure,
            Message = message,
        };

        if (number == -2)
        {
            return new ToolError
            {
                Code = ErrorCodes.Timeout,
                Message = "La sentencia superó el tiempo máximo y fue cancelada en el servidor.",
                Suggestion = "Filtre más los datos, use índices adecuados (get_execution_plan) o pagine el resultado.",
                Sql = info,
            };
        }

        if (number == 1222)
        {
            return new ToolError
            {
                Code = ErrorCodes.Timeout,
                Message = "Se superó el tiempo de espera de bloqueos (LOCK_TIMEOUT).",
                Suggestion = "Reintente más tarde; otra sesión mantiene bloqueos sobre los datos.",
                Sql = info,
            };
        }

        if (ConnectionErrorNumbers.Contains(number) || sql.Class >= 20)
        {
            return new ToolError
            {
                Code = ErrorCodes.ConnectionError,
                Message = $"No fue posible comunicarse con SQL Server (error {number}).",
                Suggestion = "Verifique la disponibilidad del servidor y las credenciales configuradas por el administrador.",
                Sql = new SqlErrorInfo { Number = number, Severity = info.Severity, State = info.State, Line = 0, Message = "Error de conexión o de inicio de sesión." },
            };
        }

        return new ToolError
        {
            Code = ErrorCodes.SqlError,
            Message = message,
            Suggestion = number switch
            {
                208 => "Verifique el nombre del objeto con list_tables o search_objects (incluya el esquema).",
                207 => "Verifique los nombres de columna con describe_table.",
                229 or 230 or 262 or 297 or 300 => "El login del MCP no tiene ese permiso en SQL Server; esto es intencional (mínimo privilegio).",
                102 or 156 => "Revise la sintaxis; puede validarla primero con validate_sql.",
                547 => "La operación viola una restricción (clave foránea o CHECK).",
                2627 or 2601 => "La operación viola una clave única o primaria.",
                _ => null,
            },
            Sql = info,
        };
    }

    internal static string SanitizeMessage(string message)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***");
        return Truncate(sanitized, 2000);
    }

    private static CallToolResult BuildResult<TData>(ToolResponse<TData> response, OutputFormat format, TabularResult? table)
        where TData : class
    {
        var structured = JsonSerializer.SerializeToElement(response, ToolJson.Options);
        string text;
        if (format == OutputFormat.Json || table is null || !response.Ok)
        {
            text = structured.GetRawText();
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("correlationId: ").Append(response.CorrelationId)
              .Append(" | perfil: ").Append(response.Profile)
              .Append(" | base: ").Append(response.Database)
              .Append(" | filas: ").Append(response.RowsReturned?.ToString(CultureInfo.InvariantCulture) ?? "-")
              .Append(" | truncado: ").Append(response.Truncated ? "sí" : "no")
              .Append(" | duración: ").Append(response.DurationMs).AppendLine(" ms");
            foreach (var w in response.Warnings)
            {
                sb.Append("advertencia: ").AppendLine(w);
            }

            sb.AppendLine();
            sb.Append(format == OutputFormat.Csv ? ResultFormatter.ToCsv(table) : ResultFormatter.ToMarkdown(table));
            text = sb.ToString();
        }

        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = text }],
            IsError = !response.Ok,
        };
    }

    private static string NewCorrelationId() => Guid.CreateVersion7().ToString("N");

    private static string ModeName(OperationMode mode) => mode switch
    {
        OperationMode.Completo => "COMPLETO",
        OperationMode.EscrituraDatos => "ESCRITURA_DATOS",
        _ => "LECTURA",
    };

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>Datos de la sesión del proceso (stdio = una sesión por proceso).</summary>
public sealed class SessionInfo
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public string OsUser { get; } = Environment.UserDomainName + "\\" + Environment.UserName;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
}
