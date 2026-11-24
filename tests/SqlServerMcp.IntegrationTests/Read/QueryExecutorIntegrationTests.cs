using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using static SqlServerMcp.IntegrationTests.Read.ReadTestSupport;

namespace SqlServerMcp.IntegrationTests.Read;

/// <summary>Ejecución de lectura contra SQL Server real (RF-QRY-01..13, RS-LIM-01/02, RS-DAT-02/03, RF-OUT-02, CA-20, CA-21).</summary>
[Collection(ReadIntegrationCollection.Name)]
public class QueryExecutorIntegrationTests
{
    private const string AllTypesSql = """
        SELECT CAST(NULL AS int) AS nulo, CAST(1 AS bit) AS b, CAST(255 AS tinyint) AS ti, CAST(-32768 AS smallint) AS si,
               CAST(2147483647 AS int) AS i, CAST(9223372036854775807 AS bigint) AS bi,
               CAST(1.5 AS real) AS r, CAST(0.1 AS float) AS f,
               CAST('12345678901234567890.123456789' AS decimal(38,9)) AS d, CAST(123.45 AS numeric(10,3)) AS n,
               CAST(922337203685477.5807 AS money) AS m, CAST(-214748.3648 AS smallmoney) AS sm,
               CAST('2024-02-29' AS date) AS dt, CAST('2024-02-29T13:14:15.500' AS datetime) AS dtm,
               CAST('2024-02-29T13:14:15.1234567' AS datetime2(7)) AS dt2, CAST('2024-02-29T13:14:00' AS smalldatetime) AS sdt,
               CAST('2024-02-29T13:14:15.5-05:00' AS datetimeoffset(3)) AS dto, CAST('13:14:15.25' AS time(2)) AS tm,
               CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS g,
               CAST('abc' AS char(5)) AS c, CAST('xyz' AS varchar(10)) AS vc, CAST(N'ñandú' AS nchar(6)) AS nc,
               CAST(N'日本' AS nvarchar(10)) AS nv, CAST(N'largo' AS nvarchar(max)) AS nvm,
               CAST('t' AS text) AS tx, CAST(N'nt' AS ntext) AS ntx, CAST('<a b="1">x</a>' AS xml) AS x,
               CAST(0x0102 AS binary(4)) AS bin, CAST(0x0A0B AS varbinary(10)) AS vb, CAST(0x01 AS image) AS img,
               CAST(42 AS sql_variant) AS v1, CAST(CAST(1.25 AS decimal(5,2)) AS sql_variant) AS v2, CAST(N'hola' AS sql_variant) AS v3,
               geography::Point(4.6, -74.08, 4326) AS geo, geometry::STGeomFromText('POINT(1 2)', 0) AS geom,
               hierarchyid::Parse('/1/2/') AS h
        """;

    [Fact]
    public async Task TodosLosTipos_SerializacionEstable()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("tipos"));
        var outcome = await RunAsync(Executor(CreateFactory()), profile, AllTypesSql);
        var result = outcome.Result;
        var row = Assert.Single(result.Rows);
        object? V(string name) => row[result.Columns.FindIndex(c => c.Name == name)];
        string T(string name) => result.Columns.Single(c => c.Name == name).SqlType;

        Assert.Null(V("nulo"));
        Assert.Equal(true, V("b"));
        Assert.Equal(255L, V("ti"));
        Assert.Equal(-32768L, V("si"));
        Assert.Equal(2147483647L, V("i"));
        Assert.Equal(long.MaxValue, V("bi"));
        Assert.Equal(1.5d, V("r"));
        Assert.Equal(0.1d, V("f"));
        Assert.Equal("12345678901234567890.123456789", V("d"));
        Assert.Equal("123.450", V("n"));
        Assert.Equal("922337203685477.5807", V("m"));
        Assert.Equal("-214748.3648", V("sm"));
        Assert.Equal("2024-02-29", V("dt"));
        Assert.Equal("2024-02-29T13:14:15.5", V("dtm"));
        Assert.Equal("2024-02-29T13:14:15.1234567", V("dt2"));
        Assert.Equal("2024-02-29T13:14:00", V("sdt"));
        Assert.Equal("2024-02-29T13:14:15.5-05:00", V("dto"));
        Assert.Equal("13:14:15.25", V("tm"));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", V("g"));
        Assert.Equal("abc  ", V("c"));
        Assert.Equal("xyz", V("vc"));
        Assert.Equal("ñandú ", V("nc"));
        Assert.Equal("日本", V("nv"));
        Assert.Equal("largo", V("nvm"));
        Assert.Equal("t", V("tx"));
        Assert.Equal("nt", V("ntx"));
        Assert.Equal("<a b=\"1\">x</a>", V("x"));
        Assert.Equal("AQIAAA==", V("bin"));
        Assert.Equal("Cgs=", V("vb"));
        Assert.Equal("AQ==", V("img"));
        Assert.Equal(42L, V("v1"));
        Assert.Equal("1.25", V("v2"));
        Assert.Equal("hola", V("v3"));
        Assert.Equal("W0A=", V("h"));
        Assert.False(string.IsNullOrEmpty((string?)V("geo")));
        Assert.Equal(Convert.ToBase64String(Convert.FromBase64String((string)V("geom")!)), V("geom"));

        Assert.Equal("decimal(38,9)", T("d"));
        Assert.Equal("nvarchar(10)", T("nv"));
        Assert.Equal("nvarchar(max)", T("nvm"));
        Assert.Equal("datetime2(7)", T("dt2"));
        Assert.Equal("time(2)", T("tm"));
        Assert.Equal("varbinary(10)", T("vb"));
        Assert.Equal("geography", T("geo"));
        Assert.Equal("geometry", T("geom"));
        Assert.Equal("hierarchyid", T("h"));
        Assert.Equal("xml", T("x"));
        Assert.Equal("money", T("m"));
        Assert.Contains(outcome.Warnings, w => w.Contains("'geo' (geography)", StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, w => w.Contains("'h' (hierarchyid)", StringComparison.Ordinal));
        Assert.False(result.Truncated);
        Assert.Equal(0, result.TruncatedCells);
    }

    [Fact]
    public async Task Parametros_DeMuchosTipos_IdaYVuelta()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("params"));
        var guid = Guid.NewGuid();
        QueryParameter P(string name, string? type, string json) => new(name, type, ParseJson(json));
        var outcome = await RunAsync(
            Executor(CreateFactory()),
            profile,
            "SELECT @i AS i, @bi AS bi, @s AS s, @d AS d, @dt AS dt, @dt2 AS dt2, @dto AS dto, @t AS t, @g AS g, @b AS b, @bin AS bin, @m AS m, @f AS f, @x AS x, @n AS n, @ti AS ti, @big AS big",
            parameters:
            [
                P("i", "int", "-5"),
                P("bi", null, "9007199254740993"),
                P("s", null, "\"texto con 'comillas'; DROP TABLE x --\""),
                P("d", "decimal(10,2)", "1234.5"),
                P("dt", "date", "\"2024-02-29\""),
                P("dt2", "datetime2(3)", "\"2024-02-29T13:14:15.123\""),
                P("dto", "datetimeoffset", "\"2024-02-29T13:14:15Z\""),
                P("t", "time(3)", "\"08:30:15.5\""),
                P("g", "uniqueidentifier", $"\"{guid}\""),
                P("b", "bit", "true"),
                P("bin", "varbinary(max)", "\"AQID\""),
                P("m", "money", "12.3456"),
                P("f", "float", "0.25"),
                P("x", "xml", "\"<a>1</a>\""),
                P("n", null, "null"),
                P("ti", "tinyint", "200"),
                P("big", null, "12345678901234567890.123456789"),
            ]);

        var row = Assert.Single(outcome.Result.Rows);
        Assert.Equal(-5L, row[0]);
        Assert.Equal(9007199254740993L, row[1]);
        Assert.Equal("texto con 'comillas'; DROP TABLE x --", row[2]);
        Assert.Equal("1234.50", row[3]);
        Assert.Equal("2024-02-29", row[4]);
        Assert.Equal("2024-02-29T13:14:15.123", row[5]);
        Assert.Equal("2024-02-29T13:14:15+00:00", row[6]);
        Assert.Equal("08:30:15.5", row[7]);
        Assert.Equal(guid.ToString("D"), row[8]);
        Assert.Equal(true, row[9]);
        Assert.Equal("AQID", row[10]);
        Assert.Equal("12.3456", row[11]);
        Assert.Equal(0.25d, row[12]);
        Assert.Equal("<a>1</a>", row[13]);
        Assert.Null(row[14]);
        Assert.Equal(200L, row[15]);
        Assert.Equal("12345678901234567890.123456789", row[16]);
    }

    [Fact]
    public async Task Parametros_EnFiltro()
    {
        IntegrationEnvironment.RequireDatabase();
        var outcome = await RunAsync(
            Executor(CreateFactory()),
            Profile(NewApplicationName("filtro")),
            "SELECT ClienteId, Nombre, Saldo FROM ventas.Clientes WHERE ClienteId = @id AND Nombre = @nombre",
            parameters: [new QueryParameter("@id", "int", ParseJson("7")), new QueryParameter("nombre", "nvarchar(100)", ParseJson("\"Cliente 7\""))]);

        var row = Assert.Single(outcome.Result.Rows);
        Assert.Equal(7L, row[0]);
        Assert.Equal("70.8638", row[2]);
        Assert.Equal("decimal(18,4)", outcome.Result.Columns[2].SqlType);
        Assert.False(outcome.Result.Columns[0].Nullable);
    }

    [Fact]
    public async Task TruncamientoPorFilas_MaxRows()
    {
        IntegrationEnvironment.RequireDatabase();
        var outcome = await RunAsync(Executor(CreateFactory()), Profile(NewApplicationName("filas")), "SELECT FacturaId FROM ventas.Facturas ORDER BY FacturaId", maxRows: 10);

        Assert.Equal(10, outcome.Result.Rows.Count);
        Assert.True(outcome.Result.Truncated);
        Assert.Equal(ResultReader.ReasonMaxRows, outcome.Result.TruncationReason);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (object?)(long)i), outcome.Result.Rows.Select(r => r[0]));
        Assert.Contains(outcome.Warnings, w => w.StartsWith("Resultado truncado", StringComparison.Ordinal));
        Assert.True(outcome.Result.ApproximateBytes > 0);
    }

    [Fact]
    public async Task LimiteDelPerfil_AcotaMaxRowsSolicitado()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("limite"), c => c.Limits.MaxRows = 7);
        var outcome = await RunAsync(Executor(CreateFactory()), profile, "SELECT ClienteId FROM ventas.Clientes", maxRows: 1000);
        Assert.Equal(7, outcome.Result.Rows.Count);
        Assert.True(outcome.Result.Truncated);
    }

    [Fact]
    public async Task UnMillonDeFilas_SeCortaSinDrenarYSinSolicitudesResiduales()
    {
        IntegrationEnvironment.RequireDatabase();
        var app = NewApplicationName("ca20");
        var profile = Profile(app);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await RunAsync(
            Executor(CreateFactory()),
            profile,
            "SELECT a.FacturaId, a.Total, b.ClienteId, b.Nombre FROM ventas.Facturas a CROSS JOIN ventas.Clientes b",
            maxRows: 500);
        stopwatch.Stop();

        Assert.Equal(500, outcome.Result.Rows.Count);
        Assert.True(outcome.Result.Truncated);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Tardó {stopwatch.Elapsed}");
        var state = await WaitForIdleAsync(app);
        Assert.Equal((0, 0, 0), state);
    }

    [Fact]
    public async Task OmitirFilas_Paginacion()
    {
        IntegrationEnvironment.RequireDatabase();
        var outcome = await RunAsync(Executor(CreateFactory()), Profile(NewApplicationName("skip")), "SELECT ClienteId FROM ventas.Clientes ORDER BY ClienteId", maxRows: 3, skipRows: 5);
        Assert.Equal([6L, 7L, 8L], outcome.Result.Rows.Select(r => (long)r[0]!));
        Assert.True(outcome.Result.Truncated);

        var beyond = await RunAsync(Executor(CreateFactory()), Profile(NewApplicationName("skip2")), "SELECT ClienteId FROM ventas.Clientes", maxRows: 3, skipRows: 1000);
        Assert.Empty(beyond.Result.Rows);
        Assert.False(beyond.Result.Truncated);
    }

    [Fact]
    public async Task TruncamientoPorTamano_MaxResponseBytes()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("bytes"), c => c.Limits.MaxResponseBytes = 2000);
        var outcome = await RunAsync(Executor(CreateFactory()), profile, "SELECT ClienteId, Nombre, Email FROM ventas.Clientes ORDER BY ClienteId", maxRows: 300);

        Assert.True(outcome.Result.Truncated);
        Assert.Equal(ResultReader.ReasonMaxResponseBytes, outcome.Result.TruncationReason);
        Assert.InRange(outcome.Result.Rows.Count, 1, 299);
        Assert.True(outcome.Result.ApproximateBytes <= 2000);
        Assert.Contains(outcome.Warnings, w => w.Contains("tamaño máximo de respuesta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CeldasGrandes_SeRecortanConMemoriaAcotada()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("celdas"), c => c.Limits.MaxCellChars = 10);
        var executor = Executor(CreateFactory());
        const string sql = """
            SELECT REPLICATE(CAST(N'a' AS nvarchar(max)), 20000000) AS texto,
                   CAST(REPLICATE(CAST('a' AS varchar(max)), 20000000) AS varbinary(max)) AS binario,
                   CAST(REPLICATE(CAST(N'<b>' AS nvarchar(max)), 10) AS nvarchar(max)) AS corto
            """;

        // Calienta el pool y el JIT para medir solo la lectura.
        await RunAsync(executor, profile, "SELECT 1 AS uno");
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var outcome = await RunAsync(executor, profile, sql);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        var row = Assert.Single(outcome.Result.Rows);
        Assert.Equal("aaaaaaaaaa…", row[0]);
        Assert.Equal("YWFhYWFh…", row[1]);
        Assert.Equal("<b><b><b><…", row[2]);
        Assert.Equal(3, outcome.Result.TruncatedCells);
        Assert.Contains(outcome.Warnings, w => w.Contains("3 celda(s)", StringComparison.Ordinal));
        // Referencia: leer los mismos valores materializándolos (GetValue). El driver siempre transfiere y descarta los
        // ~60 MB que envía el servidor; lo que se comprueba es que el lector no construye los valores completos.
        var factory = CreateFactory();
        await using var connection = await factory.OpenAsync(profile, ConnectionRole.Read, "baseline", TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var baselineBefore = GC.GetTotalAllocatedBytes(precise: true);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            GC.KeepAlive(reader.GetValue(0));
            GC.KeepAlive(reader.GetValue(1));
        }

        var baseline = GC.GetTotalAllocatedBytes(precise: true) - baselineBefore;
        Assert.True(allocated < baseline * 3 / 4, $"El lector asignó {allocated} bytes; materializar los valores asigna {baseline} bytes.");
    }

    [Fact]
    public async Task Enmascaramiento_PorOrdinalYPorNombre()
    {
        IntegrationEnvironment.RequireDatabase();
        var executor = Executor(CreateFactory());
        var profile = Profile(NewApplicationName("mascara"));

        var byOrdinal = await RunAsync(executor, profile, "SELECT ClienteId, Email AS correo FROM ventas.Clientes WHERE ClienteId <= 3 ORDER BY ClienteId", evaluation: Allow(1));
        Assert.All(byOrdinal.Result.Rows, r => Assert.Equal("****", r[1]));
        Assert.True(byOrdinal.Result.Columns[1].Masked);
        Assert.False(byOrdinal.Result.Columns[0].Masked);
        Assert.Contains(byOrdinal.Warnings, w => w.Contains("enmascaradas", StringComparison.Ordinal) && w.Contains("correo", StringComparison.Ordinal));

        var byName = await RunAsync(executor, profile, "SELECT UsuarioId, Login, Token FROM ventas.Usuarios ORDER BY UsuarioId");
        Assert.True(byName.Result.Columns[2].Masked);
        Assert.Equal("****", byName.Result.Rows[0][2]);
        Assert.Null(byName.Result.Rows[1][2]);
        Assert.Equal("admin", byName.Result.Rows[0][1]);
    }

    [Fact]
    public async Task ColumnaExcluida_AbortaAntesDeLeerFilas()
    {
        IntegrationEnvironment.RequireDatabase();
        var app = NewApplicationName("excluida");
        var executor = Executor(CreateFactory());
        var profile = Profile(app);

        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT * FROM ventas.Usuarios"));
        Assert.Equal(ErrorCodes.PolicyBlocked, ex.Code);
        Assert.Equal(ResultReader.RuntimeBlockRuleId, ex.RuleId);
        Assert.Contains("'Contrasena'", ex.Message, StringComparison.Ordinal);

        // Un resultado enorme con una columna protegida se aborta sin drenarlo.
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT b.Nombre, a.FacturaId AS Contrasena FROM ventas.Facturas a CROSS JOIN ventas.Clientes b CROSS JOIN ventas.Clientes c"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Tardó {stopwatch.Elapsed}");
        Assert.Equal((0, 0, 0), await WaitForIdleAsync(app));
    }

    [Fact]
    public async Task ColumnaSensible_ConAccionBloquear()
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = Profile(NewApplicationName("bloquear"), c => c.Sensitive.Action = SensitiveColumnAction.Block);
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(Executor(CreateFactory()), profile, "SELECT UsuarioId, Token FROM ventas.Usuarios"));
        Assert.Equal(ErrorCodes.PolicyBlocked, ex.Code);
        Assert.Contains("'Token'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TiempoAgotado_CancelaEnServidorSinBloqueosYElPoolSigueFuncionando(bool rollbackTransaction)
    {
        IntegrationEnvironment.RequireDatabase();
        var app = NewApplicationName("timeout");
        var profile = Profile(app, c =>
        {
            c.Limits.CommandTimeoutSeconds = 2;
            c.QueryInRollbackTransaction = rollbackTransaction;
            c.IsolationLevel = QueryIsolationLevel.RepeatableRead;
        });
        var executor = Executor(CreateFactory());

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(
            executor,
            profile,
            "SELECT COUNT_BIG(*) AS n FROM ventas.Facturas a CROSS JOIN ventas.Facturas b CROSS JOIN ventas.Facturas c CROSS JOIN ventas.Clientes d"));
        stopwatch.Stop();

        Assert.Equal(ErrorCodes.Timeout, ex.Code);
        Assert.Equal(-2, ex.Details["number"]);
        Assert.Contains("2 s", ex.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Tardó {stopwatch.Elapsed}");

        // RS-LIM-02 / CA-21: el servidor dejó de ejecutar y no quedan transacciones ni bloqueos.
        Assert.Equal((0, 0, 0), await WaitForIdleAsync(app));

        var after = await RunAsync(executor, profile, "SELECT COUNT(*) AS n FROM ventas.Clientes");
        Assert.Equal(300L, after.Result.Rows[0][0]);
        Assert.Equal((0, 0, 0), await WaitForIdleAsync(app));
    }

    [Theory]
    [InlineData(true, 1L)]
    [InlineData(false, 0L)]
    public async Task TransaccionDeSoloLectura_SiempreRevertida(bool enabled, long expectedTranCount)
    {
        IntegrationEnvironment.RequireDatabase();
        var app = NewApplicationName("trx");
        var profile = Profile(app, c =>
        {
            c.QueryInRollbackTransaction = enabled;
            c.IsolationLevel = QueryIsolationLevel.Serializable;
        });
        var outcome = await RunAsync(
            Executor(CreateFactory()),
            profile,
            "SELECT CAST(@@TRANCOUNT AS bigint) AS tc, CAST(transaction_isolation_level AS bigint) AS iso, SUSER_SNAME() AS login FROM sys.dm_exec_sessions WHERE session_id = @@SPID");

        var row = Assert.Single(outcome.Result.Rows);
        Assert.Equal(expectedTranCount, row[0]);
        Assert.Equal(4L, row[1]);
        Assert.Equal("mcp_lector", row[2]);
        Assert.Equal((0, 0, 0), await WaitForIdleAsync(app));
    }

    [Fact]
    public async Task ErroresSql_ConNumeroSeveridadYLinea()
    {
        IntegrationEnvironment.RequireDatabase();
        var executor = Executor(CreateFactory());
        var profile = Profile(NewApplicationName("errores"));

        var divide = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT 1/0 AS x"));
        Assert.Equal(ErrorCodes.SqlError, divide.Code);
        Assert.Equal(8134, divide.Details["number"]);
        Assert.Equal(16, divide.Details["severity"]);
        Assert.Equal(1, divide.Details["line"]);
        Assert.Contains("NULLIF", divide.Suggestion, StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT 1 AS a\nFROM ventas.NoExiste"));
        Assert.Equal(ErrorCodes.SqlError, missing.Code);
        Assert.Equal(208, missing.Details["number"]);
        // SQL Server informa el error 208 en la línea de inicio de la sentencia.
        Assert.InRange((int)missing.Details["line"]!, 1, 2);
        Assert.Contains("ventas.NoExiste", (string)missing.Details["message"]!, StringComparison.Ordinal);

        var denied = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT Valor FROM seguridad.Claves"));
        Assert.Equal(ErrorCodes.SqlError, denied.Code);
        Assert.Equal(229, denied.Details["number"]);

        // Error a mitad del flujo de filas: se informa y la conexión se libera.
        var midStream = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT 10 / (ClienteId - 150) AS x FROM ventas.Clientes ORDER BY ClienteId"));
        Assert.Equal(8134, midStream.Details["number"]);

        var ok = await RunAsync(executor, profile, "SELECT 1 AS uno");
        Assert.Equal(1L, ok.Result.Rows[0][0]);
    }

    [Fact]
    public async Task VariosConjuntos_SoloElPrimero()
    {
        IntegrationEnvironment.RequireDatabase();
        var outcome = await RunAsync(Executor(CreateFactory()), Profile(NewApplicationName("multi")), "SELECT 1 AS a; SELECT 2 AS b;");
        Assert.Equal("a", Assert.Single(outcome.Result.Columns).Name);
        Assert.Equal(1L, Assert.Single(outcome.Result.Rows)[0]);
        Assert.Contains(outcome.Warnings, w => w.Contains("más de un conjunto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MensajesInformativos_ComoAdvertencias()
    {
        IntegrationEnvironment.RequireDatabase();
        var outcome = await RunAsync(Executor(CreateFactory()), Profile(NewApplicationName("info")), "SELECT SUM(x) AS s FROM (VALUES (1), (NULL)) v(x)");
        Assert.Equal(1L, outcome.Result.Rows[0][0]);
        Assert.Contains(outcome.Warnings, w => w.StartsWith("Mensaje de SQL Server:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransaccionActiva_LeeSusCambiosYSobreviveATruncamientoYErrores()
    {
        IntegrationEnvironment.RequireDatabase();
        var ct = TestContext.Current.CancellationToken;
        var app = NewApplicationName("activa");
        var profile = Profile(app);
        var factory = CreateFactory();

        var connection = await factory.OpenAsync(profile, ConnectionRole.Write, "trx-activa", ct);
        await using (connection)
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await using (transaction)
            {
                await using (var setup = connection.CreateCommand())
                {
                    setup.Transaction = transaction;
                    setup.CommandText = "CREATE TABLE #pendiente (v nvarchar(50) NOT NULL); INSERT INTO #pendiente (v) VALUES (N'cambio-sin-confirmar');";
                    await setup.ExecuteNonQueryAsync(ct);
                }

                var active = new ActiveTransaction { TransactionId = "t-prueba", Profile = profile, Connection = connection, Transaction = transaction };
                var manager = new FixedTransactionManager(active);
                var executor = Executor(factory, manager);

                var own = await RunAsync(executor, profile, "SELECT v FROM #pendiente");
                Assert.Equal("cambio-sin-confirmar", Assert.Single(own.Result.Rows)[0]);

                var truncated = await RunAsync(executor, profile, "SELECT a.FacturaId FROM ventas.Facturas a CROSS JOIN ventas.Clientes b", maxRows: 5);
                Assert.True(truncated.Result.Truncated);

                var error = await Assert.ThrowsAsync<ToolFailureException>(() => RunAsync(executor, profile, "SELECT 1/0 AS x"));
                Assert.Equal(8134, error.Details["number"]);
                Assert.False(error.Details.ContainsKey("transactionRolledBack"));

                await using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT @@TRANCOUNT, XACT_STATE(), @@OPTIONS & 16384, (SELECT v FROM #pendiente)";
                    await using var reader = await check.ExecuteReaderAsync(ct);
                    Assert.True(await reader.ReadAsync(ct));
                    Assert.Equal(1, reader.GetInt32(0));
                    Assert.Equal(1, reader.GetInt16(1));
                    Assert.Equal(16384, reader.GetInt32(2));
                    Assert.Equal("cambio-sin-confirmar", reader.GetString(3));
                }

                Assert.Equal(2, active.StatementCount);
                Assert.True(manager.Touches >= 6);
                Assert.Equal(1, active.Gate.CurrentCount);

                await transaction.RollbackAsync(ct);
            }
        }

        Assert.Equal((0, 0, 0), await WaitForIdleAsync(app));
    }
}
