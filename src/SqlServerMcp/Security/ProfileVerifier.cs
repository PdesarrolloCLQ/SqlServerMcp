using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Security;

/// <summary>El servidor no debe iniciar: algún login tiene privilegios peligrosos (RS-DCL-05, CA-22).</summary>
public sealed class StartupSecurityException(IReadOnlyList<string> reasons)
    : Exception("Inicio rechazado por seguridad: " + string.Join(" | ", reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Verificación de conexión y permisos efectivos de los logins de cada perfil (RF-CON-09, RS-DCL-05, RS-PRV-06).</summary>
public interface IProfileVerifier
{
    /// <summary>
    /// Prueba la conexión de cada perfil habilitado y verifica sus permisos. Lanza <see cref="StartupSecurityException"/>
    /// si detecta privilegios peligrosos. Si un servidor no responde, el perfil queda sin verificar y se reintenta en el primer uso.
    /// </summary>
    Task VerifyAllAtStartupAsync(CancellationToken cancellationToken);

    /// <summary>Garantiza que el perfil está verificado antes de usarlo; si no lo está, lo verifica ahora (fail-closed).</summary>
    Task EnsureVerifiedAsync(ProfileRuntime profile, CancellationToken cancellationToken);
}

public sealed class ProfileVerifier : IProfileVerifier
{
    internal const string PrivilegeQuery = """
        SELECT
            SUSER_SNAME() AS login_name,
            USER_NAME() AS user_name,
            DB_NAME() AS database_name,
            CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS product_version,
            CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
            CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
            ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS is_sysadmin,
            ISNULL(IS_SRVROLEMEMBER('securityadmin'), 0) AS is_securityadmin,
            ISNULL(IS_SRVROLEMEMBER('serveradmin'), 0) AS is_serveradmin,
            ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER'), 0) AS has_control_server,
            ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'IMPERSONATE ANY LOGIN'), 0) AS has_impersonate_any_login,
            ISNULL(IS_ROLEMEMBER('db_owner'), 0) AS is_db_owner,
            ISNULL(IS_ROLEMEMBER('db_securityadmin'), 0) AS is_db_securityadmin,
            ISNULL(IS_ROLEMEMBER('db_accessadmin'), 0) AS is_db_accessadmin,
            ISNULL(IS_ROLEMEMBER('db_ddladmin'), 0) AS is_db_ddladmin,
            ISNULL(IS_ROLEMEMBER('db_datawriter'), 0) AS is_db_datawriter,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL'), 0) AS has_db_control,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY USER'), 0) AS has_alter_any_user,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY ROLE'), 0) AS has_alter_any_role,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY APPLICATION ROLE'), 0) AS has_alter_any_app_role,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'), 0) AS has_db_alter,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY SCHEMA'), 0) AS has_alter_any_schema,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'), 0) AS has_db_insert,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE'), 0) AS has_db_update,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE'), 0) AS has_db_delete,
            ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE SYNONYM'), 0) AS has_create_synonym,
            ISNULL(HAS_PERMS_BY_NAME('sys.sp_executesql', 'OBJECT', 'EXECUTE'), 0) AS can_execute_sp_executesql,
            (SELECT COUNT(*) FROM sys.database_principals dp
               WHERE dp.principal_id <> DATABASE_PRINCIPAL_ID() AND dp.type IN ('S','U','G','E','X','C','K')
                 AND HAS_PERMS_BY_NAME(dp.name, 'USER', 'IMPERSONATE') = 1) AS impersonable_users,
            (SELECT COUNT(*) FROM sys.server_principals sp
               WHERE sp.principal_id <> SUSER_ID() AND sp.type IN ('S','U','G','E','X','C','K')
                 AND HAS_PERMS_BY_NAME(sp.name, 'LOGIN', 'IMPERSONATE') = 1) AS impersonable_logins;
        """;

    internal const string InstanceConfigQuery = """
        SELECT name, CAST(value_in_use AS int) AS value_in_use
        FROM sys.configurations
        WHERE name IN ('xp_cmdshell', 'Ole Automation Procedures', 'Ad Hoc Distributed Queries', 'Database Mail XPs',
                       'clr enabled', 'clr strict security', 'cross db ownership chaining');
        SELECT CAST(is_trustworthy_on AS int) AS is_trustworthy_on, CAST(is_db_chaining_on AS int) AS is_db_chaining_on
        FROM sys.databases WHERE name = DB_NAME();
        """;

    private readonly LoadedConfiguration _config;
    private readonly ISqlConnectionFactory _connections;
    private readonly IAuditLog _audit;
    private readonly IAlertService _alerts;
    private readonly ILogger<ProfileVerifier> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileVerifier(LoadedConfiguration config, ISqlConnectionFactory connections, IAuditLog audit, IAlertService alerts, ILogger<ProfileVerifier> logger)
    {
        _config = config;
        _connections = connections;
        _audit = audit;
        _alerts = alerts;
        _logger = logger;
    }

    public async Task VerifyAllAtStartupAsync(CancellationToken cancellationToken)
    {
        var fatal = new List<string>();
        foreach (var profile in _config.Profiles.Values)
        {
            try
            {
                var problems = await VerifyProfileAsync(profile, cancellationToken).ConfigureAwait(false);
                if (problems.Count > 0)
                {
                    fatal.AddRange(problems);
                }
            }
            catch (SqlException ex)
            {
                // Servidor inaccesible: el perfil queda sin verificar y se reintenta en el primer uso.
                _logger.LogWarning("Perfil {Profile}: no fue posible conectar al iniciar ({Target}, error SQL {Number}). Se verificará en el primer uso.",
                    profile.Name, _connections.DescribeTarget(profile), ex.Number);
            }
            catch (Exception ex) when (ex is SecretResolutionException or InvalidOperationException or TimeoutException)
            {
                _logger.LogWarning("Perfil {Profile}: no fue posible conectar al iniciar ({Reason}). Se verificará en el primer uso.",
                    profile.Name, ex is SecretResolutionException ? ex.Message : ex.GetType().Name);
            }
        }

        if (fatal.Count > 0)
        {
            throw new StartupSecurityException(fatal);
        }
    }

    public async Task EnsureVerifiedAsync(ProfileRuntime profile, CancellationToken cancellationToken)
    {
        if (profile.Status == ProfileStatus.Verified)
        {
            return;
        }

        if (profile.Status == ProfileStatus.Rejected)
        {
            throw new ToolFailureException(ErrorCodes.ProfileUnavailable,
                $"El perfil '{profile.Name}' fue deshabilitado por seguridad: el login tiene privilegios no permitidos. Un administrador debe corregir los permisos y reiniciar el servidor MCP.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (profile.Status == ProfileStatus.Verified)
            {
                return;
            }

            IReadOnlyList<string> problems;
            try
            {
                problems = await VerifyProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw new ToolFailureException(ErrorCodes.ConnectionError,
                    $"No fue posible conectar con el perfil '{profile.Name}' (error SQL {ex.Number}). Verifique que el servidor esté disponible.");
            }
            catch (SecretResolutionException ex)
            {
                throw new ToolFailureException(ErrorCodes.ConnectionError, $"No fue posible obtener la credencial del perfil '{profile.Name}': {ex.Message}");
            }

            if (problems.Count > 0)
            {
                throw new ToolFailureException(ErrorCodes.ProfileUnavailable,
                    $"El perfil '{profile.Name}' fue deshabilitado por seguridad: el login tiene privilegios no permitidos.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Devuelve la lista de problemas fatales (vacía si el perfil es seguro) y actualiza el estado del perfil.</summary>
    private async Task<IReadOnlyList<string>> VerifyProfileAsync(ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var roles = new List<ConnectionRole> { ConnectionRole.Read };
        if (profile.HasSeparateWriter && profile.Mode != OperationMode.Lectura)
        {
            roles.Add(ConnectionRole.Write);
        }

        var fatal = new List<string>();
        foreach (var role in roles)
        {
            await using var connection = await _connections.OpenAsync(profile, role, "startup-verification", cancellationToken).ConfigureAwait(false);
            var problems = await CheckPrivilegesAsync(profile, role, connection, cancellationToken).ConfigureAwait(false);
            fatal.AddRange(problems);
            if (role == ConnectionRole.Read)
            {
                await CheckInstanceConfigurationAsync(profile, connection, cancellationToken).ConfigureAwait(false);
            }
        }

        if (fatal.Count > 0)
        {
            profile.Status = ProfileStatus.Rejected;
            profile.RejectionReason = string.Join(" ", fatal);
            foreach (var reason in fatal)
            {
                _logger.LogCritical("{Reason}", reason);
            }

            await _alerts.RaiseAsync(new AlertEvent
            {
                Severity = AlertSeverity.Critical,
                Code = "PRIVILEGED_LOGIN",
                Message = profile.RejectionReason,
                Profile = profile.Name,
            }, cancellationToken).ConfigureAwait(false);
            await TryAuditAsync(profile, "rejected", profile.RejectionReason, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            profile.Status = ProfileStatus.Verified;
            _logger.LogInformation("Perfil {Profile} verificado ({Target}, SQL Server {Version}, modo {Mode}).",
                profile.Name, _connections.DescribeTarget(profile), profile.ServerVersion, profile.Mode);
            await TryAuditAsync(profile, "verified", null, cancellationToken).ConfigureAwait(false);
        }

        return fatal;
    }

    private async Task<IReadOnlyList<string>> CheckPrivilegesAsync(ProfileRuntime profile, ConnectionRole role, SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PrivilegeQuery;
        command.CommandTimeout = Math.Max(15, profile.Limits.CommandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [$"Perfil '{profile.Name}' ({role}): no fue posible leer los permisos efectivos del login (fail-closed)."];
        }

        int Flag(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? 0 : Convert.ToInt32(reader[column], System.Globalization.CultureInfo.InvariantCulture);

        var login = reader["login_name"] as string ?? "?";
        profile.ServerVersion = $"{reader["product_version"]} {reader["edition"]}";

        var fatal = new List<string>();
        void Fatal(bool condition, string what)
        {
            if (condition)
            {
                fatal.Add($"Perfil '{profile.Name}' ({(role == ConnectionRole.Read ? "lector" : "escritor")}, login '{login}'): {what}. El MCP no inicia con logins privilegiados (RS-DCL-05).");
            }
        }

        Fatal(Flag("is_sysadmin") == 1, "es miembro de sysadmin");
        Fatal(Flag("is_securityadmin") == 1, "es miembro de securityadmin");
        Fatal(Flag("is_serveradmin") == 1, "es miembro de serveradmin");
        Fatal(Flag("has_control_server") == 1, "tiene CONTROL SERVER");
        Fatal(Flag("has_impersonate_any_login") == 1, "tiene IMPERSONATE ANY LOGIN");
        Fatal(Flag("is_db_owner") == 1, "es miembro de db_owner");
        Fatal(Flag("is_db_securityadmin") == 1, "es miembro de db_securityadmin");
        Fatal(Flag("is_db_accessadmin") == 1, "es miembro de db_accessadmin");
        Fatal(Flag("has_db_control") == 1, "tiene CONTROL sobre la base de datos");
        Fatal(Flag("has_alter_any_user") == 1, "tiene ALTER ANY USER");
        Fatal(Flag("has_alter_any_role") == 1, "tiene ALTER ANY ROLE");
        Fatal(Flag("impersonable_users") > 0, "puede suplantar (IMPERSONATE) a otros usuarios de la base de datos");
        Fatal(Flag("impersonable_logins") > 0, "puede suplantar (IMPERSONATE) a otros logins");

        void Warn(bool condition, string what)
        {
            if (condition)
            {
                _logger.LogWarning("Perfil {Profile} ({Role}, login {Login}): {What}.", profile.Name, role, login, what);
            }
        }

        Warn(Flag("has_db_alter") == 1, "tiene ALTER sobre la base de datos (RS-PRV-05)");
        Warn(Flag("has_alter_any_schema") == 1, "tiene ALTER ANY SCHEMA (RS-PRV-05)");
        Warn(Flag("has_alter_any_app_role") == 1, "tiene ALTER ANY APPLICATION ROLE");
        Warn(Flag("is_db_ddladmin") == 1, "es miembro de db_ddladmin; se recomiendan permisos DDL por esquema (RS-PRV-03)");
        if (role == ConnectionRole.Read && profile.HasSeparateWriter)
        {
            Warn(Flag("is_db_datawriter") == 1 || Flag("has_db_insert") == 1 || Flag("has_db_update") == 1 || Flag("has_db_delete") == 1,
                "el login de lectura tiene permisos de escritura a nivel de base de datos (RS-PRV-02)");
        }

        // Defensa en profundidad frente a la evasión por sinónimos y SQL dinámico: el MCP ya resuelve sinónimos y analiza
        // los módulos, pero estos permisos permitirían ejecutar SQL dinámico si un control fallara.
        if (role == ConnectionRole.Write || !profile.HasSeparateWriter)
        {
            void SecurityWarning(bool condition, string what, string fix)
            {
                if (condition)
                {
                    var message = $"Login '{login}': {what}. Recomendado: {fix}";
                    lock (profile.SecurityWarnings)
                    {
                        if (!profile.SecurityWarnings.Contains(message))
                        {
                            profile.SecurityWarnings.Add(message);
                        }
                    }

                    _logger.LogWarning("Perfil {Profile}: {Message}", profile.Name, message);
                }
            }

            SecurityWarning(Flag("has_create_synonym") == 1, "tiene CREATE SYNONYM",
                $"DENY CREATE SYNONYM TO [{login}]; (si el agente no necesita crear sinónimos).");
            SecurityWarning(Flag("can_execute_sp_executesql") == 1, "puede ejecutar sys.sp_executesql (se concede a public por defecto)",
                $"en master: CREATE USER [{login}] FOR LOGIN [{login}]; DENY EXECUTE ON sys.sp_executesql TO [{login}]; (en una base de usuario no tiene efecto; EXEC(@sql) no se puede denegar y lo bloquea el analizador del MCP).");
        }

        return fatal;
    }

    private async Task CheckInstanceConfigurationAsync(ProfileRuntime profile, SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = InstanceConfigQuery;
            command.CommandTimeout = 15;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var value = reader.GetInt32(1);
                var insecure = name switch
                {
                    "clr strict security" => value == 0,
                    _ => value == 1,
                };
                if (insecure && name != "clr enabled")
                {
                    _logger.LogWarning("Perfil {Profile}: configuración de instancia insegura '{Setting}' = {Value} (RS-PRV-06).", profile.Name, name, value);
                }
                else if (name == "clr enabled" && value == 1)
                {
                    _logger.LogWarning("Perfil {Profile}: CLR habilitado; verifique 'clr strict security' (RS-PRV-06).", profile.Name);
                }
            }

            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt32(0) == 1)
                {
                    _logger.LogWarning("Perfil {Profile}: la base de datos tiene TRUSTWORTHY ON (RS-PRV-06).", profile.Name);
                }

                if (reader.GetInt32(1) == 1)
                {
                    _logger.LogWarning("Perfil {Profile}: la base de datos tiene DB_CHAINING ON (RS-PRV-06).", profile.Name);
                }
            }
        }
        catch (SqlException ex)
        {
            _logger.LogDebug("Perfil {Profile}: no se pudo leer la configuración de la instancia (error {Number}).", profile.Name, ex.Number);
        }
    }

    private async Task TryAuditAsync(ProfileRuntime profile, string outcome, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            await _audit.WriteAsync(new AuditRecord
            {
                Event = "security",
                Profile = profile.Name,
                Server = _connections.DescribeTarget(profile),
                Database = profile.InitialDatabase,
                Tool = "profile_verification",
                Outcome = outcome,
                Reason = reason,
                OsUser = Environment.UserName,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditWriteException ex)
        {
            _logger.LogError(ex, "No fue posible auditar la verificación del perfil {Profile}.", profile.Name);
        }
    }
}
