using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Policy;

/// <summary>
/// Estructura (RS-POL-01/02/03, RS-INJ-02), normalización y hash (RS-HIL-03), determinismo y concurrencia
/// (RS-POL-11), rendimiento (RNF-01) y coherencia del catálogo de reglas (RNF-11).
/// </summary>
public sealed class StructuralAndCatalogTests
{
    [Fact]
    public void TamanoMaximo_LimitExceeded()
    {
        var profile = TestProfiles.Create(c => c.Limits.MaxStatementBytes = 100);
        var sql = "SELECT ClienteId FROM ventas.Clientes WHERE Nombre = '" + new string('x', 100) + "'";
        var e = Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Block, "POL-SIZE-001", profile);
        Assert.Equal(ErrorCodes.LimitExceeded, e.ErrorCode);
        Assert.False(e.Statement.Parsed);
        Assert.Equal(sql, e.Statement.NormalizedSql);
    }

    [Fact]
    public void TamanoMaximo_SeMideEnBytesUtf8()
    {
        var profile = TestProfiles.Create(c => c.Limits.MaxStatementBytes = 60);
        var sql = "SELECT N'" + new string('ñ', 25) + "' AS a";
        Assert.True(sql.Length < 60);
        Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Block, "POL-SIZE-001", profile);
    }

    [Fact]
    public void ErrorDeSintaxis_IndicaLineaYColumna()
    {
        var e = Policies.AssertDecision("SELECT ClienteId\nFROM ventas.Clientes WHERE", ToolKind.Query, PolicyAction.Block, "POL-PARSE-001");
        Assert.False(e.Statement.Parsed);
        Assert.NotEmpty(e.Statement.ParseErrors);
        Assert.True(e.Statement.ParseErrors[0].Number > 0);
        Assert.Contains("línea 2", e.Reason, StringComparison.Ordinal);
        Assert.Contains("columna", e.Reason, StringComparison.Ordinal);
        Assert.Null(e.ErrorCode);
    }

    [Fact]
    public void TextoNulo_SeTrataComoVacio()
    {
        Policies.AssertDecision(null!, ToolKind.Query, PolicyAction.Block, "POL-PARSE-002");
    }

    [Fact]
    public void InformacionDeLaSentencia_FragmentoNormalizacionYHash()
    {
        var e = Policies.Evaluate("delete   from ia.T   where Id = 1 -- comentario", ToolKind.Dml);
        Assert.IsType<DeleteStatement>(e.Statement.Fragment);
        Assert.Equal("DeleteStatement", e.Statement.StatementType);
        Assert.Equal(1, e.Statement.BatchCount);
        Assert.Equal(1, e.Statement.StatementCount);
        Assert.Equal("DELETE ia.T WHERE Id = 1", e.Statement.NormalizedSql);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(e.Statement.NormalizedSql)));
        Assert.Equal(expected, e.Statement.Hash);
        Assert.Equal(new SqlObjectName(null, null, "ia", "T"), e.Statement.TargetObject);
    }

    [Fact]
    public void Hash_CambiaSiCambiaLaSentencia()
    {
        var a = Policies.Evaluate("DELETE FROM ia.T WHERE Id = 1", ToolKind.Dml);
        var b = Policies.Evaluate("DELETE FROM ia.T WHERE Id = 2", ToolKind.Dml);
        Assert.NotEqual(a.Statement.Hash, b.Statement.Hash);
    }

    [Fact]
    public void ColumnasYFunciones_SeInformanConAliasResueltos()
    {
        var e = Policies.Evaluate("SELECT c.Nombre, UPPER(f.Numero) AS n FROM ventas.Clientes c JOIN ventas.Facturas f ON f.ClienteId = c.ClienteId", ToolKind.Query);
        Assert.Contains(e.Statement.Columns, c => c.Column == "Nombre" && c.Table == "Clientes" && c.Schema == "ventas" && c.InSelectList && c.OutputOrdinal == 0);
        Assert.Contains(e.Statement.Columns, c => c.Column == "Numero" && c.Table == "Facturas" && !c.InSelectList);
        Assert.Contains("UPPER", e.Statement.Functions);
        Assert.Equal(2, e.Statement.Objects.Count(o => o.Role == ObjectRole.Read));
    }

    [Fact]
    public void Determinismo_MismaEntradaMismaDecision()
    {
        const string sql = "UPDATE t SET a = 1 OUTPUT deleted.Password INTO ventas.TarjetasCredito (x) FROM seguridad.T t WHERE 1 = 1";
        var first = Policies.Evaluate(sql, ToolKind.Dml);
        for (var i = 0; i < 5; i++)
        {
            var again = Policies.Evaluate(sql, ToolKind.Dml);
            Assert.Equal(first.RuleId, again.RuleId);
            Assert.Equal(first.Hits.Select(h => h.RuleId + h.Reason), again.Hits.Select(h => h.RuleId + h.Reason));
            Assert.Equal(first.Statement.Hash, again.Statement.Hash);
        }
    }

    [Fact]
    public void Concurrencia_EvaluacionesParalelasCoincidenConLasSecuenciales()
    {
        string[] corpus =
        [
            "SELECT ClienteId, Nombre FROM ventas.Clientes WHERE ClienteId = 1",
            "DELETE FROM ia.T",
            "EXEC master..xp_cmdshell 'dir'",
            "CREATE PROCEDURE ia.p AS ALTER ROLE db_owner ADD MEMBER x",
            "SELECT Password FROM ventas.Usuarios",
            "SELECT 1; SELECT 2",
        ];
        var expected = corpus.Select(s => Policies.Evaluate(s, ToolKind.Any)).Select(e => (e.Action, e.RuleId, e.Statement.Hash)).ToArray();
        var results = new (PolicyAction, string, string)[corpus.Length * 40];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var e = Policies.Evaluate(corpus[i % corpus.Length], ToolKind.Any);
            results[i] = (e.Action, e.RuleId, e.Statement.Hash);
        });

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(expected[i % corpus.Length], results[i]);
        }
    }

    [Fact]
    public void Rendimiento_SentenciaTipicaDe10KB()
    {
        var sb = new StringBuilder("SELECT c.ClienteId, c.Nombre");
        var i = 0;
        while (sb.Length < 9_500)
        {
            sb.Append(", CASE WHEN f.Total > ").Append(i).Append(" THEN c.Nombre ELSE f.Numero END AS col").Append(i)
              .Append(", ISNULL(f.Total, 0) + ").Append(i).Append(" AS t").Append(i);
            i++;
        }

        sb.Append(" FROM ventas.Clientes c JOIN ventas.Facturas f ON f.ClienteId = c.ClienteId")
          .Append(" WHERE c.ClienteId IN (SELECT ClienteId FROM ventas.Facturas WHERE Total > 100) AND f.Fecha >= '2025-01-01'")
          .Append(" ORDER BY c.Nombre");
        var sql = sb.ToString();
        Assert.InRange(Encoding.UTF8.GetByteCount(sql), 9_500, 11_000);

        for (var w = 0; w < 3; w++)
        {
            Policies.AssertDecision(sql, ToolKind.Query, PolicyAction.Allow, "POL-QRY-001");
        }

        var times = new List<double>();
        for (var r = 0; r < 15; r++)
        {
            var watch = Stopwatch.StartNew();
            Policies.Evaluate(sql, ToolKind.Query);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        // RNF-01: < 50 ms típico; la cota de la prueba es holgada para equipos de integración continua lentos.
        Assert.True(median < 150, $"Mediana de evaluación: {median:0.0} ms");
    }

    [Fact]
    public void Clasificador_TodoTipoDeSentenciaTieneUnaCategoriaYLosNoCatalogadosSeBloquean()
    {
        var statementTypes = typeof(TSqlStatement).Assembly.GetTypes()
            .Where(t => typeof(TSqlStatement).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.True(statementTypes.Count > 300);

        Type[] required =
        [
            typeof(SelectStatement), typeof(InsertStatement), typeof(UpdateStatement), typeof(DeleteStatement), typeof(MergeStatement),
            typeof(CreateTableStatement), typeof(DropTableStatement), typeof(CreateProcedureStatement), typeof(CreateOrAlterViewStatement),
            typeof(GrantStatement), typeof(ExecuteAsStatement), typeof(DropDatabaseStatement), typeof(BulkInsertStatement),
            typeof(WaitForStatement), typeof(UseStatement), typeof(BeginTransactionStatement), typeof(PredicateSetStatement),
        ];
        Assert.All(required, t => Assert.True(StatementClassifier.IsCatalogued(t), t.Name));
        Assert.Contains(statementTypes, t => !StatementClassifier.IsCatalogued(t));
    }

    [Fact]
    public void CatalogoDeReglas_IdentificadoresUnicosYDocumentados()
    {
        var rules = PolicyRules.All;
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
            Assert.False(string.IsNullOrWhiteSpace(r.Suggestion));
            Assert.Equal(!r.Configurable, r.Immutable);
        });

        var document = File.ReadAllText(Path.Combine(FindSolutionRoot(), "docs", "POLICY_RULES.md"));
        var missing = rules.Where(r => !document.Contains("`" + r.Id + "`", StringComparison.Ordinal)).Select(r => r.Id).ToList();
        Assert.True(missing.Count == 0, "Reglas sin documentar en docs/POLICY_RULES.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void ReglasCriticas_GeneranAlertaCritica()
    {
        Assert.All(
            PolicyRules.All.Where(r => r.Id.StartsWith("POL-DCL", StringComparison.Ordinal)
                || r.Id.StartsWith("POL-IMP", StringComparison.Ordinal)
                || r.Id.StartsWith("POL-ADM", StringComparison.Ordinal)
                || r.Id.StartsWith("POL-EXT", StringComparison.Ordinal)),
            r =>
            {
                Assert.Equal(AlertSeverity.Critical, r.Severity);
                Assert.Equal(RiskLevel.Critical, r.Risk);
                Assert.Equal(PolicyAction.Block, r.Action);
            });
    }

    private static string FindSolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SqlServerMcp.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No se encontró la raíz de la solución (SqlServerMcp.slnx).");
    }
}
