using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Policy;
using SqlServerMcp.Security;

namespace SqlServerMcp.IntegrationTests.Write;

/// <summary>Ejecutores de escritura reales con dobles mínimos de lector, enlazador y catálogo.</summary>
internal sealed class WriteHarness : IAsyncDisposable
{
    private WriteHarness(ProfileRuntime profile, TimeProvider time)
    {
        Profile = profile;
        Factory = new SqlConnectionFactory(new SecretResolver(), NullLogger<SqlConnectionFactory>.Instance);
        Binder = new SimpleParameterBinder();
        Reader = new SimpleResultReader();
        Catalog = new SimpleCatalogLookup(Factory);
        Audit = new RecordingAuditLog();
        Transactions = new TransactionManager(Factory, Audit, NullLogger<TransactionManager>.Instance, time);
        AuditDirectory = Path.Combine(Path.GetTempPath(), "sqlservermcp-write-tests", Guid.NewGuid().ToString("N"));
        var auditConfig = new AuditConfig { Path = AuditDirectory, HashChain = true };
        ChangeLog = new SchemaChangeLog(auditConfig, new AuditPaths(auditConfig));
        Dml = new DmlExecutor(Factory, Binder, Reader, Catalog, Transactions, NullLogger<DmlExecutor>.Instance);
        Procedures = new ProcedureExecutor(Factory, Binder, Reader, Catalog, Transactions);
        Ddl = new DdlExecutor(Factory, Catalog, ChangeLog, Transactions, NullLogger<DdlExecutor>.Instance);
    }

    public ProfileRuntime Profile { get; }

    public SqlConnectionFactory Factory { get; }

    public SimpleParameterBinder Binder { get; }

    public SimpleResultReader Reader { get; }

    public SimpleCatalogLookup Catalog { get; }

    public RecordingAuditLog Audit { get; }

    public TransactionManager Transactions { get; }

    public string AuditDirectory { get; }

    public SchemaChangeLog ChangeLog { get; }

    public DmlExecutor Dml { get; }

    public ProcedureExecutor Procedures { get; }

    public DdlExecutor Ddl { get; }

    public static WriteHarness Create(Action<ProfileConfig>? customize = null, TimeProvider? time = null)
    {
        IntegrationEnvironment.RequireDatabase();
        var profile = IntegrationEnvironment.CreateProfile(OperationMode.Completo, customize, "escritura-" + Guid.NewGuid().ToString("N")[..8]);
        return new WriteHarness(profile, time ?? TimeProvider.System);
    }

    public DmlExecutionRequest DmlRequest(string sql, long? approvedRows = null, bool preview = false, params QueryParameter[] parameters) => new()
    {
        Profile = Profile,
        Sql = sql,
        Parameters = parameters,
        Evaluation = WriteSql.Evaluate(sql, ToolKind.Dml),
        CorrelationId = "test-" + Guid.NewGuid().ToString("N")[..8],
        ApprovedRows = approvedRows,
        Preview = preview,
    };

    /// <summary>Líneas JSON del registro de cambios de esquema.</summary>
    public List<JsonElement> ReadSchemaChanges()
    {
        if (!Directory.Exists(AuditDirectory))
        {
            return [];
        }

        return Directory.GetFiles(AuditDirectory, "schema-changes*")
            .SelectMany(File.ReadAllLines)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        await Transactions.DisposeAsync();
        try
        {
            if (Directory.Exists(AuditDirectory))
            {
                Directory.Delete(AuditDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo del directorio temporal.
        }
    }
}

/// <summary>Construye evaluaciones de política aprobadas a partir del AST (sin depender del motor de políticas).</summary>
internal static class WriteSql
{
    public static PolicyEvaluation Evaluate(string sql, ToolKind tool, string ruleId = "TEST-ALLOW")
    {
        var info = Analyze(sql);
        return new PolicyEvaluation
        {
            Statement = info,
            Tool = tool,
            Action = PolicyAction.Allow,
            RuleId = ruleId,
            Reason = "Permitido por la prueba.",
        };
    }

    public static PolicyEvaluation ForProcedure(string? ruleId = null) => new()
    {
        Statement = new SqlStatementInfo { Category = StatementCategory.ExecuteProcedure, Parsed = true },
        Tool = ToolKind.Any,
        Action = PolicyAction.Allow,
        RuleId = ruleId ?? "TEST-ALLOW",
        Reason = "Permitido por la prueba.",
    };

    public static SqlStatementInfo Analyze(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);
        var statement = Assert.Single(Assert.Single(((TSqlScript)fragment).Batches).Statements);
        new Sql170ScriptGenerator().GenerateScript(statement, out var normalized);
        return new SqlStatementInfo
        {
            Parsed = true,
            BatchCount = 1,
            StatementCount = 1,
            StatementType = statement.GetType().Name,
            Category = Categorize(statement),
            Operation = Operation(statement),
            TargetObject = Target(statement),
            Fragment = statement,
            NormalizedSql = normalized,
            HasOutputClause = sql.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static StatementCategory Categorize(TSqlStatement statement) => statement switch
    {
        InsertStatement => StatementCategory.Insert,
        UpdateStatement => StatementCategory.Update,
        DeleteStatement => StatementCategory.Delete,
        MergeStatement => StatementCategory.Merge,
        TruncateTableStatement => StatementCategory.DdlTruncate,
        DropObjectsStatement => StatementCategory.DdlDrop,
        AlterTableDropTableElementStatement or AlterTableAlterColumnStatement => StatementCategory.DdlDestructiveAlter,
        _ when statement.GetType().Name.StartsWith("Alter", StringComparison.Ordinal) => StatementCategory.DdlAlter,
        _ when statement.GetType().Name.StartsWith("Create", StringComparison.Ordinal) => StatementCategory.DdlCreate,
        _ => StatementCategory.Other,
    };

    private static string? Operation(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        return name.StartsWith("CreateOrAlter", StringComparison.Ordinal) ? "CREATE OR ALTER"
            : name.StartsWith("Create", StringComparison.Ordinal) ? "CREATE"
            : name.StartsWith("Alter", StringComparison.Ordinal) ? "ALTER"
            : name.StartsWith("Drop", StringComparison.Ordinal) ? "DROP"
            : name.StartsWith("Truncate", StringComparison.Ordinal) ? "TRUNCATE"
            : null;
    }

    private static SqlObjectName? Target(TSqlStatement statement)
    {
        var name = statement switch
        {
            CreateTableStatement s => s.SchemaObjectName,
            AlterTableStatement s => s.SchemaObjectName,
            DropObjectsStatement s => s.Objects.FirstOrDefault(),
            TruncateTableStatement s => s.TableName,
            ViewStatementBody s => s.SchemaObjectName,
            ProcedureStatementBody s => s.ProcedureReference?.Name,
            _ => null,
        };
        return name is null
            ? null
            : new SqlObjectName(name.ServerIdentifier?.Value, name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, name.BaseIdentifier.Value);
    }
}

/// <summary>Tabla temporal de trabajo en el esquema ia, creada y eliminada con el login administrador del contenedor.</summary>
internal sealed class ScratchTable : IDisposable
{
    private ScratchTable(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string Qualified => "ia." + Name;

    /// <summary>Crea la tabla; en <paramref name="seedSql"/> "{T}" se reemplaza por el nombre calificado.</summary>
    public static ScratchTable Create(string columnsDdl, string? seedSql = null)
    {
        var table = new ScratchTable("W_" + Guid.NewGuid().ToString("N")[..12]);
        IntegrationEnvironment.ExecuteAdmin($"CREATE TABLE {table.Qualified} ({columnsDdl});");
        if (seedSql is not null)
        {
            IntegrationEnvironment.ExecuteAdmin(seedSql.Replace("{T}", table.Qualified, StringComparison.Ordinal));
        }

        return table;
    }

    /// <summary>Tabla (Id int PK, Valor int) con <paramref name="rows"/> filas Id = 1..N y Valor = 0.</summary>
    public static ScratchTable WithNumbers(int rows) => Create(
        "Id int NOT NULL PRIMARY KEY, Valor int NOT NULL",
        $"INSERT INTO {{T}} (Id, Valor) SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 0 FROM sys.all_objects a CROSS JOIN sys.all_objects b;");

    public static T Scalar<T>(string sql)
    {
        using var connection = IntegrationEnvironment.OpenAdminConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Valor escalar de una consulta administrativa; "{T}" se reemplaza por el nombre calificado.</summary>
    public T Value<T>(string sqlWithTablePlaceholder) =>
        Scalar<T>(sqlWithTablePlaceholder.Replace("{T}", Qualified, StringComparison.Ordinal));

    public void Dispose()
    {
        try
        {
            IntegrationEnvironment.ExecuteAdmin($"IF OBJECT_ID(N'{Qualified}') IS NOT NULL DROP TABLE {Qualified};");
        }
        catch (SqlException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}
