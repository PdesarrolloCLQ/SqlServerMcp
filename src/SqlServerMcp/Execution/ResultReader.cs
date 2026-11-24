using System.Buffers;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Lee el conjunto de resultados actual y lo convierte en primitivos JSON estables (RF-QRY-05): null, bool, long,
/// double o string (DECIMAL/MONEY como texto exacto, fechas ISO 8601, binarios y tipos CLR en Base64 recortado,
/// uniqueidentifier como texto). Aplica el máximo de filas (RF-QRY-03, CA-20), el tamaño máximo de respuesta y el
/// largo máximo por celda (RF-QRY-06, RS-DAT-03), y el enmascaramiento o bloqueo de columnas sensibles o excluidas
/// (RS-DAT-01, RS-DAT-02). Los valores grandes se leen por fragmentos para acotar la memoria; con
/// <see cref="System.Data.CommandBehavior.SequentialAccess"/> nunca se materializa un valor completo.
/// </summary>
public sealed class ResultReader : IResultReader
{
    /// <summary>Marca que se agrega a las celdas recortadas.</summary>
    public const string TruncationMarker = "…";

    /// <summary>Regla que bloquea un resultado con columnas protegidas detectadas en tiempo de ejecución.</summary>
    public const string RuntimeBlockRuleId = "POL-DAT-RUNTIME";

    /// <summary>Motivo de truncamiento por número de filas.</summary>
    public const string ReasonMaxRows = "maxRows";

    /// <summary>Motivo de truncamiento por tamaño de respuesta.</summary>
    public const string ReasonMaxResponseBytes = "maxResponseBytes";

    /// <summary>Nombre de la columna que SQL Server usa para resultados FOR JSON.</summary>
    public const string ForJsonColumnName = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>Nombre de la columna que SQL Server usa para resultados FOR XML.</summary>
    public const string ForXmlColumnName = "XML_F52E2B61-18A1-11d1-B105-00805F49916B";

    private const int CharChunk = 8192;
    private const int ByteChunk = 8192;
    private const int MaxShownColumnName = 128;

    private static readonly HashSet<string> SystemClrTypes = new(StringComparer.OrdinalIgnoreCase) { "geography", "geometry", "hierarchyid" };

    private enum CellKind
    {
        Boolean,
        TinyInt,
        SmallInt,
        Int,
        BigInt,
        Real,
        Float,
        Decimal,
        Money,
        Date,
        DateTime,
        DateTimeOffset,
        Time,
        Guid,
        Text,
        Binary,
        ClrUdt,
        Variant,
        Other,
    }

    /// <inheritdoc />
    public async Task<TabularResult> ReadAsync(
        SqlDataReader reader,
        ProfileRuntime profile,
        int maxRows,
        int skipRows,
        IReadOnlyCollection<int> maskOrdinals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(maskOrdinals);

        var limits = profile.Limits;
        var rowLimit = Math.Max(1, maxRows <= 0 ? limits.MaxRows : Math.Min(maxRows, Math.Max(1, limits.MaxRows)));
        var cellLimit = Math.Max(1, limits.MaxCellChars);
        var maxBinaryBytes = MaxBinaryBytes(cellLimit);
        var byteLimit = (long)Math.Max(1, limits.MaxResponseBytes);
        var maskText = profile.Config.Sensitive.MaskText ?? "****";

        // El esquema se valida antes de leer cualquier fila (RS-DAT-01/02).
        var plans = DescribeColumns(reader, profile, maskOrdinals);
        var result = new TabularResult { Columns = plans.Select(p => p.Column).ToList() };
        if (plans.Count == 0)
        {
            return result;
        }

        var headerBytes = EstimateHeaderBytes(result.Columns);
        long rowBytesTotal = 0;
        var chars = ArrayPool<char>.Shared.Rent(CharChunk);
        var bytes = ArrayPool<byte>.Shared.Rent(ByteChunk);
        try
        {
            for (var skipped = 0; skipped < skipRows; skipped++)
            {
                // Leer la fila siguiente descarta las columnas no leídas de la actual (también con SequentialAccess).
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return result;
                }
            }

            while (true)
            {
                if (result.Rows.Count >= rowLimit)
                {
                    // Solo se comprueba si existe otra fila; no se drena el resto (CA-20).
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        result.Truncated = true;
                        result.TruncationReason = ReasonMaxRows;
                    }

                    break;
                }

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var row = new object?[plans.Count];
                long rowBytes = 2;
                var rowTruncatedCells = 0;
                for (var i = 0; i < plans.Count; i++)
                {
                    var (value, truncated) = await ReadCellAsync(reader, plans[i], cellLimit, maxBinaryBytes, maskText, chars, bytes, cancellationToken).ConfigureAwait(false);
                    row[i] = value;
                    rowBytes += EstimateJsonBytes(value) + 1;
                    if (truncated)
                    {
                        rowTruncatedCells++;
                    }
                }

                if (headerBytes + rowBytesTotal + rowBytes > byteLimit)
                {
                    result.Truncated = true;
                    result.TruncationReason = ReasonMaxResponseBytes;
                    break;
                }

                rowBytesTotal += rowBytes;
                result.TruncatedCells += rowTruncatedCells;
                result.Rows.Add(row);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
            ArrayPool<byte>.Shared.Return(bytes);
            result.ApproximateBytes = rowBytesTotal;
        }

        return result;
    }

    /// <summary>
    /// Advertencias en español derivadas de un resultado: truncamiento, celdas recortadas, columnas enmascaradas,
    /// tipos CLR devueltos en Base64 y fragmentación de FOR JSON/FOR XML (RF-OUT-01).
    /// </summary>
    public static List<string> BuildWarnings(TabularResult result, LimitsConfig limits)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(limits);
        var warnings = new List<string>();
        var rows = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        if (result.Truncated && result.TruncationReason == ReasonMaxRows)
        {
            warnings.Add($"Resultado truncado: se devolvieron {rows} filas y hay más disponibles. Agregue filtros o pagine con ORDER BY y OFFSET/FETCH.");
        }
        else if (result.Truncated && result.TruncationReason == ReasonMaxResponseBytes)
        {
            warnings.Add($"Resultado truncado al alcanzar el tamaño máximo de respuesta ({limits.MaxResponseBytes.ToString(CultureInfo.InvariantCulture)} bytes); se devolvieron {rows} filas. Seleccione menos columnas o filas.");
        }

        if (result.TruncatedCells > 0)
        {
            warnings.Add($"{result.TruncatedCells.ToString(CultureInfo.InvariantCulture)} celda(s) se recortaron a {limits.MaxCellChars.ToString(CultureInfo.InvariantCulture)} caracteres (terminan en «{TruncationMarker}»); los binarios se muestran en Base64 recortado.");
        }

        var masked = result.Columns.Where(c => c.Masked).Select(c => c.Name).ToList();
        if (masked.Count > 0)
        {
            warnings.Add("Columnas enmascaradas por contener datos sensibles: " + string.Join(", ", masked) + ".");
        }

        foreach (var column in result.Columns)
        {
            if (!column.Masked && IsClrUdtType(column.SqlType))
            {
                warnings.Add($"La columna '{column.Name}' ({column.SqlType}) se devuelve como Base64 de su serialización binaria nativa; " +
                    "para obtener texto convierta el valor en la consulta (por ejemplo, columna.ToString() o columna.STAsText()).");
            }
        }

        if (result.Rows.Count > 1 && result.Columns.Count == 1
            && (result.Columns[0].Name == ForJsonColumnName || result.Columns[0].Name == ForXmlColumnName))
        {
            warnings.Add("El resultado FOR JSON/FOR XML llega fragmentado en varias filas; concatene los valores en orden para obtener el documento completo.");
        }

        return warnings;
    }

    /// <summary>True si el tipo de columna es un tipo CLR (geography, geometry, hierarchyid o un tipo CLR de usuario "esquema.tipo").</summary>
    public static bool IsClrUdtType(string? sqlType) =>
        !string.IsNullOrEmpty(sqlType) && (SystemClrTypes.Contains(sqlType) || sqlType.Contains('.', StringComparison.Ordinal));

    /// <summary>Bytes máximos de un binario para que su Base64 no supere <paramref name="cellLimit"/> caracteres.</summary>
    internal static int MaxBinaryBytes(int cellLimit) => Math.Max(3, (int)Math.Min(int.MaxValue / 2, (long)cellLimit * 3 / 4 / 3 * 3));

    internal static string FormatDate(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string FormatDateTime(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    internal static string FormatDateTimeOffset(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    internal static string FormatTime(TimeSpan value)
    {
        var hours = (int)Math.Floor(value.TotalHours);
        var text = string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}");
        var fraction = value.Ticks % TimeSpan.TicksPerSecond;
        if (fraction == 0)
        {
            return text;
        }

        return text + "." + fraction.ToString("0000000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    /// <summary>double finito como número; NaN e infinito como texto (no representables en JSON).</summary>
    internal static object FormatDouble(double value)
    {
        if (double.IsFinite(value))
        {
            return value;
        }

        return double.IsNaN(value) ? "NaN" : (value > 0 ? "Infinity" : "-Infinity");
    }

    /// <summary>real → double por su representación decimal más corta (evita 1.100000023841858).</summary>
    internal static object FormatReal(float value) =>
        float.IsFinite(value)
            ? double.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture)
            : FormatDouble(value);

    internal static string FormatMoney(SqlMoney value) => value.ToDecimal().ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>Recorta un texto a <paramref name="limit"/> caracteres sin partir pares sustitutos y agrega la marca.</summary>
    internal static string TruncateText(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var cut = limit;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + TruncationMarker;
    }

    /// <summary>Base64 de como máximo <paramref name="maxBytes"/> bytes; si se recorta, agrega la marca.</summary>
    internal static (string Value, bool Truncated) FormatBinary(ReadOnlySpan<byte> data, int maxBytes) =>
        data.Length <= maxBytes
            ? (Convert.ToBase64String(data), false)
            : (Convert.ToBase64String(data[..maxBytes]) + TruncationMarker, true);

    /// <summary>
    /// Formatea un valor CLR o SqlTypes ya leído (sql_variant, tipos desconocidos) con las mismas reglas estables.
    /// </summary>
    internal static (object? Value, bool Truncated) FormatClrValue(object? value, int cellLimit)
    {
        var maxBytes = MaxBinaryBytes(cellLimit);
        switch (value)
        {
            case null:
            case DBNull:
                return (null, false);
            case INullable { IsNull: true }:
                return (null, false);
            case bool b:
                return (b, false);
            case byte or sbyte or short or ushort or int or uint or long:
                return (Convert.ToInt64(value, CultureInfo.InvariantCulture), false);
            case ulong ul:
                return ul <= long.MaxValue ? ((long)ul, false) : (ul.ToString(CultureInfo.InvariantCulture), false);
            case float f:
                return (FormatReal(f), false);
            case double d:
                return (FormatDouble(d), false);
            case decimal m:
                return (m.ToString(CultureInfo.InvariantCulture), false);
            case SqlBoolean sb:
                return (sb.Value, false);
            case SqlByte sbt:
                return ((long)sbt.Value, false);
            case SqlInt16 s16:
                return ((long)s16.Value, false);
            case SqlInt32 s32:
                return ((long)s32.Value, false);
            case SqlInt64 s64:
                return (s64.Value, false);
            case SqlSingle ss:
                return (FormatReal(ss.Value), false);
            case SqlDouble sd:
                return (FormatDouble(sd.Value), false);
            case SqlDecimal sdec:
                return (sdec.ToString(), false);
            case SqlMoney smoney:
                return (FormatMoney(smoney), false);
            case SqlDateTime sdt:
                return (FormatDateTime(sdt.Value), false);
            case DateTime dt:
                return (FormatDateTime(dt), false);
            case DateTimeOffset dto:
                return (FormatDateTimeOffset(dto), false);
            case DateOnly dOnly:
                return (dOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false);
            case TimeSpan ts:
                return (FormatTime(ts), false);
            case TimeOnly tOnly:
                return (FormatTime(tOnly.ToTimeSpan()), false);
            case SqlGuid sg:
                return (sg.Value.ToString("D"), false);
            case Guid g:
                return (g.ToString("D"), false);
            case SqlString sstr:
                return TextCell(sstr.Value, cellLimit);
            case string str:
                return TextCell(str, cellLimit);
            case SqlXml sx:
                return TextCell(sx.Value, cellLimit);
            case SqlChars sc:
                return TextCell(new string(sc.Value), cellLimit);
            case SqlBinary sbin:
            {
                var (b64, truncated) = FormatBinary(sbin.Value, maxBytes);
                return (b64, truncated);
            }

            case SqlBytes sbytes:
            {
                var (b64, truncated) = FormatBinary(sbytes.Value, maxBytes);
                return (b64, truncated);
            }

            case byte[] raw:
            {
                var (b64, truncated) = FormatBinary(raw, maxBytes);
                return (b64, truncated);
            }

            default:
                return TextCell(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, cellLimit);
        }
    }

    /// <summary>Tamaño aproximado del valor serializado como JSON (UTF-8).</summary>
    internal static long EstimateJsonBytes(object? value)
    {
        switch (value)
        {
            case null:
                return 4;
            case bool b:
                return b ? 4 : 5;
            case long l:
                return l.ToString(CultureInfo.InvariantCulture).Length;
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture).Length;
            case string s:
            {
                long extra = 0;
                foreach (var c in s)
                {
                    if (c is '"' or '\\')
                    {
                        extra++;
                    }
                    else if (c < 0x20)
                    {
                        extra += 5;
                    }
                }

                return Encoding.UTF8.GetByteCount(s) + 2 + extra;
            }

            default:
                return 16;
        }
    }

    private static (object? Value, bool Truncated) TextCell(string text, int cellLimit) =>
        text.Length > cellLimit ? (TruncateText(text, cellLimit), true) : (text, false);

    private static long EstimateHeaderBytes(IReadOnlyList<ResultColumn> columns)
    {
        long total = 64;
        foreach (var column in columns)
        {
            total += Encoding.UTF8.GetByteCount(column.Name) + Encoding.UTF8.GetByteCount(column.SqlType) + 48;
        }

        return total;
    }

    private static List<ColumnPlan> DescribeColumns(SqlDataReader reader, ProfileRuntime profile, IReadOnlyCollection<int> maskOrdinals)
    {
        var count = reader.FieldCount;
        var plans = new List<ColumnPlan>(count);
        if (count == 0)
        {
            return plans;
        }

        ReadOnlyCollection<DbColumn>? schema;
        try
        {
            schema = reader.GetColumnSchema();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or InvalidCastException
            or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            schema = null;
        }

        var maskSet = new HashSet<int>(maskOrdinals);
        var block = profile.Config.Sensitive.Action == SensitiveColumnAction.Block;
        for (var i = 0; i < count; i++)
        {
            var name = reader.GetName(i) ?? string.Empty;
            if (profile.IsColumnExcluded(null, null, name))
            {
                throw Blocked(name, AlertSeverity.Warning);
            }

            // Defensa en profundidad: además de las posiciones que indicó el análisis, se enmascara por nombre de columna
            // (secretos y, si la acción es "mask", datos personales). El bloqueo solo aplica a los secretos.
            var secret = profile.IsColumnSensitive(null, null, name);
            if (secret && block)
            {
                throw Blocked(name, AlertSeverity.Warning);
            }

            var sensitive = maskSet.Contains(i) || secret || profile.IsColumnPiiMasked(null, null, name);

            var column = schema is not null && i < schema.Count ? schema[i] : null;
            var rawType = reader.GetDataTypeName(i) ?? string.Empty;
            var (kind, sqlType) = Classify(rawType, column);
            plans.Add(new ColumnPlan(i, kind, new ResultColumn(name, sqlType, column?.AllowDBNull ?? true, sensitive)));
        }

        return plans;
    }

    private static ToolFailureException Blocked(string column, AlertSeverity alert)
    {
        var shown = column.Length > MaxShownColumnName ? column[..MaxShownColumnName] + TruncationMarker : column;
        return new ToolFailureException(
            ErrorCodes.PolicyBlocked,
            $"El resultado incluye la columna protegida '{shown}'; enumere explícitamente columnas permitidas",
            "Reemplace SELECT * o la expresión por la lista de columnas permitidas (consulte describe_table).",
            RuntimeBlockRuleId,
            alert);
    }

    private static (CellKind Kind, string SqlType) Classify(string rawType, DbColumn? column)
    {
        if (column?.UdtAssemblyQualifiedName is not null || rawType.Contains('.', StringComparison.Ordinal))
        {
            return (CellKind.ClrUdt, UdtName(rawType));
        }

        var name = rawType.ToLowerInvariant();
        return name switch
        {
            "bit" => (CellKind.Boolean, name),
            "tinyint" => (CellKind.TinyInt, name),
            "smallint" => (CellKind.SmallInt, name),
            "int" => (CellKind.Int, name),
            "bigint" => (CellKind.BigInt, name),
            "real" => (CellKind.Real, name),
            "float" => (CellKind.Float, name),
            "decimal" or "numeric" => (CellKind.Decimal, WithPrecision(name, column)),
            "money" or "smallmoney" => (CellKind.Money, name),
            "date" => (CellKind.Date, name),
            "datetime" or "smalldatetime" => (CellKind.DateTime, name),
            "datetime2" => (CellKind.DateTime, WithScale(name, column)),
            "datetimeoffset" => (CellKind.DateTimeOffset, WithScale(name, column)),
            "time" => (CellKind.Time, WithScale(name, column)),
            "uniqueidentifier" => (CellKind.Guid, name),
            "char" or "varchar" or "nchar" or "nvarchar" => (CellKind.Text, WithLength(name, column)),
            "text" or "ntext" or "xml" or "json" => (CellKind.Text, name),
            "binary" or "varbinary" => (CellKind.Binary, WithLength(name, column)),
            "image" or "timestamp" or "rowversion" => (CellKind.Binary, name),
            "sql_variant" => (CellKind.Variant, name),
            _ => (CellKind.Other, name.Length == 0 ? "desconocido" : name),
        };
    }

    /// <summary>"Base.sys.geography" → "geography"; "Base.dbo.MiTipo" → "dbo.MiTipo".</summary>
    private static string UdtName(string rawType)
    {
        var parts = rawType.Split('.');
        if (parts.Length >= 2 && parts[^2].Equals("sys", StringComparison.OrdinalIgnoreCase))
        {
            return parts[^1];
        }

        return parts.Length >= 2 ? parts[^2] + "." + parts[^1] : rawType + ".clr";
    }

    private static string WithLength(string name, DbColumn? column)
    {
        var size = column?.ColumnSize;
        if (size is null or <= 0)
        {
            return name;
        }

        return size >= 1_073_741_823 ? name + "(max)" : name + "(" + size.Value.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string WithPrecision(string name, DbColumn? column)
    {
        var precision = column?.NumericPrecision;
        var scale = column?.NumericScale;
        return precision is > 0 and <= 38 && scale is >= 0 and <= 38
            ? $"{name}({precision.Value.ToString(CultureInfo.InvariantCulture)},{scale.Value.ToString(CultureInfo.InvariantCulture)})"
            : name;
    }

    private static string WithScale(string name, DbColumn? column)
    {
        var scale = column?.NumericScale;
        return scale is >= 0 and <= 7 ? $"{name}({scale.Value.ToString(CultureInfo.InvariantCulture)})" : name;
    }

    private static async ValueTask<(object? Value, bool Truncated)> ReadCellAsync(
        SqlDataReader reader,
        ColumnPlan plan,
        int cellLimit,
        int maxBinaryBytes,
        string maskText,
        char[] chars,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var i = plan.Ordinal;
        if (await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false))
        {
            return (null, false);
        }

        // Valor enmascarado: no se lee (RS-DAT-02).
        if (plan.Column.Masked)
        {
            return (maskText, false);
        }

        return plan.Kind switch
        {
            CellKind.Boolean => (reader.GetBoolean(i), false),
            CellKind.TinyInt => ((long)reader.GetByte(i), false),
            CellKind.SmallInt => ((long)reader.GetInt16(i), false),
            CellKind.Int => ((long)reader.GetInt32(i), false),
            CellKind.BigInt => (reader.GetInt64(i), false),
            CellKind.Real => (FormatReal(reader.GetFloat(i)), false),
            CellKind.Float => (FormatDouble(reader.GetDouble(i)), false),
            CellKind.Decimal => (reader.GetSqlDecimal(i).ToString(), false),
            CellKind.Money => (FormatMoney(reader.GetSqlMoney(i)), false),
            CellKind.Date => (FormatDate(reader.GetDateTime(i)), false),
            CellKind.DateTime => (FormatDateTime(reader.GetDateTime(i)), false),
            CellKind.DateTimeOffset => (FormatDateTimeOffset(reader.GetDateTimeOffset(i)), false),
            CellKind.Time => (FormatTime(reader.GetTimeSpan(i)), false),
            CellKind.Guid => (reader.GetGuid(i).ToString("D"), false),
            CellKind.Text => ReadText(reader, i, cellLimit, chars),
            CellKind.Binary => ReadBinary(reader, i, maxBinaryBytes, bytes),
            CellKind.ClrUdt => ReadClrUdt(reader, i, maxBinaryBytes, bytes),
            CellKind.Variant => FormatClrValue(reader.GetSqlValue(i), cellLimit),
            _ => ReadOther(reader, i, cellLimit),
        };
    }

    /// <summary>Lee texto por fragmentos hasta <paramref name="cellLimit"/> + 1 caracteres (memoria acotada).</summary>
    private static (object? Value, bool Truncated) ReadText(SqlDataReader reader, int ordinal, int cellLimit, char[] buffer)
    {
        try
        {
            var builder = new StringBuilder(Math.Min(cellLimit + 1, 1024));
            long offset = 0;
            while (builder.Length <= cellLimit)
            {
                var wanted = Math.Min(buffer.Length, cellLimit + 1 - builder.Length);
                var read = reader.GetChars(ordinal, offset, buffer, 0, wanted);
                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, (int)read);
                offset += read;
            }

            return builder.Length > cellLimit
                ? (TruncateText(builder.ToString(), cellLimit), true)
                : (builder.ToString(), false);
        }
        catch (InvalidCastException)
        {
            // Tipos de texto sin acceso por fragmentos en esta versión del driver (por ejemplo, json nativo).
            return ReadOther(reader, ordinal, cellLimit);
        }
    }

    /// <summary>Lee binarios por fragmentos hasta <paramref name="maxBytes"/> + 1 bytes.</summary>
    private static (object? Value, bool Truncated) ReadBinary(SqlDataReader reader, int ordinal, int maxBytes, byte[] buffer)
    {
        using var collected = new MemoryStream();
        long offset = 0;
        while (collected.Length <= maxBytes)
        {
            var wanted = (int)Math.Min(buffer.Length, maxBytes + 1 - collected.Length);
            var read = reader.GetBytes(ordinal, offset, buffer, 0, wanted);
            if (read <= 0)
            {
                break;
            }

            collected.Write(buffer, 0, (int)read);
            offset += read;
        }

        var (value, truncated) = FormatBinary(collected.GetBuffer().AsSpan(0, (int)collected.Length), maxBytes);
        return (value, truncated);
    }

    /// <summary>
    /// Tipos CLR (geography, geometry, hierarchyid…): se leen sus bytes nativos sin cargar Microsoft.SqlServer.Types.
    /// </summary>
    private static (object? Value, bool Truncated) ReadClrUdt(SqlDataReader reader, int ordinal, int maxBytes, byte[] buffer)
    {
        try
        {
            return ReadBinary(reader, ordinal, maxBytes, buffer);
        }
        catch (InvalidCastException)
        {
            using var stream = reader.GetStream(ordinal);
            using var collected = new MemoryStream();
            int read;
            while (collected.Length <= maxBytes
                && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes + 1 - collected.Length))) > 0)
            {
                collected.Write(buffer, 0, read);
            }

            var (value, truncated) = FormatBinary(collected.GetBuffer().AsSpan(0, (int)collected.Length), maxBytes);
            return (value, truncated);
        }
    }

    private static (object? Value, bool Truncated) ReadOther(SqlDataReader reader, int ordinal, int cellLimit)
    {
        try
        {
            return FormatClrValue(reader.GetValue(ordinal), cellLimit);
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or InvalidOperationException
            or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return ($"(valor de tipo {reader.GetDataTypeName(ordinal)} no representable)", false);
        }
    }

    private sealed record ColumnPlan(int Ordinal, CellKind Kind, ResultColumn Column);
}
