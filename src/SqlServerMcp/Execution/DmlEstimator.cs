using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Policy;

namespace SqlServerMcp.Execution;

/// <summary>Estrategia para estimar las filas afectadas por un DML (RF-DML-03).</summary>
public enum DmlEstimationStrategy
{
    /// <summary>No se puede estimar de forma segura (por ejemplo, INSERT … EXEC).</summary>
    NotEstimable = 0,

    /// <summary>INSERT … VALUES: número de listas de valores, sin I/O.</summary>
    ValuesRowCount = 1,

    /// <summary>UPDATE/DELETE: SELECT COUNT_BIG(*) con el mismo origen y predicado.</summary>
    CountQuery = 2,

    /// <summary>INSERT … SELECT, MERGE, UPDATE/DELETE con FROM o TOP PERCENT: ejecución de prueba siempre revertida.</summary>
    TrialExecution = 3,
}

/// <summary>Plan de estimación calculado sin I/O a partir del AST.</summary>
/// <param name="Strategy">Estrategia elegida.</param>
/// <param name="KnownRows">Filas conocidas sin consultar (solo para <see cref="DmlEstimationStrategy.ValuesRowCount"/>).</param>
/// <param name="CountSql">Consulta de conteo (solo para <see cref="DmlEstimationStrategy.CountQuery"/>).</param>
public sealed record DmlEstimationPlan(DmlEstimationStrategy Strategy, long? KnownRows = null, string? CountSql = null);

/// <summary>
/// Decide cómo estimar las filas afectadas por una sentencia DML ya aprobada por la política (RF-DML-03, RF-DML-05, CA-13).
/// Es puro: la ejecución del plan la realiza <see cref="DmlExecutor"/>.
/// </summary>
public sealed class DmlEstimator
{
    private readonly DmlCountQueryBuilder _countQueryBuilder;

    public DmlEstimator(DmlCountQueryBuilder countQueryBuilder)
    {
        ArgumentNullException.ThrowIfNull(countQueryBuilder);
        _countQueryBuilder = countQueryBuilder;
    }

    /// <summary>Calcula el plan de estimación para la sentencia analizada.</summary>
    public DmlEstimationPlan Plan(SqlStatementInfo statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        switch (statement.Fragment)
        {
            case InsertStatement insert:
            {
                var specification = insert.InsertSpecification;
                if (specification?.InsertSource is ExecuteInsertSource)
                {
                    // INSERT … EXEC ejecutaría un procedimiento durante la estimación: no se estima.
                    return new DmlEstimationPlan(DmlEstimationStrategy.NotEstimable);
                }

                if (specification?.InsertSource is ValuesInsertSource values && specification.TopRowFilter is null)
                {
                    var rows = values.IsDefaultValues ? 1 : values.RowValues.Count;
                    return new DmlEstimationPlan(DmlEstimationStrategy.ValuesRowCount, KnownRows: rows);
                }

                return new DmlEstimationPlan(DmlEstimationStrategy.TrialExecution);
            }

            case UpdateStatement or DeleteStatement:
            {
                var sql = _countQueryBuilder.Build(statement.Fragment);
                return sql is null
                    ? new DmlEstimationPlan(DmlEstimationStrategy.TrialExecution)
                    : new DmlEstimationPlan(DmlEstimationStrategy.CountQuery, CountSql: sql);
            }

            case MergeStatement:
                return new DmlEstimationPlan(DmlEstimationStrategy.TrialExecution);

            case null when statement.Category is StatementCategory.Insert or StatementCategory.Update
                or StatementCategory.Delete or StatementCategory.Merge:
                return new DmlEstimationPlan(DmlEstimationStrategy.TrialExecution);

            default:
                return new DmlEstimationPlan(DmlEstimationStrategy.NotEstimable);
        }
    }

    /// <summary>
    /// Indica si la sentencia devuelve filas al cliente mediante OUTPUT (sin INTO) (RF-DML-06).
    /// Usa el AST cuando está disponible; si no, el indicador del análisis.
    /// </summary>
    public static bool ReturnsOutputRows(SqlStatementInfo statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var specification = statement.Fragment switch
        {
            InsertStatement i => (DataModificationSpecification?)i.InsertSpecification,
            UpdateStatement u => u.UpdateSpecification,
            DeleteStatement d => d.DeleteSpecification,
            MergeStatement m => m.MergeSpecification,
            _ => null,
        };

        return specification is null ? statement.HasOutputClause : specification.OutputClause is not null;
    }
}
