using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Tests.Execution.Write;

/// <summary>Ayudantes de análisis para las pruebas de escritura (sin depender del motor de políticas).</summary>
internal static class WriteTestSql
{
    public static TSqlStatement Parse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);
        var script = Assert.IsType<TSqlScript>(fragment);
        return Assert.Single(Assert.Single(script.Batches).Statements);
    }

    public static SqlStatementInfo Info(string sql, StatementCategory? category = null, string? operation = null)
    {
        var statement = Parse(sql);
        return new SqlStatementInfo
        {
            Parsed = true,
            BatchCount = 1,
            StatementCount = 1,
            StatementType = statement.GetType().Name,
            Category = category ?? Categorize(statement),
            Operation = operation,
            Fragment = statement,
            NormalizedSql = sql,
        };
    }

    public static ProfileRuntime Profile(Action<ProfileConfig>? customize = null)
    {
        var config = new ProfileConfig
        {
            Mode = OperationMode.Completo,
            Connection = new ConnectionConfig { Server = "localhost", Database = "VentasTest" },
            AllowedSchemas = ["ventas", "ia"],
            DdlSchemas = ["ia"],
            Excluded = new ExclusionConfig { Schemas = ["seguridad"], Tables = ["ventas.TarjetasCredito"], Columns = ["*.Contrasena"] },
        };
        customize?.Invoke(config);
        return new ProfileRuntime("unitario", config, config.Limits);
    }

    private static StatementCategory Categorize(TSqlStatement statement) => statement switch
    {
        InsertStatement => StatementCategory.Insert,
        UpdateStatement => StatementCategory.Update,
        DeleteStatement => StatementCategory.Delete,
        MergeStatement => StatementCategory.Merge,
        TruncateTableStatement => StatementCategory.DdlTruncate,
        DropTableStatement or DropViewStatement or DropProcedureStatement => StatementCategory.DdlDrop,
        AlterTableDropTableElementStatement or AlterTableAlterColumnStatement => StatementCategory.DdlDestructiveAlter,
        AlterTableStatement or AlterViewStatement or AlterProcedureStatement => StatementCategory.DdlAlter,
        _ when statement.GetType().Name.StartsWith("Create", StringComparison.Ordinal) => StatementCategory.DdlCreate,
        _ => StatementCategory.Other,
    };
}
