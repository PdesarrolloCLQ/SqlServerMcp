using System.Text.RegularExpressions;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Configuration;

public sealed class ConfigValidationResult
{
    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    public Dictionary<string, ProfileRuntime> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? DefaultProfile { get; set; }
}

/// <summary>
/// Validación semántica de la configuración (RF-CFG-01..04). Las reglas inmutables no pueden relajarse:
/// una configuración que lo intente impide el arranque (RS-POL-10, RS-DCL-02).
/// </summary>
public static partial class ConfigValidator
{
    private static readonly string[] RuleStatements =
    [
        "Select", "Insert", "Update", "Delete", "Merge", "Create", "Alter", "Drop", "Truncate", "Execute", "Trigger",
        "Query", "SelectInto", "DdlCreate", "DdlAlter", "DdlDrop", "DdlTruncate", "DdlDestructiveAlter", "DdlTrigger",
        "Statistics", "ExtendedProperty", "ExecuteProcedure", "DynamicSql",
    ];

    private static readonly string[] LogLevels = ["Error", "Warning", "Information", "Debug", "Verbose", "Trace", "Critical"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex("^(wincred|env|dpapi):.{1,512}$")]
    private static partial Regex CredentialRefRegex();

    public static ConfigValidationResult Validate(ServerConfig config, string? forcedProfile = null)
    {
        var r = new ConfigValidationResult();

        if (!string.Equals(config.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            r.Errors.Add("transport: solo se admite \"stdio\" (RS-LOC-01).");
        }

        if (config.Language is not ("es" or "en"))
        {
            r.Errors.Add("language: valores admitidos \"es\" o \"en\".");
        }

        ValidateLimits("absoluteLimits", config.AbsoluteLimits, null, r);
        ValidateAudit(config.Audit, r);
        ValidateAlerts(config.Alerts, r);

        if (!LogLevels.Contains(config.Logging.MinimumLevel, StringComparer.OrdinalIgnoreCase))
        {
            r.Errors.Add($"logging.minimumLevel: valor '{config.Logging.MinimumLevel}' no admitido ({string.Join(", ", LogLevels)}).");
        }

        if (config.Profiles.Count == 0)
        {
            r.Errors.Add("profiles: se requiere al menos un perfil.");
            return r;
        }

        if (forcedProfile is not null && !config.Profiles.ContainsKey(forcedProfile))
        {
            r.Errors.Add($"--profile: el perfil '{forcedProfile}' no existe en la configuración.");
            return r;
        }

        foreach (var (name, profile) in config.Profiles)
        {
            if (profile is null)
            {
                r.Errors.Add($"profiles.{name}: el perfil no puede ser null.");
                continue;
            }

            var active = profile.Enabled && (forcedProfile is null || string.Equals(name, forcedProfile, StringComparison.OrdinalIgnoreCase));
            ValidateProfile(name, profile, config.AbsoluteLimits, r, out var effective);
            if (active && effective is not null)
            {
                r.Profiles[name] = new ProfileRuntime(name, profile, effective);
            }
        }

        if (r.Errors.Count > 0)
        {
            return r;
        }

        if (r.Profiles.Count == 0)
        {
            r.Errors.Add("profiles: no hay ningún perfil habilitado.");
            return r;
        }

        if (forcedProfile is not null)
        {
            r.DefaultProfile = r.Profiles.Keys.Single();
        }
        else if (!string.IsNullOrWhiteSpace(config.DefaultProfile))
        {
            if (!r.Profiles.ContainsKey(config.DefaultProfile))
            {
                r.Errors.Add($"defaultProfile: '{config.DefaultProfile}' no existe o no está habilitado.");
            }
            else
            {
                r.DefaultProfile = r.Profiles.Keys.First(k => string.Equals(k, config.DefaultProfile, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (r.Profiles.Count == 1)
        {
            r.DefaultProfile = r.Profiles.Keys.Single();
        }
        else
        {
            r.Errors.Add("defaultProfile: es obligatorio cuando hay más de un perfil habilitado.");
        }

        var modes = r.Profiles.Values.Select(p => p.Mode).Distinct().ToList();
        if (modes.Count > 1)
        {
            r.Warnings.Add("Hay perfiles habilitados con modos distintos: se publican las herramientas del modo más amplio y cada llamada se valida contra el modo de su perfil. Para producción se recomienda una instancia dedicada con --profile.");
        }

        return r;
    }

    private static void ValidateProfile(string name, ProfileConfig p, LimitsConfig absolute, ConfigValidationResult r, out LimitsConfig? effective)
    {
        effective = null;
        var prefix = $"profiles.{name}";
        var errorsBefore = r.Errors.Count;

        if (!ProfileNameRegex().IsMatch(name))
        {
            r.Errors.Add($"{prefix}: el nombre del perfil solo admite letras, números, '_' y '-' (máximo 64).");
        }

        ValidateConnection($"{prefix}.connection", p.Connection, r);
        if (p.WriteConnection is not null)
        {
            ValidateWriteConnection($"{prefix}.writeConnection", p.WriteConnection, p.Connection, r);
        }

        foreach (var db in p.AllowedDatabases)
        {
            if (!SqlNames.IsValidIdentifier(db))
            {
                r.Errors.Add($"{prefix}.allowedDatabases: '{db}' no es un identificador válido.");
            }
            else if (ProfileRuntime.IsSystemDatabase(SqlNames.Normalize(db)))
            {
                r.Errors.Add($"{prefix}.allowedDatabases: las bases de datos del sistema ('{db}') no pueden permitirse (RS-POL-07).");
            }
        }

        if (p.AllowedDatabases.Count > 0 && !p.AllowedDatabases.Select(SqlNames.Normalize).Contains(p.Connection.Database, StringComparer.OrdinalIgnoreCase))
        {
            r.Warnings.Add($"{prefix}: la base inicial '{p.Connection.Database}' se agregó implícitamente a allowedDatabases.");
        }

        foreach (var s in p.AllowedSchemas.Concat(p.DdlSchemas).Concat(p.Excluded.Schemas))
        {
            if (!SqlNames.IsValidIdentifier(s))
            {
                r.Errors.Add($"{prefix}: el esquema '{s}' no es un identificador válido.");
            }
        }

        foreach (var s in p.AllowedSchemas.Concat(p.DdlSchemas))
        {
            if (ProfileRuntime.IsSystemSchema(SqlNames.Normalize(s)))
            {
                r.Errors.Add($"{prefix}: el esquema del sistema '{s}' no puede permitirse.");
            }
        }

        var allowed = p.AllowedSchemas.Count == 0 ? ["dbo"] : p.AllowedSchemas.Select(SqlNames.Normalize).ToList();
        foreach (var s in p.DdlSchemas.Select(SqlNames.Normalize))
        {
            if (!allowed.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                r.Errors.Add($"{prefix}.ddlSchemas: '{s}' debe estar también en allowedSchemas.");
            }

            if (s.Equals("dbo", StringComparison.OrdinalIgnoreCase))
            {
                r.Warnings.Add($"{prefix}.ddlSchemas: se permite DDL en 'dbo'. Se recomienda un esquema de trabajo con propietario dedicado (RS-PRV-04).");
            }
        }

        if (p.Mode == OperationMode.Completo && p.DdlSchemas.Count == 0)
        {
            r.Warnings.Add($"{prefix}: modo COMPLETO sin ddlSchemas; execute_ddl rechazará todas las sentencias.");
        }

        if (p.Mode != OperationMode.Completo && p.DdlSchemas.Count > 0)
        {
            r.Warnings.Add($"{prefix}: ddlSchemas se ignora porque el modo no es COMPLETO.");
        }

        if (p.Environment == ProfileEnvironment.Production && p.Mode != OperationMode.Lectura)
        {
            r.Warnings.Add($"{prefix}: perfil de producción en modo {p.Mode}. Se recomienda LECTURA (RS-PRV-10).");
        }

        foreach (var t in p.Excluded.Tables)
        {
            if (SqlNames.SplitMultipart(t, 2) is null)
            {
                r.Errors.Add($"{prefix}.excluded.tables: patrón '{t}' inválido (use esquema.objeto).");
            }
        }

        foreach (var c in p.Excluded.Columns.Concat(p.Sensitive.Patterns).Concat(p.Pii.Patterns))
        {
            if (SqlNames.SplitMultipart(c, 3) is null)
            {
                r.Errors.Add($"{prefix}: patrón de columna '{c}' inválido (use [esquema.][tabla.]columna).");
            }
        }

        ValidatePolicy(prefix + ".policy", p, r);

        foreach (var proc in p.Procedures)
        {
            if (SqlNames.SplitMultipart(proc.Name, 2) is not { Count: 2 })
            {
                r.Errors.Add($"{prefix}.procedures: '{proc.Name}' debe tener la forma esquema.procedimiento (admite '*').");
            }
        }

        foreach (var proc in p.DeniedProcedures)
        {
            if (SqlNames.SplitMultipart(proc, 2) is null)
            {
                r.Errors.Add($"{prefix}.deniedProcedures: '{proc}' no es un nombre válido.");
            }
        }

        var limits = p.Limits ?? new LimitsConfig();
        ValidateLimits($"{prefix}.limits", limits, absolute, r);

        if (r.Errors.Count == errorsBefore)
        {
            effective = limits;
        }
    }

    private static void ValidateConnection(string prefix, ConnectionConfig c, ConfigValidationResult r)
    {
        if (string.IsNullOrWhiteSpace(c.Server) || c.Server.Length > 256 || c.Server.Contains(';', StringComparison.Ordinal))
        {
            r.Errors.Add($"{prefix}.server: obligatorio, sin ';' y de hasta 256 caracteres.");
        }

        if (!SqlNames.IsValidIdentifier(c.Database))
        {
            r.Errors.Add($"{prefix}.database: obligatorio y debe ser un identificador válido.");
        }
        else if (ProfileRuntime.IsSystemDatabase(c.Database))
        {
            r.Errors.Add($"{prefix}.database: no se permite conectarse a una base de datos del sistema.");
        }

        ValidateAuth(prefix, c.Authentication, c.UserName, c.CredentialRef, r);

        if (c.Encrypt == EncryptMode.Optional)
        {
            r.Warnings.Add($"{prefix}.encrypt: el cifrado está desactivado (Optional). Se recomienda Mandatory o Strict (RF-CON-06).");
        }

        if (c.TrustServerCertificate)
        {
            r.Warnings.Add($"{prefix}.trustServerCertificate: no se valida el certificado del servidor (RF-CON-06).");
        }

        if (c.Encrypt == EncryptMode.Strict && c.TrustServerCertificate)
        {
            r.Warnings.Add($"{prefix}: con Encrypt=Strict siempre se valida el certificado; trustServerCertificate se ignora.");
        }

        if (string.IsNullOrWhiteSpace(c.ApplicationName) || c.ApplicationName.Length > 128 || c.ApplicationName.Contains(';', StringComparison.Ordinal))
        {
            r.Errors.Add($"{prefix}.applicationName: obligatorio, sin ';' y de hasta 128 caracteres.");
        }

        if (c.ConnectTimeoutSeconds is < 1 or > 300)
        {
            r.Errors.Add($"{prefix}.connectTimeoutSeconds: debe estar entre 1 y 300.");
        }

        if (c.MaxPoolSize is < 1 or > 200)
        {
            r.Errors.Add($"{prefix}.maxPoolSize: debe estar entre 1 y 200.");
        }

        if (c.Retry.MaxRetries is < 0 or > 10 || c.Retry.DeltaSeconds is < 1 or > 60 || c.Retry.MaxSeconds is < 1 or > 300)
        {
            r.Errors.Add($"{prefix}.retry: maxRetries 0-10, deltaSeconds 1-60, maxSeconds 1-300.");
        }
    }

    private static void ValidateWriteConnection(string prefix, WriteConnectionConfig w, ConnectionConfig main, ConfigValidationResult r)
    {
        var auth = w.Authentication ?? main.Authentication;
        ValidateAuth(prefix, auth, w.UserName, w.CredentialRef, r);
        if (auth == main.Authentication
            && string.Equals(w.UserName ?? main.UserName, main.UserName, StringComparison.OrdinalIgnoreCase)
            && auth != AuthenticationKind.WindowsIntegrated)
        {
            r.Warnings.Add($"{prefix}: el login de escritura es el mismo que el de lectura; no hay separación de privilegios (RF-CON-07).");
        }

        if (w.ApplicationName is not null && (w.ApplicationName.Length > 128 || w.ApplicationName.Contains(';', StringComparison.Ordinal)))
        {
            r.Errors.Add($"{prefix}.applicationName: sin ';' y de hasta 128 caracteres.");
        }
    }

    private static void ValidateAuth(string prefix, AuthenticationKind auth, string? user, string? credentialRef, ConfigValidationResult r)
    {
        if (credentialRef is not null && !CredentialRefRegex().IsMatch(credentialRef))
        {
            r.Errors.Add($"{prefix}.credentialRef: debe tener la forma wincred:<destino>, env:<VARIABLE> o dpapi:<ruta>. Nunca escriba el secreto en el archivo (RS-SEC-02).");
        }

        if (user is not null && (user.Length > 256 || user.Contains(';', StringComparison.Ordinal)))
        {
            r.Errors.Add($"{prefix}.userName: sin ';' y de hasta 256 caracteres.");
        }

        switch (auth)
        {
            case AuthenticationKind.SqlPassword:
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(credentialRef))
                {
                    r.Errors.Add($"{prefix}: SqlPassword requiere userName y credentialRef.");
                }

                break;
            case AuthenticationKind.EntraServicePrincipal:
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(credentialRef))
                {
                    r.Errors.Add($"{prefix}: EntraServicePrincipal requiere userName (client id) y credentialRef (secreto).");
                }

                break;
            case AuthenticationKind.WindowsIntegrated:
                if (!string.IsNullOrWhiteSpace(credentialRef))
                {
                    r.Errors.Add($"{prefix}: WindowsIntegrated no usa credentialRef.");
                }

                break;
        }
    }

    private static void ValidatePolicy(string prefix, ProfileConfig p, ConfigValidationResult r)
    {
        var pol = p.Policy;

        // Pisos de las reglas configurables (sección 3): no pueden relajarse por debajo del mínimo.
        RequireAtLeast($"{prefix}.unfilteredDml", pol.UnfilteredDml, PolicyAction.Confirm, r);
        RequireAtLeast($"{prefix}.dynamicSql", pol.DynamicSql, PolicyAction.Confirm, r);
        RequireAtLeast($"{prefix}.dropAction", pol.DropAction, PolicyAction.Confirm, r);
        RequireAtLeast($"{prefix}.truncateAction", pol.TruncateAction, PolicyAction.Confirm, r);
        RequireAtLeast($"{prefix}.destructiveAlterAction", pol.DestructiveAlterAction, PolicyAction.Confirm, r);
        RequireAtLeast($"{prefix}.triggerAction", pol.TriggerAction, PolicyAction.Alert, r);

        if (pol.ConfirmationUnavailable == ConfirmationUnavailableAction.AllowWithAlert && p.Environment != ProfileEnvironment.Development)
        {
            r.Errors.Add($"{prefix}.confirmationUnavailable: AllowWithAlert solo se admite en perfiles con environment = Development (RS-HIL-05).");
        }

        if (pol.AllowEncryptedModules)
        {
            r.Warnings.Add($"{prefix}.allowEncryptedModules: los módulos cifrados no se pueden auditar (RF-DDL-03).");
        }

        if (pol.CriticalAttemptsThreshold is < 1 or > 100 || pol.CriticalAttemptsWindowMinutes is < 1 or > 1440)
        {
            r.Errors.Add($"{prefix}: criticalAttemptsThreshold 1-100 y criticalAttemptsWindowMinutes 1-1440.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in pol.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64)
            {
                r.Errors.Add($"{prefix}.rules: cada regla requiere un id de hasta 64 caracteres.");
            }
            else if (!ids.Add(rule.Id))
            {
                r.Errors.Add($"{prefix}.rules: id duplicado '{rule.Id}'.");
            }
            else if (rule.Id.StartsWith("POL-", StringComparison.OrdinalIgnoreCase))
            {
                r.Errors.Add($"{prefix}.rules: el prefijo 'POL-' está reservado para las reglas internas ('{rule.Id}').");
            }

            if (string.IsNullOrWhiteSpace(rule.Reason))
            {
                r.Errors.Add($"{prefix}.rules[{rule.Id}]: 'reason' es obligatorio para que la decisión sea explicable (RS-POL-11).");
            }

            foreach (var st in rule.Statements.Append(rule.Statement).OfType<string>())
            {
                if (!RuleStatements.Contains(st, StringComparer.OrdinalIgnoreCase))
                {
                    r.Errors.Add($"{prefix}.rules[{rule.Id}]: tipo de sentencia '{st}' no reconocido ({string.Join(", ", RuleStatements)}).");
                }
            }

            if (rule.Object is not null && SqlNames.SplitMultipart(rule.Object, 2) is null)
            {
                r.Errors.Add($"{prefix}.rules[{rule.Id}]: object '{rule.Object}' inválido (use esquema.objeto).");
            }
        }
    }

    private static void RequireAtLeast(string path, PolicyAction value, PolicyAction minimum, ConfigValidationResult r)
    {
        if (value < minimum)
        {
            r.Errors.Add($"{path}: el valor mínimo permitido es {minimum.ToString().ToUpperInvariant()}; no se puede relajar a {value.ToString().ToUpperInvariant()}.");
        }
    }

    private static void ValidateLimits(string prefix, LimitsConfig l, LimitsConfig? absolute, ConfigValidationResult r)
    {
        void Check(string field, long value, long min, long? max)
        {
            if (value < min)
            {
                r.Errors.Add($"{prefix}.{field}: debe ser mayor o igual a {min}.");
            }
            else if (max is not null && value > max)
            {
                r.Errors.Add($"{prefix}.{field}: {value} supera el máximo absoluto configurado ({max}).");
            }
        }

        Check("commandTimeoutSeconds", l.CommandTimeoutSeconds, 1, absolute?.CommandTimeoutSeconds);
        Check("lockTimeoutMs", l.LockTimeoutMs, 0, absolute?.LockTimeoutMs);
        Check("maxRows", l.MaxRows, 1, absolute?.MaxRows);
        Check("maxResponseBytes", l.MaxResponseBytes, 1024, absolute?.MaxResponseBytes);
        Check("maxCellChars", l.MaxCellChars, 16, absolute?.MaxCellChars);
        Check("maxAffectedRows", l.MaxAffectedRows, 1, absolute?.MaxAffectedRows);
        Check("confirmAboveRows", l.ConfirmAboveRows, 0, absolute?.ConfirmAboveRows);
        Check("maxInsertRows", l.MaxInsertRows, 1, absolute?.MaxInsertRows);
        Check("maxStatementBytes", l.MaxStatementBytes, 256, absolute?.MaxStatementBytes);
        Check("maxConcurrentQueries", l.MaxConcurrentQueries, 1, absolute?.MaxConcurrentQueries);
        Check("maxCallsPerMinute", l.MaxCallsPerMinute, 1, absolute?.MaxCallsPerMinute);
        Check("transactionIdleSeconds", l.TransactionIdleSeconds, 5, absolute?.TransactionIdleSeconds);
        Check("sampleMaxRows", l.SampleMaxRows, 1, absolute?.SampleMaxRows);
        Check("confirmationExpirySeconds", l.ConfirmationExpirySeconds, 10, absolute?.ConfirmationExpirySeconds);
        Check("affectedRowsMarginPercent", l.AffectedRowsMarginPercent, 0, absolute?.AffectedRowsMarginPercent);
        Check("largeTableRows", l.LargeTableRows, 1, absolute?.LargeTableRows);

        if (l.ConfirmAboveEstimatedCost is < 0)
        {
            r.Errors.Add($"{prefix}.confirmAboveEstimatedCost: no puede ser negativo.");
        }

        if (absolute is not null && l.ConfirmAboveRows > l.MaxAffectedRows)
        {
            r.Warnings.Add($"{prefix}: confirmAboveRows ({l.ConfirmAboveRows}) es mayor que maxAffectedRows ({l.MaxAffectedRows}); nunca se pedirá confirmación por filas.");
        }
    }

    private static void ValidateAudit(AuditConfig a, ConfigValidationResult r)
    {
        if (string.IsNullOrWhiteSpace(a.Path))
        {
            r.Errors.Add("audit.path: obligatorio.");
        }

        if (a.RetentionDays is < 1 or > 3650)
        {
            r.Errors.Add("audit.retentionDays: debe estar entre 1 y 3650.");
        }

        if (a.MaxFileBytes < 1024 * 1024)
        {
            r.Errors.Add("audit.maxFileBytes: mínimo 1 MB.");
        }

        if (!a.HashChain)
        {
            r.Warnings.Add("audit.hashChain: desactivado; no se podrá verificar la integridad del registro (RS-AUD-08).");
        }
    }

    private static void ValidateAlerts(AlertsConfig a, ConfigValidationResult r)
    {
        if (a.WebhookRef is not null && !CredentialRefRegex().IsMatch(a.WebhookRef))
        {
            r.Errors.Add("alerts.webhookRef: debe ser una referencia wincred:/env:/dpapi:, nunca la URL en claro.");
        }

        if (string.IsNullOrWhiteSpace(a.EventLogSource) || a.EventLogSource.Length > 128)
        {
            r.Errors.Add("alerts.eventLogSource: obligatorio y de hasta 128 caracteres.");
        }
    }
}
