using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Execution;

/// <summary>
/// Construye la DataTable tipada que alimenta SqlBulkCopy en insert_rows (RF-DML-07). Los valores se convierten con
/// <see cref="IParameterBinder.BindForCatalogType"/> para aplicar exactamente las mismas reglas de validación y
/// conversión que la ruta parametrizada (RF-DML-08).
/// </summary>
public static class DmlBulkCopyTableBuilder
{
    /// <summary>Tipo CLR de la columna de la DataTable según el tipo de catálogo (o su tipo base si es un alias).</summary>
    public static Type MapClrType(CatalogType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (type.BaseTypeName ?? type.TypeName).ToLowerInvariant() switch
        {
            "bigint" => typeof(long),
            "int" => typeof(int),
            "smallint" => typeof(short),
            "tinyint" => typeof(byte),
            "bit" => typeof(bool),
            "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "float" => typeof(double),
            "real" => typeof(float),
            "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            "datetimeoffset" => typeof(DateTimeOffset),
            "time" => typeof(TimeSpan),
            "uniqueidentifier" => typeof(Guid),
            "binary" or "varbinary" or "image" => typeof(byte[]),
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" or "sysname" => typeof(string),
            _ => typeof(object),
        };
    }

    /// <summary>Crea la DataTable con una columna por destino (nombres "c0", "c1"…; el mapeo se hace por posición).</summary>
    public static DataTable Build(IReadOnlyList<CatalogColumn> columns, IReadOnlyList<JsonElement[]> rows, IReadOnlyList<int> rowNumbers, IParameterBinder binder)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rowNumbers);
        ArgumentNullException.ThrowIfNull(binder);

        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        var types = new Type[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            types[c] = MapClrType(columns[c].Type);
            table.Columns.Add(new DataColumn("c" + c.ToString(CultureInfo.InvariantCulture), types[c]) { AllowDBNull = true });
        }

        table.BeginLoadData();
        for (var r = 0; r < rows.Count; r++)
        {
            var values = new object[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                var parameter = binder.BindForCatalogType(DmlInsertPlanner.ParameterName(r, c), columns[c].Type, rows[r][c]);
                values[c] = ConvertValue(parameter.Value, types[c], columns[c].Name, rowNumbers[r]);
            }

            table.Rows.Add(values);
        }

        table.EndLoadData();
        return table;
    }

    /// <summary>Convierte el valor ya validado por el enlazador al tipo de la columna de la DataTable.</summary>
    public static object ConvertValue(object? value, Type targetType, string columnName, int rowNumber)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        var plain = Unwrap(value);
        if (plain is null)
        {
            return DBNull.Value;
        }

        if (targetType == typeof(object) || targetType.IsInstanceOfType(plain))
        {
            return plain;
        }

        try
        {
            return plain switch
            {
                DateTime dt when targetType == typeof(DateTimeOffset) => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                DateTimeOffset dto when targetType == typeof(DateTime) => dto.UtcDateTime,
                string s when targetType == typeof(Guid) => Guid.Parse(s),
                string s when targetType == typeof(TimeSpan) => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
                string s when targetType == typeof(DateTimeOffset) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                string s when targetType == typeof(byte[]) => Convert.FromBase64String(s),
                IConvertible convertible => convertible.ToType(targetType, CultureInfo.InvariantCulture),
                _ => throw new InvalidCastException(),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"El valor de la columna '{columnName}' en la fila {rowNumber.ToString(CultureInfo.InvariantCulture)} no es compatible con su tipo.",
                "Revise el tipo de la columna con describe_table.");
        }
    }

    private static object? Unwrap(object? value) => value switch
    {
        null or DBNull => null,
        INullable { IsNull: true } => null,
        SqlString v => v.Value,
        SqlInt64 v => v.Value,
        SqlInt32 v => v.Value,
        SqlInt16 v => v.Value,
        SqlByte v => v.Value,
        SqlBoolean v => v.Value,
        SqlDecimal v => v.Value,
        SqlMoney v => v.Value,
        SqlDouble v => v.Value,
        SqlSingle v => v.Value,
        SqlDateTime v => v.Value,
        SqlGuid v => v.Value,
        SqlBinary v => v.Value,
        SqlBytes v => v.Value,
        SqlChars v => new string(v.Value),
        SqlXml v => v.Value,
        _ => value,
    };
}
