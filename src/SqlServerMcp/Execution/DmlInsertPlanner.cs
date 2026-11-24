using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Execution;

/// <summary>Filas de insert_rows que comparten el mismo conjunto de columnas.</summary>
/// <param name="Columns">Columnas de destino, en orden de column_id.</param>
/// <param name="Rows">Valores de cada fila, alineados con <paramref name="Columns"/>.</param>
/// <param name="RowNumbers">Número (1-based) de cada fila en la solicitud original, para los mensajes de error.</param>
public sealed record DmlInsertGroup(IReadOnlyList<CatalogColumn> Columns, IReadOnlyList<JsonElement[]> Rows, IReadOnlyList<int> RowNumbers);

/// <summary>Plan validado de una inserción estructurada (RF-DML-07, RF-DML-08).</summary>
/// <param name="Table">Tabla de destino resuelta en el catálogo.</param>
/// <param name="Groups">Grupos de filas por conjunto de columnas, en orden de primera aparición.</param>
/// <param name="OutputColumns">Columnas visibles (sin excluidas) que se devuelven con OUTPUT si se pidieron las filas insertadas.</param>
public sealed record DmlInsertPlan(CatalogObject Table, IReadOnlyList<DmlInsertGroup> Groups, IReadOnlyList<CatalogColumn> OutputColumns)
{
    /// <summary>Nombre de destino delimitado con QUOTENAME ("[esquema].[tabla]").</summary>
    public string QuotedTable => SqlNames.Quote(Table.Schema) + "." + SqlNames.Quote(Table.Name);
}

/// <summary>
/// Valida las filas de insert_rows contra el catálogo y genera las sentencias INSERT parametrizadas (RF-DML-07, RF-DML-08).
/// Los identificadores solo provienen del catálogo y se delimitan con <see cref="SqlNames.Quote"/>; los valores nunca
/// se concatenan: viajan como parámetros <c>@p{fila}_{columna}</c>. Es puro (sin I/O).
/// </summary>
public static class DmlInsertPlanner
{
    /// <summary>
    /// Valida y agrupa las filas. Lanza <see cref="ToolFailureException"/> con INVALID_ARGUMENT si una fila está vacía,
    /// nombra una columna desconocida o excluida, repite una columna o intenta escribir columnas IDENTITY, calculadas o rowversion.
    /// </summary>
    public static DmlInsertPlan Plan(
        ProfileRuntime profile,
        CatalogObject table,
        IReadOnlyList<CatalogColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> rows)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        // Las columnas excluidas nunca son resolubles (RS-DAT-01), aunque el catálogo las devolviera.
        var visible = columns
            .Where(c => !profile.IsColumnExcluded(table.Schema, table.Name, c.Name))
            .OrderBy(c => c.ColumnId)
            .ToList();
        var byName = new Dictionary<string, CatalogColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in visible)
        {
            byName.TryAdd(column.Name, column);
        }

        var groups = new Dictionary<string, (List<CatalogColumn> Columns, List<JsonElement[]> Rows, List<int> Numbers)>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = index + 1;
            var row = rows[index] ?? throw InvalidArgument($"La fila {rowNumber} es nula.", "Envíe cada fila como un objeto JSON columna → valor.");
            if (row.Count == 0)
            {
                throw InvalidArgument(
                    $"La fila {rowNumber} no contiene columnas.",
                    "Indique al menos una columna con su valor; las columnas omitidas toman su valor por defecto.");
            }

            var resolved = new List<(CatalogColumn Column, JsonElement Value)>(row.Count);
            foreach (var (columnKey, value) in row)
            {
                var column = ResolveColumn(columnKey, byName, table, rowNumber);
                if (resolved.Exists(r => r.Column.ColumnId == column.ColumnId))
                {
                    throw InvalidArgument(
                        $"La columna '{column.Name}' aparece más de una vez en la fila {rowNumber}.",
                        "Indique cada columna una sola vez por fila.");
                }

                resolved.Add((column, value));
            }

            resolved.Sort((a, b) => a.Column.ColumnId.CompareTo(b.Column.ColumnId));
            var key = string.Join(',', resolved.Select(r => r.Column.ColumnId.ToString(CultureInfo.InvariantCulture)));
            if (!groups.TryGetValue(key, out var group))
            {
                group = (resolved.Select(r => r.Column).ToList(), [], []);
                groups[key] = group;
                order.Add(key);
            }

            group.Rows.Add(resolved.Select(r => r.Value).ToArray());
            group.Numbers.Add(rowNumber);
        }

        var planGroups = order
            .Select(k => new DmlInsertGroup(groups[k].Columns, groups[k].Rows, groups[k].Numbers))
            .ToList();
        return new DmlInsertPlan(table, planGroups, visible);
    }

    /// <summary>
    /// Genera "INSERT INTO [s].[t] ([c1],[c2]) [OUTPUT INSERTED.[a], …] VALUES (@p0_0,@p0_1),(…);" seguido del conteo exacto
    /// de filas insertadas (@@ROWCOUNT, sin contar las de desencadenadores) con el alias indicado.
    /// </summary>
    public static string BuildInsertSql(
        DmlInsertPlan plan,
        IReadOnlyList<CatalogColumn> columns,
        int rowCount,
        IReadOnlyList<CatalogColumn>? outputColumns,
        string rowCountAlias)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowCount, 1);
        if (columns.Count * rowCount > DmlRowLimits.MaxParametersPerInsertStatement || rowCount > DmlRowLimits.MaxRowsPerInsertStatement)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), "El lote supera el máximo de filas o parámetros por sentencia.");
        }

        var sb = new StringBuilder(64 + (rowCount * columns.Count * 10));
        sb.Append("INSERT INTO ").Append(plan.QuotedTable).Append(" (");
        sb.AppendJoin(", ", columns.Select(c => SqlNames.Quote(c.Name)));
        sb.Append(')');
        if (outputColumns is { Count: > 0 })
        {
            sb.Append("\nOUTPUT ");
            sb.AppendJoin(", ", outputColumns.Select(c => "INSERTED." + SqlNames.Quote(c.Name)));
        }

        sb.Append("\nVALUES ");
        for (var r = 0; r < rowCount; r++)
        {
            if (r > 0)
            {
                sb.Append(", ");
            }

            sb.Append('(');
            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(ParameterName(r, c));
            }

            sb.Append(')');
        }

        sb.Append(";\nSELECT CAST(@@ROWCOUNT AS bigint) AS ").Append(SqlNames.Quote(rowCountAlias)).Append(';');
        return sb.ToString();
    }

    /// <summary>Nombre del parámetro de la fila <paramref name="row"/> (dentro del lote) y la columna <paramref name="column"/>.</summary>
    public static string ParameterName(int row, int column) =>
        string.Create(CultureInfo.InvariantCulture, $"@p{row}_{column}");

    private static CatalogColumn ResolveColumn(string key, Dictionary<string, CatalogColumn> byName, CatalogObject table, int rowNumber)
    {
        var name = key is null ? null : SqlNames.Normalize(key);
        if (!SqlNames.IsValidIdentifier(name))
        {
            throw InvalidArgument(
                $"La fila {rowNumber} contiene un nombre de columna no válido.",
                "Use los nombres de columna que devuelve describe_table.");
        }

        if (!byName.TryGetValue(name!, out var column))
        {
            throw InvalidArgument(
                $"La columna '{name}' no existe en {table.QualifiedName} o no está disponible (fila {rowNumber}).",
                "Consulte las columnas con describe_table.");
        }

        if (column.IsIdentity)
        {
            throw InvalidArgument(
                $"La columna '{column.Name}' es IDENTITY: su valor lo genera SQL Server y no puede indicarse (fila {rowNumber}).",
                "Omita la columna en las filas.");
        }

        if (column.IsComputed)
        {
            throw InvalidArgument(
                $"La columna '{column.Name}' es calculada y no admite valores (fila {rowNumber}).",
                "Omita la columna en las filas.");
        }

        if (column.IsRowVersion)
        {
            throw InvalidArgument(
                $"La columna '{column.Name}' es rowversion: su valor lo genera SQL Server (fila {rowNumber}).",
                "Omita la columna en las filas.");
        }

        return column;
    }

    private static ToolFailureException InvalidArgument(string message, string suggestion) =>
        new(ErrorCodes.InvalidArgument, message, suggestion);
}
