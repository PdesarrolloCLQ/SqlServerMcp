using System.Text;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using static SqlServerMcp.IntegrationTests.Read.ReadTestSupport;

namespace SqlServerMcp.IntegrationTests.Read;

/// <summary>Plan estimado con SHOWPLAN_XML contra SQL Server real (RF-QRY-09).</summary>
[Collection(ReadIntegrationCollection.Name)]
public class ExecutionPlanIntegrationTests
{
    private const string JoinSql =
        "SELECT f.FacturaId, c.Nombre, f.Total FROM ventas.Facturas f JOIN ventas.Clientes c ON c.ClienteId = f.ClienteId " +
        "WHERE f.Total > 500 AND c.Email LIKE N'%1%' ORDER BY f.Fecha";

    private static ExecutionPlanService Service(ISqlConnectionFactory factory) => new(factory, new ParameterBinder());

    private static Task<ExecutionPlanResult> PlanAsync(ExecutionPlanService service, ProfileRuntime profile, string sql, bool includeXml = false, IReadOnlyList<QueryParameter>? parameters = null) =>
        service.GetEstimatedPlanAsync(profile, sql, parameters ?? [], Guid.NewGuid().ToString("N"), includeXml, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Join_ResumenConOperadoresEIndiceFaltanteQueNoSeEjecuta()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("plan"));
        var plan = await PlanAsync(Service(CreateFactory()), profile, JoinSql);

        Assert.True(plan.EstimatedCost > 0);
        Assert.True(plan.EstimatedRows > 0);
        Assert.InRange(plan.CostlyOperators.Count, 2, PlanXmlAnalyzer.MaxOperators);
        Assert.Contains(plan.CostlyOperators, o => o.Object is not null && o.Object.StartsWith("ventas.Facturas", StringComparison.Ordinal));
        Assert.True(plan.CostlyOperators.Sum(o => o.CostPercent) is > 99 and < 101);
        Assert.Null(plan.PlanXml);

        var index = Assert.Single(plan.MissingIndexes, m => m.Table == "ventas.Facturas");
        Assert.Contains("Total", index.InequalityColumns);
        Assert.StartsWith("CREATE NONCLUSTERED INDEX [IX_Facturas_Total] ON [ventas].[Facturas] ([Total])", index.CreateStatement, StringComparison.Ordinal);
        Assert.True(index.Impact > 0);

        // La sugerencia nunca se ejecuta.
        using var admin = IntegrationEnvironment.OpenAdminConnection();
        using var command = admin.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_Facturas_Total'";
        Assert.Equal(0, (int)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task ConParametros_SeDeclaranSinValores()
    {
        IntegrationEnvironment.RequireDatabase();
        var plan = await PlanAsync(
            Service(CreateFactory()),
            Profile(NewApplicationName("plan-param")),
            "SELECT f.FacturaId FROM ventas.Facturas f JOIN ventas.Clientes c ON c.ClienteId = f.ClienteId WHERE f.Total > @t AND c.Nombre = @nombre",
            includeXml: true,
            parameters: [new QueryParameter("@t", "decimal(18,2)", ParseJson("100")), new QueryParameter("nombre", null, ParseJson("\"x'; DROP TABLE y --\""))]);

        Assert.NotEmpty(plan.CostlyOperators);
        Assert.Contains(plan.Warnings, w => w.Contains("variables locales", StringComparison.Ordinal));
        Assert.NotNull(plan.PlanXml);
        Assert.DoesNotContain("DROP TABLE y", plan.PlanXml, StringComparison.Ordinal);
        Assert.Contains("DECLARE @t decimal(18,2), @nombre nvarchar(4000);", plan.PlanXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncluirXml_RecortadoAlTamanoMaximo()
    {
        IntegrationEnvironment.RequireDatabase();
        var service = Service(CreateFactory());

        var full = await PlanAsync(service, Profile(NewApplicationName("plan-xml")), JoinSql, includeXml: true);
        Assert.StartsWith("<ShowPlanXML", full.PlanXml, StringComparison.Ordinal);
        Assert.False(full.PlanXmlTruncated);

        var small = await PlanAsync(service, Profile(NewApplicationName("plan-xml2"), c => c.Limits.MaxResponseBytes = 1000), JoinSql, includeXml: true);
        Assert.True(small.PlanXmlTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(small.PlanXml!) <= 1000);
        Assert.NotEmpty(small.CostlyOperators);
    }

    [Fact]
    public async Task ShowplanSeDesactiva_LaConexionDelPoolEjecutaConsultasReales()
    {
        IntegrationEnvironment.RequireDatabase();
        var factory = CreateFactory();
        var profile = Profile(NewApplicationName("plan-pool"), c => c.Connection.MaxPoolSize = 1);
        var service = Service(factory);
        var executor = Executor(factory);

        for (var i = 0; i < 3; i++)
        {
            await PlanAsync(service, profile, "SELECT COUNT(*) AS n FROM ventas.Clientes");
            var outcome = await RunAsync(executor, profile, "SELECT COUNT(*) AS n FROM ventas.Clientes");
            Assert.Equal(300L, Assert.Single(outcome.Result.Rows)[0]);
        }

        // Un error durante SHOWPLAN también deja la sesión sin SHOWPLAN.
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => PlanAsync(service, profile, "SELECT * FROM ventas.NoExiste"));
        Assert.Equal(ErrorCodes.SqlError, ex.Code);
        Assert.Equal(208, ex.Details["number"]);
        var after = await RunAsync(executor, profile, "SELECT 1 AS uno");
        Assert.Equal(1L, after.Result.Rows[0][0]);
    }

    [Fact]
    public async Task SinPermisoShowplan_ErrorConSugerenciaParaElDba()
    {
        IntegrationEnvironment.RequireDatabase();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var login = "mcp_sinplan_" + suffix;
        var variable = "SQLSERVERMCP_TEST_NOPLAN_" + suffix.ToUpperInvariant();
        var password = "Aa1!" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var quotedLogin = SqlNames.Quote(login);
        IntegrationEnvironment.ExecuteAdmin(
            $"CREATE LOGIN {quotedLogin} WITH PASSWORD = N'{password}', CHECK_POLICY = OFF, DEFAULT_DATABASE = [{IntegrationEnvironment.Database}];" +
            $"CREATE USER {quotedLogin} FOR LOGIN {quotedLogin} WITH DEFAULT_SCHEMA = [ventas];" +
            $"GRANT SELECT ON SCHEMA::[ventas] TO {quotedLogin};");
        Environment.SetEnvironmentVariable(variable, password);
        try
        {
            var profile = Profile(NewApplicationName("plan-sinpermiso"), c =>
            {
                c.Connection.UserName = login;
                c.Connection.CredentialRef = "env:" + variable;
                c.WriteConnection = null;
            });

            var ex = await Assert.ThrowsAsync<ToolFailureException>(() => PlanAsync(Service(CreateFactory()), profile, "SELECT ClienteId FROM ventas.Clientes"));
            Assert.Equal(ErrorCodes.SqlError, ex.Code);
            Assert.Equal(262, ex.Details["number"]);
            Assert.Contains("GRANT SHOWPLAN", ex.Suggestion, StringComparison.Ordinal);
            Assert.DoesNotContain(password, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            SqlConnection.ClearAllPools();
            IntegrationEnvironment.ExecuteAdmin(
                $"DECLARE @kill nvarchar(max) = N''; SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';' FROM sys.dm_exec_sessions WHERE login_name = N'{login}'; EXEC (@kill);" +
                $"IF USER_ID(N'{login}') IS NOT NULL DROP USER {quotedLogin};" +
                $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN {quotedLogin};");
        }
    }
}
