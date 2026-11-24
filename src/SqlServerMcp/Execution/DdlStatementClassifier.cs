using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Execution;

/// <summary>
/// Clasificación de sentencias DDL ya analizadas para el respaldo previo y el análisis de impacto
/// (RF-DDL-05..08, RS-AUD-05). Es pura: usa el tipo de sentencia de ScriptDom, la categoría y la operación de la política.
/// </summary>
public static class DdlStatementClassifier
{
    private static readonly HashSet<string> NonTransactionalTypes = new(StringComparer.Ordinal)
    {
        nameof(CreateFullTextCatalogStatement),
        nameof(AlterFullTextCatalogStatement),
        nameof(DropFullTextCatalogStatement),
        nameof(CreateFullTextIndexStatement),
        nameof(AlterFullTextIndexStatement),
        nameof(DropFullTextIndexStatement),
    };

    private static readonly HashSet<StatementCategory> DdlCategories =
    [
        StatementCategory.DdlCreate,
        StatementCategory.DdlAlter,
        StatementCategory.DdlDrop,
        StatementCategory.DdlTruncate,
        StatementCategory.DdlDestructiveAlter,
        StatementCategory.DdlTrigger,
        StatementCategory.Statistics,
        StatementCategory.ExtendedProperty,
    ];

    /// <summary>Categorías que puede ejecutar execute_ddl.</summary>
    public static bool IsDdlCategory(StatementCategory category) => DdlCategories.Contains(category);

    /// <summary>
    /// Sentencias que SQL Server no admite dentro de una transacción de usuario (catálogos e índices de texto completo):
    /// se ejecutan sin transacción y se rechazan dentro de una transacción explícita.
    /// </summary>
    public static bool IsNonTransactional(SqlStatementInfo statement) => NonTransactionalTypes.Contains(TypeName(statement));

    /// <summary>CREATE OR ALTER de vista, procedimiento, función o desencadenador.</summary>
    public static bool IsCreateOrAlter(SqlStatementInfo statement) =>
        TypeName(statement).StartsWith("CreateOrAlter", StringComparison.Ordinal)
        || string.Equals(statement.Operation, "CREATE OR ALTER", StringComparison.OrdinalIgnoreCase);

    public static bool IsDrop(SqlStatementInfo statement) =>
        statement.Category == StatementCategory.DdlDrop
        || TypeName(statement).StartsWith("Drop", StringComparison.Ordinal)
        || string.Equals(statement.Operation, "DROP", StringComparison.OrdinalIgnoreCase);

    public static bool IsTruncate(SqlStatementInfo statement) =>
        statement.Category == StatementCategory.DdlTruncate
        || TypeName(statement) == nameof(TruncateTableStatement)
        || string.Equals(statement.Operation, "TRUNCATE", StringComparison.OrdinalIgnoreCase);

    /// <summary>ALTER (incluidos los destructivos y ENABLE/DISABLE TRIGGER).</summary>
    public static bool IsAlter(SqlStatementInfo statement)
    {
        var type = TypeName(statement);
        return statement.Category is StatementCategory.DdlAlter or StatementCategory.DdlDestructiveAlter
            || type.StartsWith("Alter", StringComparison.Ordinal)
            || type == nameof(EnableDisableTriggerStatement)
            || string.Equals(statement.Operation, "ALTER", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>sp_rename (por operación declarada o por el procedimiento invocado).</summary>
    public static bool IsRename(SqlStatementInfo statement) =>
        string.Equals(statement.Operation, "RENAME", StringComparison.OrdinalIgnoreCase)
        || (statement.Fragment is ExecuteStatement execute
            && execute.ExecuteSpecification?.ExecutableEntity is ExecutableProcedureReference reference
            && string.Equals(reference.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value, "sp_rename", StringComparison.OrdinalIgnoreCase));

    /// <summary>Requiere respaldar la definición previa si el destino existe (RF-DDL-05).</summary>
    public static bool RequiresBackup(SqlStatementInfo statement) =>
        IsAlter(statement) || IsDrop(statement) || IsTruncate(statement) || IsRename(statement) || IsCreateOrAlter(statement);

    /// <summary>Informar dependientes: eliminar, renombrar o cambiar la firma (RF-DDL-07).</summary>
    public static bool NeedsDependents(SqlStatementInfo statement) =>
        IsDrop(statement) || IsAlter(statement) || IsRename(statement) || IsCreateOrAlter(statement);

    /// <summary>Filas aproximadas para DROP TABLE, TRUNCATE TABLE, DROP COLUMN y ALTER COLUMN (RF-DDL-06).</summary>
    public static bool NeedsApproximateRows(SqlStatementInfo statement)
    {
        switch (statement.Fragment)
        {
            case DropTableStatement or TruncateTableStatement or AlterTableAlterColumnStatement:
                return true;
            case AlterTableDropTableElementStatement drop:
                return drop.AlterTableDropTableElements.Any(e => e.TableElementType == TableElementType.Column);
        }

        return TypeName(statement) is nameof(DropTableStatement) or nameof(TruncateTableStatement) or nameof(AlterTableAlterColumnStatement)
            || (statement.Fragment is null && TypeName(statement) == nameof(AlterTableDropTableElementStatement));
    }

    /// <summary>Operación para el registro de cambios: CREATE, CREATE OR ALTER, ALTER, DROP, TRUNCATE, RENAME…</summary>
    public static string OperationName(SqlStatementInfo statement)
    {
        if (IsRename(statement))
        {
            return "RENAME";
        }

        if (IsCreateOrAlter(statement))
        {
            return "CREATE OR ALTER";
        }

        if (!string.IsNullOrWhiteSpace(statement.Operation))
        {
            return statement.Operation.ToUpperInvariant();
        }

        if (IsTruncate(statement))
        {
            return "TRUNCATE";
        }

        if (IsDrop(statement))
        {
            return "DROP";
        }

        return IsAlter(statement) ? "ALTER" : "CREATE";
    }

    private static string TypeName(SqlStatementInfo statement) =>
        statement.Fragment?.GetType().Name ?? statement.StatementType ?? string.Empty;
}
