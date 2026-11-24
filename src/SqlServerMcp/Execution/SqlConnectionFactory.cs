using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Configuration;
using SqlServerMcp.Security;

namespace SqlServerMcp.Execution;

/// <summary>
/// Abre conexiones por perfil y rol con el driver oficial Microsoft.Data.SqlClient (RF-CON-01..11).
/// Las cadenas de conexión nunca contienen contraseñas: se usa <see cref="SqlCredential"/> con SecureString.
/// Las cadenas de conexión y credenciales nunca salen de esta clase (RS-SEC-03).
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private const int LoginFailedError = 18456;

    private readonly ISecretResolver _secrets;
    private readonly ILogger<SqlConnectionFactory> _logger;
    private readonly ConcurrentDictionary<(string Profile, ConnectionRole Role), ConnectionPlan> _plans = new();

    public SqlConnectionFactory(ISecretResolver secrets, ILogger<SqlConnectionFactory> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public string DescribeTarget(ProfileRuntime profile) => $"{profile.Config.Connection.Server}/{profile.InitialDatabase}";

    public async Task<SqlConnection> OpenAsync(ProfileRuntime profile, ConnectionRole role, string correlationId, CancellationToken cancellationToken)
    {
        var effectiveRole = role == ConnectionRole.Write && profile.HasSeparateWriter ? ConnectionRole.Write : ConnectionRole.Read;
        var plan = _plans.GetOrAdd((profile.Name, effectiveRole), _ => BuildPlan(profile, effectiveRole));

        SqlConnection connection;
        try
        {
            connection = await OpenWithPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == LoginFailedError && plan.UsesSecret)
        {
            // Rotación de credenciales (RS-SEC-05): se vuelve a leer el secreto una vez.
            _logger.LogWarning("Perfil {Profile}: inicio de sesión rechazado; se vuelve a leer la credencial por posible rotación.", profile.Name);
            plan = BuildPlan(profile, effectiveRole);
            _plans[(profile.Name, effectiveRole)] = plan;
            connection = await OpenWithPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await PrepareSessionAsync(connection, profile, correlationId, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SqlConnection> OpenWithPlanAsync(ConnectionPlan plan, CancellationToken cancellationToken)
    {
        var connection = plan.Credential is null
            ? new SqlConnection(plan.ConnectionString)
            : new SqlConnection(plan.ConnectionString, plan.Credential);
        if (plan.RetryProvider is not null)
        {
            connection.RetryLogicProvider = plan.RetryProvider;
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Prepara la sesión: identificador de correlación en SESSION_CONTEXT (RF-CON-08, RS-AUD-07),
    /// LOCK_TIMEOUT (RS-LIM), XACT_ABORT ON y nivel de aislamiento (RF-QRY-12).
    /// </summary>
    private static async Task PrepareSessionAsync(SqlConnection connection, ProfileRuntime profile, string correlationId, CancellationToken cancellationToken)
    {
        var isolation = profile.Config.IsolationLevel switch
        {
            QueryIsolationLevel.Snapshot => "SNAPSHOT",
            QueryIsolationLevel.ReadUncommitted => "READ UNCOMMITTED",
            QueryIsolationLevel.RepeatableRead => "REPEATABLE READ",
            QueryIsolationLevel.Serializable => "SERIALIZABLE",
            _ => "READ COMMITTED",
        };

        // SESSION_CONTEXT persiste en la sesión aunque se fije desde un lote parametrizado (sp_executesql).
        await using (var context = connection.CreateCommand())
        {
            context.CommandTimeout = Math.Max(5, profile.Limits.CommandTimeoutSeconds);
            context.CommandText =
                "EXEC sys.sp_set_session_context @key = N'mcp_correlation_id', @value = @cid;" +
                "EXEC sys.sp_set_session_context @key = N'mcp_profile', @value = @profile;";
            context.Parameters.Add(new SqlParameter("@cid", SqlDbType.NVarChar, 64) { Value = correlationId });
            context.Parameters.Add(new SqlParameter("@profile", SqlDbType.NVarChar, 64) { Value = profile.Name });
            await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Las opciones SET deben ir en un lote SIN parámetros: dentro de sp_executesql se revierten al terminar el lote.
        // Todos los valores son números o palabras clave controlados por el servidor, nunca texto del agente.
        await using var options = connection.CreateCommand();
        options.CommandTimeout = Math.Max(5, profile.Limits.CommandTimeoutSeconds);
        options.CommandText =
            $"SET LOCK_TIMEOUT {profile.Limits.LockTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
            "SET XACT_ABORT ON; SET IMPLICIT_TRANSACTIONS OFF; SET NOCOUNT OFF;" +
            $"SET TRANSACTION ISOLATION LEVEL {isolation};";
        await options.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private ConnectionPlan BuildPlan(ProfileRuntime profile, ConnectionRole role)
    {
        var main = profile.Config.Connection;
        var write = role == ConnectionRole.Write ? profile.Config.WriteConnection : null;
        var auth = write?.Authentication ?? main.Authentication;
        var user = write is not null ? (write.UserName ?? main.UserName) : main.UserName;
        var credentialRef = write is not null ? (write.CredentialRef ?? (write.Authentication is null ? main.CredentialRef : null)) : main.CredentialRef;
        var appName = write?.ApplicationName ?? main.ApplicationName;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = main.Server,
            InitialCatalog = profile.InitialDatabase,
            ApplicationName = appName,
            Encrypt = main.Encrypt switch
            {
                EncryptMode.Strict => SqlConnectionEncryptOption.Strict,
                EncryptMode.Optional => SqlConnectionEncryptOption.Optional,
                _ => SqlConnectionEncryptOption.Mandatory,
            },
            TrustServerCertificate = main.Encrypt != EncryptMode.Strict && main.TrustServerCertificate,
            ApplicationIntent = main.ApplicationIntent == ApplicationIntentKind.ReadOnly && role == ConnectionRole.Read
                ? ApplicationIntent.ReadOnly
                : ApplicationIntent.ReadWrite,
            ConnectTimeout = main.ConnectTimeoutSeconds,
            Pooling = true,
            MaxPoolSize = main.MaxPoolSize,
            MultipleActiveResultSets = false,
            PersistSecurityInfo = false,
            ConnectRetryCount = main.Retry.Enabled ? Math.Min(main.Retry.MaxRetries, 255) : 0,
        };

        if (!string.IsNullOrWhiteSpace(main.HostNameInCertificate))
        {
            builder.HostNameInCertificate = main.HostNameInCertificate;
        }

        SqlCredential? credential = null;
        var usesSecret = false;
        switch (auth)
        {
            case AuthenticationKind.WindowsIntegrated:
                builder.IntegratedSecurity = true;
                break;
            case AuthenticationKind.SqlPassword:
            {
                var secret = _secrets.Resolve(credentialRef!);
                var login = string.IsNullOrWhiteSpace(user) ? secret.UserName : user!;
                credential = new SqlCredential(login, secret.ToSecureString());
                usesSecret = true;
                break;
            }

            case AuthenticationKind.EntraManagedIdentity:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    builder.UserID = user;
                }

                break;
            case AuthenticationKind.EntraServicePrincipal:
            {
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
                var secret = _secrets.Resolve(credentialRef!);
                credential = new SqlCredential(user!, secret.ToSecureString());
                usesSecret = true;
                break;
            }

            case AuthenticationKind.EntraInteractive:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    builder.UserID = user;
                }

                break;
            case AuthenticationKind.EntraDefault:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
        }

        SqlRetryLogicBaseProvider? retry = null;
        if (main.Retry.Enabled && main.Retry.MaxRetries > 0)
        {
            retry = SqlConfigurableRetryFactory.CreateExponentialRetryProvider(new SqlRetryLogicOption
            {
                NumberOfTries = main.Retry.MaxRetries + 1,
                DeltaTime = TimeSpan.FromSeconds(main.Retry.DeltaSeconds),
                MaxTimeInterval = TimeSpan.FromSeconds(main.Retry.MaxSeconds),
            });
        }

        return new ConnectionPlan(builder.ConnectionString, credential, retry, usesSecret);
    }

    private sealed record ConnectionPlan(string ConnectionString, SqlCredential? Credential, SqlRetryLogicBaseProvider? RetryProvider, bool UsesSecret);
}
