using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;
using SqlServerMcp.Tools;

namespace SqlServerMcp.Tests.Policy;

/// <summary>
/// Correcciones de la auditoría de seguridad: evasión por sinónimos (crítica), columnas sensibles y PII (media) y umbral
/// de filas troceado en una transacción (baja).
/// </summary>
public sealed class AuditFixesTests
{
    // ------------------------------------------------------------------ 2. Columnas sensibles y PII

    [Theory]
    [InlineData("SSN")]
    [InlineData("CustomerSSN")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("API-KEY")]
    [InlineData("APIKey")]
    [InlineData("PinCode")]
    [InlineData("PasswordHash")]
    [InlineData("userpassword")]
    [InlineData("Contraseña")]
    [InlineData("NumeroTarjeta")]
    [InlineData("CreditCardNumber")]
    [InlineData("credit_card_number")]
    [InlineData("RefreshToken")]
    [InlineData("Token")]
    [InlineData("ClientSecret")]
    [InlineData("PrivateKey")]
    [InlineData("AccessKeyId")]
    [InlineData("OtpCode")]
    [InlineData("Iban")]
    [InlineData("Cvc")]
    [InlineData("PasswordSalt")]
    [InlineData("ClaveAcceso")]
    [InlineData("NumeroCuenta")]
    [InlineData("cuenta_bancaria")]
    [InlineData("SocialSecurityNumber")]
    public void Secretos_DetectadosPorSegmentos(string column) =>
        Assert.True(TestProfiles.Default.IsColumnSensitive(null, null, column), column);

    [Theory]
    [InlineData("ShippingMethod")]
    [InlineData("MappingId")]
    [InlineData("TokenizerName")]
    [InlineData("Spinner")]
    [InlineData("Saltillo")]
    [InlineData("Nombre")]
    [InlineData("Email")]
    public void NoSecretos_SinFalsosPositivos(string column) =>
        Assert.False(TestProfiles.Default.IsColumnSensitive(null, null, column), column);

    [Theory]
    [InlineData("Email", true)]
    [InlineData("EmailAddress", true)]
    [InlineData("CorreoElectronico", true)]
    [InlineData("Telefono", true)]
    [InlineData("Teléfono", true)]
    [InlineData("PhoneNumber", true)]
    [InlineData("TelefonoMovil", true)]
    [InlineData("Direccion", true)]
    [InlineData("FechaNacimiento", true)]
    [InlineData("fecha_nacimiento", true)]
    [InlineData("DNI", true)]
    [InlineData("Pasaporte", true)]
    [InlineData("Nombre", false)]
    [InlineData("ShippingMethod", false)]
    [InlineData("Emailing", false)]
    public void Pii_DetectadaPorSegmentos(string column, bool expected) =>
        Assert.Equal(expected, TestProfiles.Default.IsColumnPii(null, null, column));

    [Fact]
    public void A07_FiltroSobreColumnaSensible_Bloqueado() =>
        Policies.AssertBlockedWithPrefix("SELECT SSN FROM dbo.T WHERE SSN = '1';", ToolKind.Query, "POL-DAT");

    [Fact]
    public void A08_EscrituraEnColumnaSensible_Bloqueada() =>
        Policies.AssertBlockedWithPrefix("UPDATE dbo.T SET ApiKey = N'x' WHERE Id = 1;", ToolKind.Dml, "POL-DAT");

    [Fact]
    public void A09_SecretosEnLaListaSelect_SeEnmascaran()
    {
        var evaluation = Policies.Evaluate("SELECT Id, SSN, ApiKey FROM dbo.T;", ToolKind.Query);
        Assert.Equal(PolicyAction.Allow, evaluation.Action);
        Assert.Equal([1, 2], evaluation.MaskOrdinals);
    }

    [Fact]
    public void A10_PiiEnModoMask_SeEnmascara_YSePuedeFiltrar()
    {
        var masked = Policies.Evaluate("SELECT Email, Telefono FROM dbo.T;", ToolKind.Query);
        Assert.Equal(PolicyAction.Allow, masked.Action);
        Assert.Equal([0, 1], masked.MaskOrdinals);

        var filter = Policies.Evaluate("SELECT Id FROM dbo.T WHERE Email = @correo;", ToolKind.Query);
        Assert.Equal(PolicyAction.Allow, filter.Action);
        Assert.Empty(filter.MaskOrdinals);
    }

    [Fact]
    public void PiiEnModoAllow_NoSeEnmascara()
    {
        var profile = TestProfiles.Create(c => c.Pii.Action = PiiColumnAction.Allow);
        var evaluation = Policies.Evaluate("SELECT Email, Telefono FROM dbo.T;", ToolKind.Query, profile);
        Assert.Equal(PolicyAction.Allow, evaluation.Action);
        Assert.Empty(evaluation.MaskOrdinals);
    }

    [Fact]
    public void A14_ColumnasConNombresParecidos_NoSeMarcan()
    {
        var evaluation = Policies.Evaluate("SELECT ShippingMethod, MappingId, TokenizerName FROM dbo.T;", ToolKind.Query);
        Assert.Equal(PolicyAction.Allow, evaluation.Action);
        Assert.Empty(evaluation.MaskOrdinals);
    }

    // ------------------------------------------------------------------ 1a. CREATE SYNONYM (reglas estáticas)

    [Theory]
    [InlineData("CREATE SYNONYM ia.s1 FOR sp_executesql;", "POL-DYN-001")]
    [InlineData("CREATE SYNONYM ia.s2 FOR xp_cmdshell;", "POL-EXT-003")]
    [InlineData("CREATE SYNONYM ia.s3 FOR dbo.sp_executesql;", "POL-DYN-001")]
    [InlineData("CREATE SYNONYM ia.s4 FOR sp_OACreate;", "POL-")]
    [InlineData("CREATE SYNONYM ia.s5 FOR master.sys.sql_logins;", "POL-CAT-001")]
    [InlineData("CREATE SYNONYM ia.s6 FOR OtraBD.dbo.Tabla;", "POL-REF-001")]
    [InlineData("CREATE SYNONYM ia.s7 FOR sys.objects;", "POL-SYN-001")]
    [InlineData("CREATE SYNONYM ia.s8 FOR sp_helptext;", "POL-SYN-001")]
    [InlineData("CREATE SYNONYM ia.s9 FOR mssqlsystemresource.sys.objects;", "POL-SYN-001")]
    [InlineData("CREATE SYNONYM ia.s10 FOR seguridad.Claves;", "POL-DAT-001")]
    [InlineData("CREATE SYNONYM ia.s11 FOR msdb.dbo.sp_send_dbmail;", "POL-")]
    [InlineData("CREATE SYNONYM ia.s12 FOR otroServidor.VentasTest.dbo.T;", "POL-EXT-002")]
    public void A01a04_SinonimoHaciaObjetoDelSistemaOFueraDelAlcance_Bloqueado(string sql, string rulePrefix)
    {
        // Misma decisión en execute_ddl y en validate_sql sin herramienta destino.
        Policies.AssertBlockedWithPrefix(sql, ToolKind.Ddl, rulePrefix);
        Policies.AssertBlockedWithPrefix(sql, ToolKind.Any, rulePrefix);
    }

    [Fact]
    public void A12_SinonimoHaciaTablaDeUsuario_Permitido() =>
        Policies.AssertDecision("CREATE SYNONYM ia.sProd FOR dbo.Producto;", ToolKind.Ddl, PolicyAction.Allow, "POL-DDL-CREATE");

    // ------------------------------------------------------------------ Reescritura de sinónimos

    [Fact]
    public void BaseObjectName_SeInterpretaConCorchetes()
    {
        Assert.Equal(new SqlObjectName(null, "master", "sys", "sql_logins"), SynonymRewriter.ParseBaseObjectName("[master].[sys].[sql_logins]"));
        Assert.Equal(new SqlObjectName(null, null, null, "sp_executesql"), SynonymRewriter.ParseBaseObjectName("[sp_executesql]"));
        Assert.Equal(new SqlObjectName(null, null, "dbo", "Producto"), SynonymRewriter.ParseBaseObjectName("[dbo].[Producto]"));
        Assert.Null(SynonymRewriter.ParseBaseObjectName("[sin cierre"));
    }

    [Fact]
    public void Reescritura_EnCuerpoDeProcedimiento_RevelaSqlDinamico()
    {
        var sql = "CREATE PROCEDURE ia.pUsaSinonimo @s nvarchar(max) AS EXEC dbo.syn_Ejecutar @s;";
        var rewritten = SynonymRewriter.Rewrite(sql, new SqlObjectName(null, null, "dbo", "syn_Ejecutar"), new SqlObjectName(null, null, null, "sp_executesql"), out var count);
        Assert.Equal(1, count);
        Assert.Contains("sp_executesql", rewritten, StringComparison.OrdinalIgnoreCase);
        Policies.AssertDecision(rewritten!, ToolKind.Ddl, PolicyAction.Block, "POL-DYN-001");
    }

    [Fact]
    public void Reescritura_DeFuncionEscalar()
    {
        var rewritten = SynonymRewriter.Rewrite("SELECT dbo.synTotal(1) AS t;", new SqlObjectName(null, null, "dbo", "synTotal"), new SqlObjectName(null, null, "ventas", "fn_TotalCliente"), out var count);
        Assert.Equal(1, count);
        Assert.Contains("[ventas].[fn_TotalCliente]", rewritten, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 1c. Análisis de la definición de módulos

    [Fact]
    public void Modulo_ConExecDeCadena_Bloqueado() =>
        AssertModule("CREATE PROCEDURE dbo.pDinamico @sql nvarchar(max) AS EXEC(@sql);", PolicyAction.Block, "POL-DYN-001");

    [Fact]
    public void Modulo_ConSpExecutesql_Bloqueado() =>
        AssertModule("CREATE PROCEDURE dbo.pDinamico @sql nvarchar(max) AS EXEC sp_executesql @sql;", PolicyAction.Block, "POL-DYN-001");

    [Fact]
    public void Modulo_ConsultaNormal_FueraDeLosEsquemasDeDdl_Permitido() =>
        AssertModule("CREATE PROCEDURE dbo.pConsulta @id int AS SELECT ClienteId, Nombre FROM ventas.Clientes WHERE ClienteId = @id;", PolicyAction.Allow, "POL-PRC-001");

    [Fact]
    public void Modulo_ConDrop_RequiereConfirmacion() =>
        AssertModule("CREATE PROCEDURE dbo.pLimpia AS DROP TABLE ia.Temporal;", PolicyAction.Confirm, "POL-DDL-DROP");

    [Fact]
    public void Modulo_ConExecuteAsOwner_Bloqueado()
    {
        var evaluation = Policies.Engine.EvaluateModule("CREATE PROCEDURE dbo.pDueno WITH EXECUTE AS OWNER AS SELECT 1 AS uno;", TestProfiles.Default);
        Assert.Equal(PolicyAction.Block, evaluation.Action);
        Assert.StartsWith("POL-IMP", evaluation.RuleId, StringComparison.Ordinal);
    }

    [Fact]
    public void Modulo_DefinicionQueNoEsUnProcedimiento_Bloqueada() =>
        AssertModule("SELECT 1 AS uno;", PolicyAction.Block, "POL-PRC-DEF-001");

    private static void AssertModule(string definition, PolicyAction action, string ruleId) =>
        Policies.AssertDecision(Policies.Engine.EvaluateModule(definition, TestProfiles.Default), action, ruleId);

    // ------------------------------------------------------------------ 3. Acumulado por transacción

    private const string Insert = "INSERT INTO ia.Carga (Valor) VALUES (1);";

    [Fact]
    public void A11_SegundoInsertEnTransaccion_SuperaElUmbralAcumulado_Confirma()
    {
        var evaluation = Policies.Evaluate(Insert, ToolKind.Dml, TestProfiles.EndToEnd);
        var first = Policies.Engine.ApplyRuntimeFacts(evaluation, new RuntimeFacts(90, null, TransactionRows: 0), TestProfiles.EndToEnd);
        Assert.Equal(PolicyAction.Allow, first.Action);

        var second = Policies.Engine.ApplyRuntimeFacts(evaluation, new RuntimeFacts(90, null, TransactionRows: 90), TestProfiles.EndToEnd);
        Policies.AssertDecision(second, PolicyAction.Confirm, "POL-DML-TX-THRESHOLD");
        Assert.Contains("180", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A15_FueraDeTransaccion_NoSeAcumula()
    {
        var evaluation = Policies.Evaluate(Insert, ToolKind.Dml, TestProfiles.EndToEnd);
        Assert.Equal(PolicyAction.Allow, Policies.Engine.ApplyRuntimeFacts(evaluation, new RuntimeFacts(90, null), TestProfiles.EndToEnd).Action);
    }

    [Fact]
    public void AcumuladoSobreElMaximo_Rechazado()
    {
        var evaluation = Policies.Evaluate(Insert, ToolKind.Dml, TestProfiles.EndToEnd);
        var result = Policies.Engine.ApplyRuntimeFacts(evaluation, new RuntimeFacts(90, null, TransactionRows: 950), TestProfiles.EndToEnd);
        Policies.AssertDecision(result, PolicyAction.Block, "POL-LIM-TX");
        Assert.Equal(ErrorCodes.LimitExceeded, result.ErrorCode);
    }

    // ------------------------------------------------------------------ 1b y 1c. Política con catálogo (sinónimos y definiciones)

    [Fact]
    public async Task A05_ProcedimientoQueUsaSinonimoCreadoFueraDelMcp_Bloqueado()
    {
        var catalog = new FakeCatalog().Synonym("dbo", "syn_Ejecutar", "[sp_executesql]");
        var evaluation = await Statements(catalog).EvaluateAsync("CREATE PROCEDURE ia.pUsa @s nvarchar(max) AS EXEC dbo.syn_Ejecutar @s;", ToolKind.Ddl, TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-DYN-001");
        Assert.Contains("sinónimo", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A13_ConsultaATravesDeSinonimoHaciaTablaDeUsuario_Permitida()
    {
        var catalog = new FakeCatalog().Synonym("dbo", "sProd", "[dbo].[Producto]");
        var evaluation = await Statements(catalog).EvaluateAsync("SELECT ProductoId, SKU FROM dbo.sProd WHERE ProductoId = 1;", ToolKind.Query, TestProfiles.Default, "t", default);
        Assert.Equal(PolicyAction.Allow, evaluation.Action);
    }

    [Theory]
    [InlineData("[master].[sys].[sql_logins]", "POL-CAT-001")]
    [InlineData("[OtraBD].[dbo].[Tabla]", "POL-REF-001")]
    [InlineData("[seguridad].[Claves]", "POL-DAT-001")]
    public async Task SinonimoCreadoFueraDelMcp_HaciaObjetoProhibido_Bloqueado(string baseObject, string ruleId)
    {
        var catalog = new FakeCatalog().Synonym("dbo", "sExterno", baseObject);
        var evaluation = await Statements(catalog).EvaluateAsync("SELECT * FROM dbo.sExterno;", ToolKind.Query, TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, ruleId);
    }

    [Fact]
    public async Task SinonimoHaciaTablaConColumnaSensible_SeFiltraIgual()
    {
        var catalog = new FakeCatalog().Synonym("dbo", "sT", "[dbo].[T]");
        var evaluation = await Statements(catalog).EvaluateAsync("SELECT Id FROM dbo.sT WHERE SSN = '1';", ToolKind.Query, TestProfiles.Default, "t", default);
        Assert.Equal(PolicyAction.Block, evaluation.Action);
    }

    [Fact]
    public async Task CatalogoNoDisponible_FailClosed()
    {
        var catalog = new FakeCatalog { Fail = true };
        var evaluation = await Statements(catalog).EvaluateAsync("SELECT ClienteId FROM ventas.Clientes;", ToolKind.Query, TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-SYN-002");
    }

    [Theory]
    [InlineData("U", PolicyAction.Allow)]
    [InlineData("P", PolicyAction.Allow)]
    [InlineData("SN", PolicyAction.Block)]
    [InlineData("X", PolicyAction.Block)]
    [InlineData("SO", PolicyAction.Block)]
    public async Task CreateSynonym_TipoDelDestino(string type, PolicyAction expected)
    {
        var catalog = new FakeCatalog().Object("dbo", "Producto", type);
        var evaluation = await Statements(catalog).EvaluateAsync("CREATE SYNONYM ia.sProd FOR dbo.Producto;", ToolKind.Ddl, TestProfiles.Default, "t", default);
        Assert.Equal(expected, evaluation.Action);
        if (expected == PolicyAction.Block)
        {
            Assert.Equal("POL-SYN-001", evaluation.RuleId);
        }
    }

    [Fact]
    public async Task CreateSynonym_DestinoInexistente_Bloqueado()
    {
        var evaluation = await Statements(new FakeCatalog()).EvaluateAsync("CREATE SYNONYM ia.sProd FOR dbo.NoExiste;", ToolKind.Ddl, TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-SYN-001");
    }

    [Fact]
    public async Task A06_ProcedimientoCreadoFueraDelMcpConExecDeCadena_Bloqueado()
    {
        var catalog = new FakeCatalog().Object("dbo", "pDinamico", "P", "CREATE PROCEDURE dbo.pDinamico @sql nvarchar(max) AS EXEC(@sql);");
        var evaluation = await Statements(catalog).EvaluateProcedureAsync(new SqlObjectName(null, null, "dbo", "pDinamico"), TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-DYN-001");
    }

    [Fact]
    public async Task ProcedimientoQueInvocaOtroConSqlDinamico_Bloqueado()
    {
        var catalog = new FakeCatalog()
            .Object("dbo", "pA", "P", "CREATE PROCEDURE dbo.pA AS EXEC dbo.pB;")
            .Object("dbo", "pB", "P", "CREATE PROCEDURE dbo.pB AS BEGIN DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC(@s); END");
        var evaluation = await Statements(catalog).EvaluateProcedureAsync(new SqlObjectName(null, null, "dbo", "pA"), TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-DYN-001");
    }

    [Fact]
    public async Task ProcedimientoSinDefinicionVisible_Bloqueado()
    {
        var catalog = new FakeCatalog().Object("dbo", "pCifrado", "P");
        var evaluation = await Statements(catalog).EvaluateProcedureAsync(new SqlObjectName(null, null, "dbo", "pCifrado"), TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-PRC-DEF-001");
    }

    [Fact]
    public async Task ProcedimientoATravesDeSinonimoHaciaSpExecutesql_Bloqueado()
    {
        var catalog = new FakeCatalog().Object("dbo", "syn_Ejecutar", "SN", synonymBase: "[sp_executesql]");
        var evaluation = await Statements(catalog).EvaluateProcedureAsync(new SqlObjectName(null, null, "dbo", "syn_Ejecutar"), TestProfiles.Default, "t", default);
        Policies.AssertDecision(evaluation, PolicyAction.Block, "POL-DYN-001");
    }

    [Fact]
    public async Task ProcedimientoLegitimo_Permitido_YLaDefinicionSeCachea()
    {
        var catalog = new FakeCatalog().Object("dbo", "pConsulta", "P", "CREATE PROCEDURE dbo.pConsulta @id int AS SELECT ClienteId FROM ventas.Clientes WHERE ClienteId = @id;");
        var statements = Statements(catalog);
        for (var i = 0; i < 2; i++)
        {
            var evaluation = await statements.EvaluateProcedureAsync(new SqlObjectName(null, null, "dbo", "pConsulta"), TestProfiles.Default, "t", default);
            Assert.Equal(PolicyAction.Allow, evaluation.Action);
        }
    }

    private static StatementPolicy Statements(ICatalogResolver catalog) => new(Policies.Engine, catalog);

    /// <summary>Catálogo simulado: sinónimos y objetos (tipo, definición, base de sinónimo).</summary>
    private sealed class FakeCatalog : ICatalogResolver
    {
        private readonly List<SynonymEntry> _synonyms = [];
        private readonly List<ModuleCatalogEntry> _objects = [];

        public bool Fail { get; init; }

        public FakeCatalog Synonym(string schema, string name, string baseObject)
        {
            _synonyms.Add(new SynonymEntry(schema, name, baseObject));
            return this;
        }

        public FakeCatalog Object(string schema, string name, string type, string? definition = null, string? synonymBase = null)
        {
            _objects.Add(new ModuleCatalogEntry("VentasTest", schema, name, type, definition, synonymBase));
            if (type == "SN" && synonymBase is not null)
            {
                _synonyms.Add(new SynonymEntry(schema, name, synonymBase));
            }

            return this;
        }

        public Task<IReadOnlyList<SynonymEntry>> GetSynonymsAsync(ProfileRuntime profile, string database, string correlationId, CancellationToken cancellationToken) =>
            Fail ? throw new InvalidOperationException("catálogo no disponible") : Task.FromResult<IReadOnlyList<SynonymEntry>>(_synonyms);

        public Task<IReadOnlyList<ModuleCatalogEntry>> GetObjectsAsync(ProfileRuntime profile, SqlObjectName name, string correlationId, CancellationToken cancellationToken) =>
            Fail
                ? throw new InvalidOperationException("catálogo no disponible")
                : Task.FromResult<IReadOnlyList<ModuleCatalogEntry>>(_objects
                    .Where(o => string.Equals(o.Name, name.Name, StringComparison.OrdinalIgnoreCase)
                                && (string.IsNullOrEmpty(name.Schema) || string.Equals(o.Schema, name.Schema, StringComparison.OrdinalIgnoreCase)))
                    .ToList());
    }
}
