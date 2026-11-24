using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Audit;
using SqlServerMcp.Configuration;
using SqlServerMcp.Execution;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.IntegrationTests.Write;

/// <summary>
/// Lector de resultados mínimo para las pruebas de escritura (el real lo implementa otro módulo): convierte valores a
/// primitivos JSON, aplica el máximo de filas y enmascara por nombre sensible.
/// </summary>
internal sealed class SimpleResultReader : IResultReader
{
    public async Task<TabularResult> ReadAsync(SqlDataReader reader, ProfileRuntime profile, int maxRows, int skipRows, IReadOnlyCollection<int> maskOrdinals, CancellationToken cancellationToken)
    {
        var result = new TabularResult();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var masked = maskOrdinals.Contains(i) || profile.IsColumnSensitive(null, null, reader.GetName(i));
            result.Columns.Add(new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i), true, masked));
        }

        var skipped = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (skipped < skipRows)
            {
                skipped++;
                continue;
            }

            if (result.Rows.Count >= maxRows)
            {
                result.Truncated = true;
                result.TruncationReason = "maxRows";
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = result.Columns[i].Masked && !reader.IsDBNull(i) ? profile.Config.Sensitive.MaskText : Convert(reader.GetValue(i));
            }

            result.Rows.Add(row);
            result.ApproximateBytes += JsonSerializer.SerializeToUtf8Bytes(row).Length;
        }

        return result;
    }

    private static object? Convert(object value) => value switch
    {
        DBNull => null,
        byte or short or int or long => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double or float => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
        bool b => b,
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        byte[] bytes => System.Convert.ToBase64String(bytes),
        string s => s,
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}

/// <summary>Enlazador mínimo para pruebas: tipos explícitos o inferidos del JSON y tipos de catálogo comunes.</summary>
internal sealed class SimpleParameterBinder : IParameterBinder
{
    public SqlParameter Bind(QueryParameter parameter)
    {
        var name = parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name;
        if (parameter.Type is { } declared)
        {
            return BindForCatalogType(name, ParseType(declared), parameter.Value);
        }

        var value = parameter.Value;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var l) => new SqlParameter(name, SqlDbType.BigInt) { Value = l },
            JsonValueKind.Number => new SqlParameter(name, SqlDbType.Decimal) { Value = value.GetDecimal() },
            JsonValueKind.True or JsonValueKind.False => new SqlParameter(name, SqlDbType.Bit) { Value = value.GetBoolean() },
            JsonValueKind.String => new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = value.GetString() },
            _ => new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = DBNull.Value },
        };
    }

    public SqlParameter BindForCatalogType(string parameterName, CatalogType type, JsonElement value)
    {
        var name = parameterName.StartsWith('@') ? parameterName : "@" + parameterName;
        var typeName = (type.BaseTypeName ?? type.TypeName).ToLowerInvariant();
        var parameter = typeName switch
        {
            "int" => new SqlParameter(name, SqlDbType.Int),
            "bigint" => new SqlParameter(name, SqlDbType.BigInt),
            "smallint" => new SqlParameter(name, SqlDbType.SmallInt),
            "tinyint" => new SqlParameter(name, SqlDbType.TinyInt),
            "bit" => new SqlParameter(name, SqlDbType.Bit),
            "decimal" or "numeric" => new SqlParameter(name, SqlDbType.Decimal) { Precision = type.Precision, Scale = type.Scale },
            "money" => new SqlParameter(name, SqlDbType.Money),
            "float" => new SqlParameter(name, SqlDbType.Float),
            "nvarchar" or "nchar" => new SqlParameter(name, SqlDbType.NVarChar, type.MaxLength == -1 ? -1 : type.MaxLength / 2),
            "varchar" or "char" => new SqlParameter(name, SqlDbType.VarChar, type.MaxLength),
            "datetime2" => new SqlParameter(name, SqlDbType.DateTime2) { Scale = type.Scale },
            "datetime" => new SqlParameter(name, SqlDbType.DateTime),
            "date" => new SqlParameter(name, SqlDbType.Date),
            "uniqueidentifier" => new SqlParameter(name, SqlDbType.UniqueIdentifier),
            "varbinary" or "binary" => new SqlParameter(name, SqlDbType.VarBinary, type.MaxLength),
            _ => new SqlParameter(name, SqlDbType.NVarChar, -1),
        };
        parameter.Value = ConvertValue(value, typeName);
        return parameter;
    }

    private static object ConvertValue(JsonElement value, string typeName)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DBNull.Value;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return typeName switch
        {
            "int" => int.Parse(text, CultureInfo.InvariantCulture),
            "bigint" => long.Parse(text, CultureInfo.InvariantCulture),
            "smallint" => short.Parse(text, CultureInfo.InvariantCulture),
            "tinyint" => byte.Parse(text, CultureInfo.InvariantCulture),
            "bit" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : text == "1",
            "decimal" or "numeric" or "money" => decimal.Parse(text, CultureInfo.InvariantCulture),
            "float" => double.Parse(text, CultureInfo.InvariantCulture),
            "datetime2" or "datetime" or "date" => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "uniqueidentifier" => Guid.Parse(text),
            "varbinary" or "binary" => System.Convert.FromBase64String(text),
            _ => text,
        };
    }

    private static CatalogType ParseType(string declared)
    {
        var open = declared.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return new CatalogType(declared.Trim(), -1, 18, 0);
        }

        var name = declared[..open].Trim();
        var args = declared[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (args[0].Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            return new CatalogType(name, -1, 0, 0);
        }

        var first = int.Parse(args[0], CultureInfo.InvariantCulture);
        return name.ToLowerInvariant() switch
        {
            "decimal" or "numeric" => new CatalogType(name, 9, (byte)first, args.Length > 1 ? byte.Parse(args[1], CultureInfo.InvariantCulture) : (byte)0),
            "nvarchar" or "nchar" => new CatalogType(name, (short)(first * 2), 0, 0),
            _ => new CatalogType(name, (short)first, 0, 0),
        };
    }
}

/// <summary>Consultas de catálogo mínimas para pruebas (la implementación real es de otro módulo). Siempre parametrizadas.</summary>
internal sealed class SimpleCatalogLookup(ISqlConnectionFactory factory) : ICatalogLookup
{
    public async Task<CatalogObject?> FindObjectAsync(ProfileRuntime profile, SqlObjectName name, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        var schema = name.Schema ?? "dbo";
        if (!profile.IsSchemaAllowed(schema) || profile.IsObjectExcluded(schema, name.Name))
        {
            return null;
        }

        var rows = await QueryAsync(profile, connection, transaction, cancellationToken,
            "SELECT o.object_id, s.name, o.name, RTRIM(o.type), o.type_desc FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = @s AND o.name = @n AND o.is_ms_shipped = 0;",
            [new SqlParameter("@s", SqlDbType.NVarChar, 128) { Value = schema }, new SqlParameter("@n", SqlDbType.NVarChar, 128) { Value = name.Name }],
            r => new CatalogObject(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<CatalogColumn>> GetColumnsAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        var columns = await QueryAsync(profile, connection, transaction, cancellationToken,
            "SELECT c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, " +
            "CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit), c.is_rowguidcol, CAST(CASE WHEN c.system_type_id = 189 THEN 1 ELSE 0 END AS bit), " +
            "t.is_user_defined, bt.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "LEFT JOIN sys.types bt ON t.is_user_defined = 1 AND bt.user_type_id = t.system_type_id WHERE c.object_id = @id ORDER BY c.column_id;",
            [new SqlParameter("@id", SqlDbType.Int) { Value = table.ObjectId }],
            r => new CatalogColumn(
                r.GetInt32(0),
                r.GetString(1),
                new CatalogType(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5), r.GetBoolean(12), r.IsDBNull(13) ? null : r.GetString(13)),
                r.GetBoolean(6),
                r.GetBoolean(7),
                r.GetBoolean(8),
                r.GetBoolean(9),
                r.GetBoolean(10),
                r.GetBoolean(11)));
        return columns.Where(c => !profile.IsColumnExcluded(table.Schema, table.Name, c.Name)).ToList();
    }

    public async Task<IReadOnlyList<CatalogParameter>> GetParametersAsync(ProfileRuntime profile, CatalogObject routine, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null) =>
        await QueryAsync(profile, connection, transaction, cancellationToken,
            "SELECT p.parameter_id, p.name, t.name, p.max_length, p.precision, p.scale, p.is_output, p.has_default_value, p.is_readonly, t.is_user_defined, bt.name " +
            "FROM sys.parameters p JOIN sys.types t ON t.user_type_id = p.user_type_id " +
            "LEFT JOIN sys.types bt ON t.is_user_defined = 1 AND t.is_table_type = 0 AND bt.user_type_id = t.system_type_id " +
            "WHERE p.object_id = @id AND p.parameter_id > 0 ORDER BY p.parameter_id;",
            [new SqlParameter("@id", SqlDbType.Int) { Value = routine.ObjectId }],
            r => new CatalogParameter(
                r.GetInt32(0),
                r.GetString(1),
                new CatalogType(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5), r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetString(10)),
                r.GetBoolean(6),
                r.GetBoolean(7),
                r.GetBoolean(8)));

    public async Task<long?> GetApproximateRowCountAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        var rows = await QueryAsync(profile, connection, transaction, cancellationToken,
            "SELECT CAST(SUM(p.rows) AS bigint) FROM sys.partitions p WHERE p.object_id = @id AND p.index_id IN (0, 1);",
            [new SqlParameter("@id", SqlDbType.Int) { Value = table.ObjectId }],
            r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0));
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> GetDependentsAsync(ProfileRuntime profile, CatalogObject target, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null) =>
        await QueryAsync(profile, connection, transaction, cancellationToken,
            "SELECT DISTINCT s.name + N'.' + o.name FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id = d.referencing_id " +
            "JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE d.referenced_id = @id;",
            [new SqlParameter("@id", SqlDbType.Int) { Value = target.ObjectId }],
            r => r.GetString(0));

    public async Task<string?> GetModuleDefinitionAsync(ProfileRuntime profile, CatalogObject module, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null)
    {
        var rows = await QueryAsync(profile, connection, transaction, cancellationToken,
            "SELECT OBJECT_DEFINITION(@id);",
            [new SqlParameter("@id", SqlDbType.Int) { Value = module.ObjectId }],
            r => r.IsDBNull(0) ? null : r.GetString(0));
        return rows.FirstOrDefault();
    }

    public Task<IReadOnlyList<string>> GetClassifiedColumnsAsync(ProfileRuntime profile, CatalogObject table, CancellationToken cancellationToken, SqlConnection? connection = null, SqlTransaction? transaction = null) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    private async Task<List<T>> QueryAsync<T>(ProfileRuntime profile, SqlConnection? connection, SqlTransaction? transaction, CancellationToken cancellationToken, string sql, SqlParameter[] parameters, Func<SqlDataReader, T> map)
    {
        var owned = connection is null;
        var conn = connection ?? await factory.OpenAsync(profile, ConnectionRole.Read, "catalog", cancellationToken);
        try
        {
            await using var command = conn.CreateCommand();
            command.Transaction = owned ? null : transaction;
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var list = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(map(reader));
            }

            return list;
        }
        finally
        {
            if (owned)
            {
                await conn.DisposeAsync();
            }
        }
    }
}

/// <summary>Registro de auditoría en memoria.</summary>
internal sealed class RecordingAuditLog : IAuditLog
{
    private readonly Lock _sync = new();
    private readonly List<AuditRecord> _records = [];

    public bool IsHealthy => true;

    public IReadOnlyList<AuditRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return [.. _records];
            }
        }
    }

    public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Registro de cambios de esquema que siempre falla (para comprobar el comportamiento fail-closed).</summary>
internal sealed class FailingSchemaChangeLog : ISchemaChangeLog
{
    public Task RecordAsync(SchemaChangeRecord record, CancellationToken cancellationToken = default) =>
        throw new AuditWriteException("Registro de cambios no disponible (prueba).");
}
