namespace SqlServerMcp.Execution;

/// <summary>Aritmética de umbrales de filas y de lotes de inserción (RF-DML-04, RF-DML-07, RS-HIL-07).</summary>
public static class DmlRowLimits
{
    /// <summary>A partir de este número de filas de un mismo grupo (sin devolver filas) se usa SqlBulkCopy (RF-DML-07).</summary>
    public const int BulkCopyThresholdRows = 200;

    /// <summary>Máximo de listas de valores por INSERT (límite de SQL Server para VALUES).</summary>
    public const int MaxRowsPerInsertStatement = 1000;

    /// <summary>Máximo de parámetros por sentencia (SQL Server admite 2.100; se deja margen).</summary>
    public const int MaxParametersPerInsertStatement = 2000;

    /// <summary>
    /// Filas máximas admitidas tras una aprobación: aprobadas + techo(aprobadas × margen / 100) (RS-HIL-07).
    /// Los valores negativos se tratan como cero.
    /// </summary>
    public static long MaxAllowedForApproval(long approvedRows, int marginPercent)
    {
        var approved = Math.Max(0, approvedRows);
        var margin = Math.Max(0, marginPercent);
        var extra = approved == 0 || margin == 0 ? 0 : ((approved * margin) + 99) / 100;
        return checked(approved + extra);
    }

    /// <summary>True si las filas reales superan las aprobadas más el margen. Sin aprobación (null) nunca se supera.</summary>
    public static bool ExceedsApproval(long actualRows, long? approvedRows, int marginPercent) =>
        approvedRows is { } approved && actualRows > MaxAllowedForApproval(approved, marginPercent);

    /// <summary>Filas por sentencia INSERT parametrizada: ≤ 1.000 filas y ≤ 2.000 parámetros.</summary>
    public static int RowsPerInsertStatement(int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columnCount, 1);
        return Math.Max(1, Math.Min(MaxRowsPerInsertStatement, MaxParametersPerInsertStatement / columnCount));
    }

    /// <summary>Usar SqlBulkCopy: más de <see cref="BulkCopyThresholdRows"/> filas y sin devolver las filas insertadas.</summary>
    public static bool ShouldUseBulkCopy(int rowCount, bool returnInserted) => !returnInserted && rowCount > BulkCopyThresholdRows;
}
