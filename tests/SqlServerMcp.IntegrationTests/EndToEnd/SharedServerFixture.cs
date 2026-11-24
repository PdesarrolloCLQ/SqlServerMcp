namespace SqlServerMcp.IntegrationTests.EndToEnd;

/// <summary>Una instancia compartida del servidor (perfil COMPLETO con elicitation) para las pruebas de punta a punta.</summary>
public sealed class SharedServerFixture : IAsyncLifetime
{
    public McpServerHarness? Harness { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (IntegrationEnvironment.IsAvailable)
        {
            // La instancia compartida recibe muchos intentos críticos (CA-01, CA-05, CA-17…): se eleva el umbral de
            // suspensión (RS-DCL-04) para que no interfiera; RSDCL04 se prueba con su propia instancia y el umbral por defecto.
            var directory = McpServerHarness.NewDirectory();
            var config = McpServerHarness.DefaultConfig(directory, d => d["policy"]!["criticalAttemptsThreshold"] = 100);
            Harness = await McpServerHarness.StartAsync(config, directory: directory);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Harness is not null)
        {
            await Harness.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class EndToEndCollection : ICollectionFixture<SharedServerFixture>
{
    public const string Name = "SqlServerMcp de punta a punta";
}
