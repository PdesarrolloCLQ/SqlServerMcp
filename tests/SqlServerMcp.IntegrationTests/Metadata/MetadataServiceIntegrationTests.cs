using System.Text.Json;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.IntegrationTests.Metadata;

/// <summary>
/// Pruebas de <see cref="MetadataService"/> contra VentasTest con el perfil estándar de pruebas
/// (login lector; esquemas ventas e ia; excluidos: esquema seguridad, ventas.TarjetasCredito y *.Contrasena).
/// </summary>
public class MetadataServiceIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MetadataService _service;
    private readonly ProfileRuntime _profile;

    public MetadataServiceIntegrationTests()
    {
        (_service, _, _) = MetadataIntegrationSupport.CreateServices();
        _profile = IntegrationEnvironment.CreateProfile();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TestConnection_DevuelveVersionEdicionYBase()
    {
        IntegrationEnvironment.RequireDatabase();
        var result = await _service.TestConnectionAsync(_profile, "it-test-connection", Ct);

        Assert.StartsWith("16.", result.ProductVersion, StringComparison.Ordinal);
        Assert.Equal("SQL Server", result.EngineEditionDescription);
        Assert.Equal(3, result.EngineEdition);
        Assert.Contains("Developer", result.Edition, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IntegrationEnvironment.Database, result.CurrentDatabase);
        Assert.Equal("mcp_lector", result.LoginName);
        Assert.InRange(result.ServerUtcTime, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.True(result.CompatibilityLevel >= 130);
        Assert.False(result.IsReadOnly);
        Assert.NotNull(result.Collation);
    }

    [Fact]
    public async Task ListDatabases_SoloLasPermitidasYAccesibles()
    {
        IntegrationEnvironment.RequireDatabase();
        var result = await _service.ListDatabasesAsync(_profile, "it", Ct);
        var database = Assert.Single(result.Databases);
        Assert.Equal(IntegrationEnvironment.Database, database.Name);
        Assert.True(database.IsCurrent);
        Assert.Equal("ONLINE", database.State);

        // OtraBD permitida en el perfil pero sin acceso para el login lector: no aparece.
        var withOther = IntegrationEnvironment.CreateProfile(customize: c => c.AllowedDatabases = [IntegrationEnvironment.Database, "OtraBD"], name: "metadatos-otra");
        var filtered = await _service.ListDatabasesAsync(withOther, "it", Ct);
        Assert.Equal([IntegrationEnvironment.Database], filtered.Databases.Select(d => d.Name));
    }

    [Fact]
    public async Task BaseSinAccesoDelLogin_ErrorControlado()
    {
        IntegrationEnvironment.RequireDatabase();
        var withOther = IntegrationEnvironment.CreateProfile(customize: c => c.AllowedDatabases = [IntegrationEnvironment.Database, "OtraBD"], name: "metadatos-otra-acceso");
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => _service.ListTablesAsync(withOther, "OtraBD", null, null, true, 0, 10, "it", Ct));
        Assert.Equal(ErrorCodes.SqlError, ex.Code);
        Assert.Contains("OtraBD", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListSchemas_SinEsquemasNiObjetosExcluidos()
    {
        IntegrationEnvironment.RequireDatabase();
        var result = await _service.ListSchemasAsync(_profile, null, "it", Ct);
        Assert.Equal(IntegrationEnvironment.Database, result.Database);
        Assert.DoesNotContain(result.Schemas, s => s.Name == "seguridad" || s.Name == "dbo");
        var ventas = Assert.Single(result.Schemas, s => s.Name == "ventas");

        var tables = await _service.ListTablesAsync(_profile, null, "ventas", null, includeViews: false, 0, 500, "it", Ct);
        Assert.Equal(tables.TotalCount, ventas.Tables);
        Assert.True(ventas.Views >= 1);
        Assert.True(ventas.Procedures >= 2);
        Assert.True(ventas.Functions >= 1);
    }

    [Fact]
    public async Task ListTables_FiltrosYPaginacion()
    {
        IntegrationEnvironment.RequireDatabase();
        var all = await _service.ListTablesAsync(_profile, null, "ventas", null, includeViews: true, 0, 500, "it", Ct);
        var clientes = Assert.Single(all.Items, t => t.Name == "Clientes");
        Assert.Equal("TABLE", clientes.Type);
        Assert.Equal("Clientes de la compañía", clientes.Description);
        Assert.True(clientes.ApproximateRows > 0);
        Assert.Null(clientes.TemporalType);
        Assert.Contains(all.Items, t => t.Name == "vClientesActivos" && t.Type == "VIEW");
        Assert.DoesNotContain(all.Items, t => t.Name == "TarjetasCredito");
        Assert.Equal(all.Items.Count, all.TotalCount);
        Assert.False(all.HasMore);

        var page = await _service.ListTablesAsync(_profile, null, "ventas", null, includeViews: true, 1, 1, "it", Ct);
        Assert.Single(page.Items);
        Assert.Equal(all.Items[1], page.Items[0]);
        Assert.True(page.HasMore);
        Assert.Equal(all.TotalCount, page.TotalCount);

        var clamped = await _service.ListTablesAsync(_profile, null, "ventas", null, includeViews: true, -10, 0, "it", Ct);
        Assert.Equal(0, clamped.Offset);
        Assert.Equal(1, clamped.Limit);

        var tablesOnly = await _service.ListTablesAsync(_profile, null, "ventas", null, includeViews: false, 0, 500, "it", Ct);
        Assert.All(tablesOnly.Items, t => Assert.Equal("TABLE", t.Type));
    }

    [Fact]
    public async Task ListTables_PatronDeNombre()
    {
        IntegrationEnvironment.RequireDatabase();
        var prefix = await _service.ListTablesAsync(_profile, null, "ventas", "Cli*", true, 0, 100, "it", Ct);
        Assert.Equal(["Clientes"], prefix.Items.Select(t => t.Name));

        var contains = await _service.ListTablesAsync(_profile, null, "VENTAS", "clientes", true, 0, 100, "it", Ct);
        Assert.Equal(["Clientes", "vClientesActivos"], contains.Items.Select(t => t.Name).Order(StringComparer.Ordinal));

        var single = await _service.ListTablesAsync(_profile, null, "ventas", "Factura?", true, 0, 100, "it", Ct);
        Assert.Equal(["Facturas"], single.Items.Select(t => t.Name));

        var excluded = await _service.ListTablesAsync(_profile, null, null, "Tarjetas*", true, 0, 100, "it", Ct);
        Assert.Empty(excluded.Items);
        Assert.Equal(0, excluded.TotalCount);
    }

    [Theory]
    [InlineData("%' OR 1=1 --")]
    [InlineData("[a-z]%")]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("'; DROP TABLE ventas.Clientes; --")]
    public async Task IntentosDeInyeccion_SeTratanLiteralmente(string attempt)
    {
        IntegrationEnvironment.RequireDatabase();
        var tables = await _service.ListTablesAsync(_profile, null, "ventas", attempt, true, 0, 100, "it", Ct);
        Assert.Empty(tables.Items);
        Assert.Equal(0, tables.TotalCount);

        var bySchema = await _service.ListTablesAsync(_profile, null, attempt, null, true, 0, 100, "it", Ct);
        Assert.Empty(bySchema.Items);

        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => _service.DescribeTableAsync(_profile, null, "ventas", attempt, "it", Ct));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        // La tabla sigue existiendo y nada se ejecutó.
        var clientes = await _service.DescribeTableAsync(_profile, null, "ventas", "Clientes", "it", Ct);
        Assert.Equal("Clientes", clientes.Name);
    }

    [Theory]
    [InlineData("%' OR 1=1 --")]
    [InlineData("[a-z]%")]
    [InlineData("'; DROP TABLE ventas.Clientes; --")]
    public async Task IntentosDeInyeccion_EnRutinasYBusqueda(string attempt)
    {
        IntegrationEnvironment.RequireDatabase();
        var routines = await _service.ListRoutinesAsync(_profile, null, "ventas", attempt, RoutineTypeFilter.Any, 0, 100, "it", Ct);
        Assert.Empty(routines.Items);

        var search = await _service.SearchObjectsAsync(_profile, null, attempt, SearchScope.Both, SearchObjectType.Any, 0, 100, "it", Ct);
        Assert.Empty(search.Items);
        Assert.Equal(0, search.TotalCount);
    }

    [Fact]
    public async Task ComodinesDeLike_SeEscapan()
    {
        IntegrationEnvironment.RequireDatabase();

        // "_" es literal: solo coinciden nombres con guion bajo (sin escape coincidiría con todo).
        var underscore = await _service.ListRoutinesAsync(_profile, null, "ventas", "_", RoutineTypeFilter.Any, 0, 100, "it", Ct);
        Assert.NotEmpty(underscore.Items);
        Assert.All(underscore.Items, r => Assert.Contains('_', r.Name));

        var prefix = await _service.ListRoutinesAsync(_profile, null, "ventas", "usp_*", RoutineTypeFilter.Any, 0, 100, "it", Ct);
        Assert.Equal(["usp_ClientesPorSaldo", "usp_DesactivarCliente"], prefix.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task DescribeTable_Clientes()
    {
        IntegrationEnvironment.RequireDatabase();
        var table = await _service.DescribeTableAsync(_profile, null, "ventas", "Clientes", "it", Ct);

        Assert.Equal("TABLE", table.Type);
        Assert.Equal("Clientes de la compañía", table.Description);
        Assert.True(table.ApproximateRows > 0);
        Assert.Equal(["ClienteId", "Nombre", "Email", "Activo", "Saldo", "FechaAlta", "Notas"], table.Columns.Select(c => c.Name));

        var id = table.Columns[0];
        Assert.True(id.IsIdentity);
        Assert.Equal(1L, id.IdentitySeed);
        Assert.Equal(1L, id.IdentityIncrement);
        Assert.Equal("int", id.DataType);
        Assert.False(id.IsNullable);

        var nombre = table.Columns.Single(c => c.Name == "Nombre");
        Assert.Equal("nvarchar(100)", nombre.DataType);
        Assert.Equal(100, nombre.Length);
        Assert.NotNull(nombre.Collation);

        var saldo = table.Columns.Single(c => c.Name == "Saldo");
        Assert.Equal("decimal(18,4)", saldo.DataType);
        Assert.Equal(18, saldo.Precision);
        Assert.Equal(4, saldo.Scale);
        Assert.Equal("DF_Clientes_Saldo", saldo.DefaultConstraint);
        Assert.Equal("((0))", saldo.DefaultDefinition);

        Assert.Equal("((1))", table.Columns.Single(c => c.Name == "Activo").DefaultDefinition);
        Assert.Contains("sysutcdatetime", table.Columns.Single(c => c.Name == "FechaAlta").DefaultDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, table.Columns.Single(c => c.Name == "FechaAlta").Scale);
        Assert.Equal(-1, table.Columns.Single(c => c.Name == "Notas").Length);
        Assert.Equal("nvarchar(max)", table.Columns.Single(c => c.Name == "Notas").DataType);

        Assert.NotNull(table.PrimaryKey);
        Assert.Equal("PK_Clientes", table.PrimaryKey.Name);
        Assert.Equal(["ClienteId"], table.PrimaryKey.Columns);
        Assert.True(table.PrimaryKey.IsClustered);

        var unique = Assert.Single(table.UniqueConstraints);
        Assert.Equal("UQ_Clientes_Email", unique.Name);
        Assert.Equal(["Email"], unique.Columns);

        var check = Assert.Single(table.CheckConstraints);
        Assert.Equal("CK_Clientes_Saldo", check.Name);
        Assert.Contains("[Saldo]>=(-1000000)", check.Definition, StringComparison.Ordinal);
        Assert.True(check.IsTrusted);

        Assert.Empty(table.ForeignKeys);
        Assert.Contains(table.Indexes, i => i.Name == "PK_Clientes" && i.IsPrimaryKey && i.Type == "CLUSTERED");
        Assert.Contains(table.Indexes, i => i.Name == "UQ_Clientes_Email" && i.IsUniqueConstraint && i.IsUnique);
        Assert.Null(table.Temporal);
    }

    [Fact]
    public async Task DescribeTable_Facturas_ClaveForaneaEIndiceConInclude()
    {
        IntegrationEnvironment.RequireDatabase();
        var table = await _service.DescribeTableAsync(_profile, null, null, "ventas.Facturas", "it", Ct);

        var fk = Assert.Single(table.ForeignKeys);
        Assert.Equal("FK_Facturas_Clientes", fk.Name);
        Assert.Equal(["ClienteId"], fk.Columns);
        Assert.Equal("ventas", fk.ReferencedSchema);
        Assert.Equal("Clientes", fk.ReferencedTable);
        Assert.Equal(["ClienteId"], fk.ReferencedColumns);
        Assert.Equal("NO_ACTION", fk.OnDelete);
        Assert.Equal("NO_ACTION", fk.OnUpdate);
        Assert.False(fk.IsDisabled);

        var index = Assert.Single(table.Indexes, i => i.Name == "IX_Facturas_ClienteId");
        Assert.Equal("NONCLUSTERED", index.Type);
        Assert.False(index.IsUnique);
        var key = Assert.Single(index.KeyColumns);
        Assert.Equal(new IndexKeyColumn("ClienteId", 1, "ASC"), key);
        Assert.Equal(["Total"], index.IncludedColumns);
        Assert.True(index.SizeKb > 0);

        Assert.Equal("varbinary(64)", table.Columns.Single(c => c.Name == "Firma").DataType);
        Assert.Equal(64, table.Columns.Single(c => c.Name == "Firma").Length);
        Assert.Contains("newid", table.Columns.Single(c => c.Name == "Guid").DefaultDefinition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DescribeTable_ColumnasExcluidasYSensibles()
    {
        IntegrationEnvironment.RequireDatabase();
        var table = await _service.DescribeTableAsync(_profile, null, "ventas", "Usuarios", "it", Ct);
        Assert.Equal(["UsuarioId", "Login", "Token"], table.Columns.Select(c => c.Name));
        Assert.True(table.Columns.Single(c => c.Name == "Token").IsSensitive);
        Assert.False(table.Columns.Single(c => c.Name == "Login").IsSensitive);
    }

    [Fact]
    public async Task DescribeTable_Vista()
    {
        IntegrationEnvironment.RequireDatabase();
        var view = await _service.DescribeTableAsync(_profile, null, "ventas", "vClientesActivos", "it", Ct);
        Assert.Equal("VIEW", view.Type);
        Assert.Equal(["ClienteId", "Nombre", "Email", "Saldo"], view.Columns.Select(c => c.Name));
        Assert.Null(view.PrimaryKey);
        Assert.Null(view.ApproximateRows);
    }

    [Fact]
    public async Task NotFound_UniformeParaInexistenteExcluidoONoPermitido()
    {
        IntegrationEnvironment.RequireDatabase();
        (string Schema, string Name)[] cases =
        [
            ("ventas", "NoExiste"),
            ("ventas", "TarjetasCredito"),
            ("seguridad", "Claves"),
            ("dbo", "Clientes"),
            ("ventas", "#Temporal"),
        ];

        var messages = new List<string>();
        foreach (var (schema, name) in cases)
        {
            var ex = await Assert.ThrowsAsync<ToolFailureException>(() => _service.DescribeTableAsync(_profile, null, schema, name, "it", Ct));
            Assert.Equal(ErrorCodes.NotFound, ex.Code);
            messages.Add(ex.Message.Replace($"{schema}.{name}", "{objeto}", StringComparison.Ordinal) + "|" + ex.Suggestion);
        }

        Assert.Single(messages.Distinct());

        foreach (var call in new Func<Task>[]
        {
            () => _service.ListIndexesAsync(_profile, null, "ventas", "TarjetasCredito", "it", Ct),
            () => _service.GetObjectDefinitionAsync(_profile, null, "seguridad", "Claves", "it", Ct),
            () => _service.GetDependenciesAsync(_profile, null, "ventas", "TarjetasCredito", DependencyDirection.Both, "it", Ct),
            () => _service.DescribeRoutineAsync(_profile, null, "seguridad", "Claves", "it", Ct),
            () => _service.GetRelationshipsAsync(_profile, null, "ventas", "TarjetasCredito", "it", Ct),
            () => _service.GetTableStatsAsync(_profile, null, "ventas", "TarjetasCredito", 0, 10, "it", Ct),
        })
        {
            var ex = await Assert.ThrowsAsync<ToolFailureException>(call);
            Assert.Equal(ErrorCodes.NotFound, ex.Code);
        }
    }

    [Fact]
    public async Task BaseNoPermitida_PolicyBlocked()
    {
        IntegrationEnvironment.RequireDatabase();
        foreach (var call in new Func<Task>[]
        {
            () => _service.ListTablesAsync(_profile, "OtraBD", null, null, true, 0, 10, "it", Ct),
            () => _service.DescribeTableAsync(_profile, "OtraBD", "dbo", "X", "it", Ct),
            () => _service.ListSchemasAsync(_profile, "OtraBD", "it", Ct),
            () => _service.SearchObjectsAsync(_profile, "OtraBD", "x", SearchScope.Both, SearchObjectType.Any, 0, 10, "it", Ct),
            () => _service.ListTablesAsync(_profile, "master", null, null, true, 0, 10, "it", Ct),
        })
        {
            var ex = await Assert.ThrowsAsync<ToolFailureException>(call);
            Assert.Equal(ErrorCodes.PolicyBlocked, ex.Code);
        }
    }

    [Fact]
    public async Task Rutinas_ListaYDescripcion()
    {
        IntegrationEnvironment.RequireDatabase();
        var routines = await _service.ListRoutinesAsync(_profile, null, "ventas", null, RoutineTypeFilter.Any, 0, 100, "it", Ct);
        Assert.Contains(routines.Items, r => r.Name == "usp_ClientesPorSaldo" && r.Type == "procedure" && r.TypeDescription == "SQL_STORED_PROCEDURE");
        Assert.Contains(routines.Items, r => r.Name == "fn_TotalCliente" && r.Type == "scalar_function");

        var scalars = await _service.ListRoutinesAsync(_profile, null, "ventas", null, RoutineTypeFilter.ScalarFunction, 0, 100, "it", Ct);
        Assert.Equal(["fn_TotalCliente"], scalars.Items.Select(r => r.Name));

        var procedure = await _service.DescribeRoutineAsync(_profile, null, "ventas", "usp_ClientesPorSaldo", "it", Ct);
        Assert.Equal("procedure", procedure.Type);
        Assert.Collection(
            procedure.Parameters,
            p =>
            {
                Assert.Equal("@SaldoMinimo", p.Name);
                Assert.Equal("decimal(18,4)", p.DataType);
                Assert.Equal("IN", p.Direction);
                Assert.False(p.HasDefault);
            },
            p =>
            {
                Assert.Equal("@Cantidad", p.Name);
                Assert.Equal("int", p.DataType);
                Assert.Equal("OUT", p.Direction);
            });
        Assert.Null(procedure.ReturnType);
        Assert.False(procedure.IsEncrypted);

        var function = await _service.DescribeRoutineAsync(_profile, null, "ventas", "fn_TotalCliente", "it", Ct);
        Assert.Equal("scalar_function", function.Type);
        Assert.Equal("decimal(18,2)", function.ReturnType);
        var parameter = Assert.Single(function.Parameters);
        Assert.Equal("@ClienteId", parameter.Name);
        Assert.Equal(1, parameter.Ordinal);

        var notRoutine = await Assert.ThrowsAsync<ToolFailureException>(() => _service.DescribeRoutineAsync(_profile, null, "ventas", "Clientes", "it", Ct));
        Assert.Equal(ErrorCodes.NotFound, notRoutine.Code);
    }

    [Fact]
    public async Task Definiciones()
    {
        IntegrationEnvironment.RequireDatabase();
        var view = await _service.GetObjectDefinitionAsync(_profile, null, "ventas", "vClientesActivos", "it", Ct);
        Assert.Equal("VIEW", view.TypeDescription);
        Assert.Contains("WHERE Activo = 1", view.Definition, StringComparison.Ordinal);
        Assert.False(view.IsEncrypted);
        Assert.False(view.IsWithheld);

        var procedure = await _service.GetObjectDefinitionAsync(_profile, null, "ventas", "usp_ClientesPorSaldo", "it", Ct);
        Assert.Contains("RETURN 7", procedure.Definition, StringComparison.Ordinal);

        var function = await _service.GetObjectDefinitionAsync(_profile, null, null, "ventas.fn_TotalCliente", "it", Ct);
        Assert.Contains("RETURNS decimal(18,2)", function.Definition, StringComparison.OrdinalIgnoreCase);

        var table = await Assert.ThrowsAsync<ToolFailureException>(() => _service.GetObjectDefinitionAsync(_profile, null, "ventas", "Clientes", "it", Ct));
        Assert.Equal(ErrorCodes.NotFound, table.Code);
        Assert.Contains("describe_table", table.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indices_DeFacturas()
    {
        IntegrationEnvironment.RequireDatabase();
        var result = await _service.ListIndexesAsync(_profile, null, "ventas", "Facturas", "it", Ct);
        Assert.Equal("Facturas", result.Table);
        Assert.Equal(["PK_Facturas", "IX_Facturas_ClienteId"], result.Indexes.Select(i => i.Name));
        Assert.Equal(["Total"], result.Indexes[1].IncludedColumns);
    }

    [Fact]
    public async Task Relaciones_EntrantesYSalientes()
    {
        IntegrationEnvironment.RequireDatabase();
        var forClientes = await _service.GetRelationshipsAsync(_profile, null, "ventas", "Clientes", "it", Ct);
        var edge = Assert.Single(forClientes.Relationships, r => r.ConstraintName == "FK_Facturas_Clientes");
        Assert.Equal(("ventas", "Facturas", "ventas", "Clientes"), (edge.FromSchema, edge.FromTable, edge.ToSchema, edge.ToTable));
        Assert.Equal(["ClienteId"], edge.FromColumns);
        Assert.Equal(["ClienteId"], edge.ToColumns);

        var forFacturas = await _service.GetRelationshipsAsync(_profile, null, null, "ventas.Facturas", "it", Ct);
        Assert.Contains(forFacturas.Relationships, r => r.ConstraintName == "FK_Facturas_Clientes");

        var all = await _service.GetRelationshipsAsync(_profile, null, null, null, "it", Ct);
        Assert.False(all.Truncated);
        Assert.Contains(all.Relationships, r => r.ConstraintName == "FK_Facturas_Clientes");
        Assert.DoesNotContain(all.Relationships, r => r.FromTable == "TarjetasCredito" || r.ToTable == "TarjetasCredito");
    }

    [Fact]
    public async Task Dependencias()
    {
        IntegrationEnvironment.RequireDatabase();
        var clientes = await _service.GetDependenciesAsync(_profile, null, "ventas", "Clientes", DependencyDirection.Both, "it", Ct);
        Assert.NotNull(clientes.ReferencedBy);
        Assert.Contains(clientes.ReferencedBy, d => d.Name == "vClientesActivos" && d.Type == "VIEW" && d.Schema == "ventas");
        Assert.Contains(clientes.ReferencedBy, d => d.Name == "usp_ClientesPorSaldo");
        Assert.Contains(clientes.ReferencedBy, d => d.Name == "usp_DesactivarCliente");

        var function = await _service.GetDependenciesAsync(_profile, null, "ventas", "fn_TotalCliente", DependencyDirection.Referenced, "it", Ct);
        Assert.Null(function.ReferencedBy);
        Assert.NotNull(function.References);
        Assert.Contains(function.References, d => d.Name == "Facturas" && d.Schema == "ventas" && d.IsResolved && d.Type == "USER_TABLE");

        var referencing = await _service.GetDependenciesAsync(_profile, null, "ventas", "Facturas", DependencyDirection.Referencing, "it", Ct);
        Assert.Null(referencing.References);
        Assert.Contains(referencing.ReferencedBy!, d => d.Name == "fn_TotalCliente");
    }

    [Fact]
    public async Task Busqueda_PorNombreYDefinicion()
    {
        IntegrationEnvironment.RequireDatabase();
        var byName = await _service.SearchObjectsAsync(_profile, null, "Clientes", SearchScope.Name, SearchObjectType.Any, 0, 100, "it", Ct);
        Assert.Contains(byName.Items, m => m.Name == "Clientes" && m.MatchedIn == "name" && m.Type == "USER_TABLE");
        Assert.Contains(byName.Items, m => m.Name == "usp_ClientesPorSaldo");
        Assert.All(byName.Items, m => Assert.Null(m.Snippet));

        var byDefinition = await _service.SearchObjectsAsync(_profile, null, "saldo >= @SaldoMinimo", SearchScope.Definition, SearchObjectType.Any, 0, 100, "it", Ct);
        var match = Assert.Single(byDefinition.Items);
        Assert.Equal("usp_ClientesPorSaldo", match.Name);
        Assert.Equal("definition", match.MatchedIn);
        Assert.Contains("Saldo >= @SaldoMinimo", match.Snippet, StringComparison.Ordinal);
        Assert.True(match.Snippet!.Length <= "Saldo >= @SaldoMinimo".Length + 130);

        var both = await _service.SearchObjectsAsync(_profile, null, "Clientes", SearchScope.Both, SearchObjectType.Procedure, 0, 100, "it", Ct);
        Assert.Contains(both.Items, m => m.Name == "usp_ClientesPorSaldo" && m.MatchedIn == "both");
        Assert.Contains(both.Items, m => m.Name == "usp_DesactivarCliente" && m.MatchedIn == "definition");
        Assert.All(both.Items, m => Assert.Equal("SQL_STORED_PROCEDURE", m.Type));

        var paged = await _service.SearchObjectsAsync(_profile, null, "Clientes", SearchScope.Both, SearchObjectType.Any, 0, 1, "it", Ct);
        Assert.Single(paged.Items);
        Assert.True(paged.HasMore);
        Assert.True(paged.TotalCount > 1);
    }

    [Fact]
    public async Task EstadisticasDeTablas()
    {
        IntegrationEnvironment.RequireDatabase();
        var stats = await _service.GetTableStatsAsync(_profile, null, "ventas", null, 0, 100, "it", Ct);
        Assert.DoesNotContain(stats.Items, s => s.Name == "TarjetasCredito");
        var facturas = Assert.Single(stats.Items, s => s.Name == "Facturas");
        Assert.True(facturas.ApproximateRows > 0);
        Assert.True(facturas.ReservedKb >= facturas.UsedKb);
        Assert.True(facturas.UsedKb > 0);
        Assert.Equal(facturas.UsedKb - facturas.DataKb, facturas.IndexKb);
        Assert.True(facturas.DataKb > 0);

        var single = await _service.GetTableStatsAsync(_profile, null, "ventas", "Facturas", 0, 100, "it", Ct);
        Assert.Equal("Facturas", Assert.Single(single.Items).Name);
        Assert.Equal(1, single.TotalCount);
    }

    [Fact]
    public async Task Exclusiones_NoAparecenEnNingunaRespuesta()
    {
        IntegrationEnvironment.RequireDatabase();
        var responses = new List<object>
        {
            await _service.ListSchemasAsync(_profile, null, "it", Ct),
            await _service.ListTablesAsync(_profile, null, null, null, true, 0, 500, "it", Ct),
            await _service.DescribeTableAsync(_profile, null, "ventas", "Usuarios", "it", Ct),
            await _service.DescribeTableAsync(_profile, null, "ventas", "Clientes", "it", Ct),
            await _service.ListRoutinesAsync(_profile, null, null, null, RoutineTypeFilter.Any, 0, 500, "it", Ct),
            await _service.GetRelationshipsAsync(_profile, null, null, null, "it", Ct),
            await _service.GetDependenciesAsync(_profile, null, "ventas", "Clientes", DependencyDirection.Both, "it", Ct),
            await _service.SearchObjectsAsync(_profile, null, "Tarjetas", SearchScope.Both, SearchObjectType.Any, 0, 500, "it", Ct),
            await _service.SearchObjectsAsync(_profile, null, "Contrasena", SearchScope.Both, SearchObjectType.Any, 0, 500, "it", Ct),
            await _service.SearchObjectsAsync(_profile, null, "seguridad", SearchScope.Both, SearchObjectType.Any, 0, 500, "it", Ct),
            await _service.SearchObjectsAsync(_profile, null, "*", SearchScope.Name, SearchObjectType.Any, 0, 500, "it", Ct),
            await _service.GetTableStatsAsync(_profile, null, null, null, 0, 500, "it", Ct),
            await _service.ListIndexesAsync(_profile, null, "ventas", "Usuarios", "it", Ct),
            await _service.ListObjectsAsync(_profile, null, CatalogObjectKind.Trigger, null, 0, 500, "it", Ct),
            await _service.ListObjectsAsync(_profile, null, CatalogObjectKind.Synonym, null, 0, 500, "it", Ct),
            await _service.ListObjectsAsync(_profile, null, CatalogObjectKind.Sequence, null, 0, 500, "it", Ct),
            await _service.ListObjectsAsync(_profile, null, CatalogObjectKind.Type, null, 0, 500, "it", Ct),
        };

        foreach (var response in responses)
        {
            var json = JsonSerializer.Serialize(response, response.GetType(), Json);
            Assert.DoesNotContain("TarjetasCredito", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Contrasena", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seguridad", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Claves", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ListObjects_BaseEstandarSinObjetosOcultos()
    {
        IntegrationEnvironment.RequireDatabase();
        var types = await _service.ListObjectsAsync(_profile, null, CatalogObjectKind.Type, "ventas", 0, 100, "it", Ct);
        Assert.Equal(IntegrationEnvironment.Database, types.Database);
        Assert.Equal(types.Items.Count, types.TotalCount);
        Assert.All(types.Items, t => Assert.Equal("type", t.Kind));
    }
}
