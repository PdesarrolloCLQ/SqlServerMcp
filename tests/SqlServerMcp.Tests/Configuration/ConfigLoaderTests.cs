using SqlServerMcp.Configuration;

namespace SqlServerMcp.Tests.Configuration;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlmcp-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string ValidProfile = """
        "connection": { "server": "localhost", "database": "VentasDev", "authentication": "WindowsIntegrated" },
        "allowedSchemas": ["ventas", "ia"]
        """;

    private string Write(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static Dictionary<string, string?> NoEnv() => new(StringComparer.OrdinalIgnoreCase);

    private LoadedConfiguration Load(string json, Dictionary<string, string?>? env = null, string? profile = null) =>
        ConfigLoader.Load(Write(json), profile, env ?? NoEnv());

    private ConfigurationException LoadFails(string json, Dictionary<string, string?>? env = null) =>
        Assert.Throws<ConfigurationException>(() => Load(json, env));

    [Fact]
    public void EjemploDelRepositorio_EsValido()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "sqlservermcp.example.json");
        var loaded = ConfigLoader.Load(example, null, NoEnv());
        Assert.Equal("desarrollo", loaded.DefaultProfile);
        var p = loaded.Profiles["desarrollo"];
        Assert.Equal(OperationMode.Completo, p.Mode);
        Assert.True(p.HasSeparateWriter);
        Assert.Single(p.Policy.Rules);
        Assert.Equal(PolicyAction.Block, p.Policy.Rules[0].Action);
        Assert.False(loaded.Profiles.ContainsKey("produccion")); // deshabilitado
        Assert.Equal(64, loaded.FileHash.Length);
    }

    [Fact]
    public void ValoresSegurosPorDefecto()
    {
        var loaded = Load($$"""{ "profiles": { "dev": { {{ValidProfile}} } } }""");
        var p = loaded.Profiles["dev"];
        Assert.Equal(OperationMode.Lectura, p.Mode);                  // RF-CFG-04
        Assert.Equal(EncryptMode.Mandatory, p.Config.Connection.Encrypt);
        Assert.False(p.Config.Connection.TrustServerCertificate);
        Assert.Equal(500, p.Limits.MaxRows);
        Assert.Equal(30, p.Limits.CommandTimeoutSeconds);
        Assert.Equal(PolicyAction.Block, p.Policy.UnfilteredDml);
        Assert.Equal(PolicyAction.Block, p.Policy.DynamicSql);
        Assert.Equal(ConfirmationUnavailableAction.Reject, p.Policy.ConfirmationUnavailable);
        Assert.Equal("dev", loaded.DefaultProfile);
    }

    [Fact]
    public void PropiedadDesconocida_ImpideElArranque()
    {
        var ex = LoadFails($$"""{ "profiles": { "dev": { {{ValidProfile}}, "modo": "COMPLETO" } } }""");
        Assert.Contains(ex.Errors, e => e.Contains("Error de esquema", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"unfilteredDml\": \"ALLOW\"")]
    [InlineData("\"dynamicSql\": \"PERMITIR\"")]
    [InlineData("\"dropAction\": \"ALERT\"")]
    [InlineData("\"truncateAction\": \"ALLOW\"")]
    [InlineData("\"destructiveAlterAction\": \"ALLOW\"")]
    [InlineData("\"triggerAction\": \"ALLOW\"")]
    public void ReglasConPiso_NoSePuedenRelajar(string policy)
    {
        var ex = LoadFails($$"""{ "profiles": { "dev": { {{ValidProfile}}, "policy": { {{policy}} } } } }""");
        Assert.Contains(ex.Errors, e => e.Contains("valor mínimo permitido", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfirmacionNoDisponible_AllowSoloEnDesarrollo()
    {
        var ex = LoadFails($$"""{ "profiles": { "prod": { {{ValidProfile}}, "environment": "Production", "policy": { "confirmationUnavailable": "AllowWithAlert" } } } }""");
        Assert.Contains(ex.Errors, e => e.Contains("AllowWithAlert", StringComparison.Ordinal));
        var ok = Load($$"""{ "profiles": { "dev": { {{ValidProfile}}, "environment": "Development", "policy": { "confirmationUnavailable": "AllowWithAlert" } } } }""");
        Assert.Equal(ConfirmationUnavailableAction.AllowWithAlert, ok.Profiles["dev"].Policy.ConfirmationUnavailable);
    }

    [Fact]
    public void SqlPassword_RequiereReferenciaDeSecreto_NoTextoPlano()
    {
        var ex = LoadFails("""
            { "profiles": { "dev": { "connection": { "server": "s", "database": "d", "authentication": "SqlPassword", "userName": "u", "credentialRef": "MiClave123" } } } }
            """);
        Assert.Contains(ex.Errors, e => e.Contains("credentialRef", StringComparison.Ordinal));
        var ex2 = LoadFails("""{ "profiles": { "dev": { "connection": { "server": "s", "database": "d", "authentication": "SqlPassword", "userName": "u" } } } }""");
        Assert.Contains(ex2.Errors, e => e.Contains("SqlPassword requiere", StringComparison.Ordinal));
    }

    [Fact]
    public void LaContrasenaEnLaCadenaDeConexion_NoEsUnaPropiedadAdmitida()
    {
        var ex = LoadFails("""{ "profiles": { "dev": { "connection": { "server": "s", "database": "d", "password": "x" } } } }""");
        Assert.NotEmpty(ex.Errors);
    }

    [Theory]
    [InlineData("\"allowedDatabases\": [\"master\"]")]
    [InlineData("\"allowedSchemas\": [\"sys\"]")]
    [InlineData("\"ddlSchemas\": [\"otro\"]")]
    public void ReferenciasInseguras_SeRechazan(string fragment)
    {
        LoadFails($$"""{ "profiles": { "dev": { "connection": { "server": "s", "database": "d" }, {{fragment}} } } }""");
    }

    [Fact]
    public void LimiteSuperiorAlMaximoAbsoluto_SeRechaza()
    {
        var ex = LoadFails($$"""{ "absoluteLimits": { "maxRows": 1000 }, "profiles": { "dev": { {{ValidProfile}}, "limits": { "maxRows": 5000 } } } }""");
        Assert.Contains(ex.Errors, e => e.Contains("máximo absoluto", StringComparison.Ordinal));
    }

    [Fact]
    public void VariasPerfilesSinDefaultProfile_SeRechaza()
    {
        LoadFails($$"""{ "profiles": { "a": { {{ValidProfile}} }, "b": { {{ValidProfile}} } } }""");
        var forced = Load($$"""{ "profiles": { "a": { {{ValidProfile}} }, "b": { {{ValidProfile}} } } }""", profile: "b");
        Assert.Equal("b", forced.DefaultProfile);
        Assert.Single(forced.Profiles);
    }

    [Fact]
    public void VariablesDeEntorno_SoloParametrosNoSensibles()
    {
        var json = $$"""{ "profiles": { "dev": { {{ValidProfile}} } } }""";
        var ok = Load(json, new Dictionary<string, string?> { ["SQLSERVERMCP__profiles__dev__limits__maxRows"] = "200" });
        Assert.Equal(200, ok.Profiles["dev"].Limits.MaxRows);
        Assert.Single(ok.AppliedOverrides);

        var ex = LoadFails(json, new Dictionary<string, string?> { ["SQLSERVERMCP__profiles__dev__mode"] = "COMPLETO" });
        Assert.Contains(ex.Errors, e => e.Contains("no permitido", StringComparison.Ordinal));
        LoadFails(json, new Dictionary<string, string?> { ["SQLSERVERMCP__profiles__dev__connection__server"] = "otro" });
        LoadFails(json, new Dictionary<string, string?> { ["SQLSERVERMCP__absoluteLimits__maxRows"] = "999999" });
    }

    [Fact]
    public void AliasEnEspanol_SeAceptan()
    {
        var loaded = Load($$"""{ "profiles": { "dev": { {{ValidProfile}}, "mode": "ESCRITURA_DATOS", "policy": { "rules": [ { "id": "R1", "statement": "Delete", "action": "BLOQUEAR", "reason": "x" } ] } } } }""");
        Assert.Equal(OperationMode.EscrituraDatos, loaded.Profiles["dev"].Mode);
        Assert.Equal(PolicyAction.Block, loaded.Profiles["dev"].Policy.Rules[0].Action);
    }

    [Fact]
    public void ReglaConPrefijoReservado_SeRechaza()
    {
        LoadFails($$"""{ "profiles": { "dev": { {{ValidProfile}}, "policy": { "rules": [ { "id": "POL-X", "action": "BLOCK", "reason": "x" } ] } } } }""");
    }

    [Fact]
    public void TransporteHttp_NoSeAdmite()
    {
        LoadFails($$"""{ "transport": "http", "profiles": { "dev": { {{ValidProfile}} } } }""");
    }
}
