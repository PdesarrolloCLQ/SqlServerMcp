using System.Text;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>Tablas, vistas, índices, relaciones y estadísticas (RF-MET-02, 03, 07, 08, 11).</summary>
public sealed partial class MetadataService
{
    /// <summary>Máximo de aristas devueltas por get_relationships.</summary>
    internal const int MaxRelationshipEdges = 2000;

    private const string DescribeTableSql = $"""
        SELECT o.create_date, o.modify_date, CONVERT(nvarchar(max), ep.value),
               t.temporal_type, hs.name, h.name,
               (SELECT SUM(pa.rows) FROM sys.partitions AS pa WHERE pa.object_id = o.object_id AND pa.index_id IN (0, 1)),
               per.start_column, per.end_column
        FROM sys.objects AS o
        LEFT JOIN sys.tables AS t ON t.object_id = o.object_id
        LEFT JOIN sys.objects AS h ON h.object_id = t.history_table_id
        LEFT JOIN sys.schemas AS hs ON hs.schema_id = h.schema_id
        LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        OUTER APPLY (SELECT TOP (1) COL_NAME(pr.object_id, pr.start_column_id) AS start_column, COL_NAME(pr.object_id, pr.end_column_id) AS end_column
                     FROM sys.periods AS pr WHERE pr.object_id = o.object_id) AS per
        WHERE o.object_id = @id AND o.name = @name;

        SELECT c.column_id, c.name, t.name, SCHEMA_NAME(t.schema_id), t.is_user_defined, {CatalogQueries.BaseTypeExpression},
               c.max_length, c.precision, c.scale, c.is_nullable,
               dc.name, dc.definition,
               c.is_identity, CONVERT(nvarchar(40), ic.seed_value), CONVERT(nvarchar(40), ic.increment_value),
               c.is_computed, cc.definition, cc.is_persisted,
               c.collation_name, c.is_sparse, c.is_rowguidcol, CASE WHEN c.system_type_id = 189 THEN 1 ELSE 0 END,
               CASE WHEN c.generated_always_type = 0 THEN NULL ELSE c.generated_always_type_desc END, c.is_hidden,
               CONVERT(nvarchar(max), epc.value)
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.extended_properties AS epc ON epc.class = 1 AND epc.major_id = c.object_id AND epc.minor_id = c.column_id AND epc.name = N'MS_Description'
        WHERE c.object_id = @id
        ORDER BY c.column_id;

        SELECT kc.name, RTRIM(kc.type), CASE WHEN i.type = 1 THEN 1 ELSE 0 END, ic.key_ordinal, c.name
        FROM sys.key_constraints AS kc
        JOIN sys.indexes AS i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE kc.parent_object_id = @id
        ORDER BY kc.name, ic.key_ordinal;

        SELECT fk.name, fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled, fk.is_not_trusted,
               rs.name, ro.name, fkc.constraint_column_id, pc.name, rc.name, fk.object_id
        FROM sys.foreign_keys AS fk
        JOIN sys.objects AS ro ON ro.object_id = fk.referenced_object_id
        JOIN sys.schemas AS rs ON rs.schema_id = ro.schema_id
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = @id
        ORDER BY fk.name, fkc.constraint_column_id;

        SELECT cc.name, cc.definition, COL_NAME(cc.parent_object_id, NULLIF(cc.parent_column_id, 0)), cc.is_disabled, cc.is_not_trusted
        FROM sys.check_constraints AS cc
        WHERE cc.parent_object_id = @id
        ORDER BY cc.name;

        {CatalogQueries.Indexes}
        """;

    /// <inheritdoc />
    /// <remarks>RF-MET-02, RF-MET-06, RNF-02: una sola ida y vuelta (total + página), filtrado y paginado en el servidor.</remarks>
    public Task<MetadataPage<TableSummary>> ListTablesAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string? namePattern,
        bool includeViews,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var schemaFilter = MetadataArguments.OptionalIdentifier(schema, "schema");
        var pattern = MetadataArguments.OptionalPattern(namePattern, "namePattern");
        (offset, limit) = MetadataPaging.Normalize(offset, limit);
        var patterns = ExclusionText.TextPatterns(profile);

        var parameters = new CatalogParameters();
        var filter = new VisibilityFilter(profile, parameters);
        var where = new StringBuilder()
            .Append("o.is_ms_shipped = 0 AND o.type IN (").Append(includeViews ? "'U', 'V'" : "'U'").Append(") AND ")
            .Append(filter.ObjectVisible("s.name", "o.name"));
        if (schemaFilter is not null)
        {
            where.Append(" AND s.name").Append(Ci).Append(" = ").Append(parameters.NVarChar("@schemaFilter", schemaFilter));
        }

        if (pattern is not null)
        {
            where.Append(" AND o.name").Append(Ci).Append(" LIKE ")
                .Append(parameters.NVarChar("@pattern", LikePatterns.FromWildcard(pattern, containsWhenNoWildcards: true), 1024))
                .Append(" ESCAPE N'\\'");
        }

        parameters.Int("@offset", offset);
        parameters.Int("@limit", limit);
        var sql = $"""
            SELECT COUNT_BIG(*) FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id WHERE {where};
            SELECT s.name, o.name, RTRIM(o.type), t.temporal_type, r.row_count, CONVERT(nvarchar(max), ep.value)
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.tables AS t ON t.object_id = o.object_id
            LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
            OUTER APPLY (SELECT SUM(pa.rows) AS row_count FROM sys.partitions AS pa WHERE pa.object_id = o.object_id AND pa.index_id IN (0, 1)) AS r
            WHERE {where}
            ORDER BY s.name, o.name
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        return RunAsync(profile, db, correlationId, connection => QueryAsync(
            connection,
            profile,
            db,
            sql,
            parameters,
            async reader =>
            {
                var total = await ReadCountAsync(reader, cancellationToken).ConfigureAwait(false);
                var items = new List<TableSummary>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var itemSchema = reader.GetStringOrEmpty(0);
                    var itemName = reader.GetStringOrEmpty(1);
                    if (!IsVisible(profile, itemSchema, itemName))
                    {
                        continue;
                    }

                    var type = reader.GetStringOrEmpty(2) == "V" ? "VIEW" : "TABLE";
                    items.Add(new TableSummary(
                        itemSchema,
                        itemName,
                        type,
                        reader.GetLongOrNull(4),
                        TemporalTypeName(reader.GetIntOrNull(3)),
                        SafeText(profile, patterns, reader.GetStringOrNull(5))));
                }

                return MetadataPaging.Page(db, items, total, offset, limit);
            },
            cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-03, RF-MET-06, RS-DAT-02: columnas visibles con todos sus atributos, PK, FK, UNIQUE, CHECK, índices,
    /// información temporal y MS_Description. Se omiten las columnas excluidas y las restricciones o índices que
    /// las usan; las expresiones que mencionan elementos excluidos se devuelven como null.
    /// </remarks>
    public Task<TableDescription> DescribeTableAsync(ProfileRuntime profile, string? database, string? schema, string name, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var (objectSchema, objectName) = MetadataArguments.ResolveObjectName(schema, name);
        var patterns = ExclusionText.TextPatterns(profile);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var target = await FindVisibleAsync(connection, profile, db, objectSchema, objectName, cancellationToken).ConfigureAwait(false);
            if (!target.IsTable && !target.IsView)
            {
                throw new ToolFailureException(
                    ErrorCodes.NotFound,
                    $"El objeto '{target.QualifiedName}' no es una tabla ni una vista.",
                    "Use describe_routine para procedimientos y funciones, o get_object_definition para su código.");
            }

            var classified = profile.Config.Sensitive.UseSqlClassification && target.IsTable
                ? new HashSet<string>(await _catalog.GetClassifiedColumnsAsync(profile, target, cancellationToken, connection).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return await QueryAsync(
                connection,
                profile,
                db,
                DescribeTableSql,
                ObjectParameters(target),
                reader => ReadTableDescriptionAsync(reader, profile, db, target, classified, patterns, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>RF-MET-08, RF-MET-06: los índices que incluyen columnas excluidas no se muestran.</remarks>
    public Task<IndexListResult> ListIndexesAsync(ProfileRuntime profile, string? database, string? schema, string table, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var (objectSchema, objectName) = MetadataArguments.ResolveObjectName(schema, table, "table");
        var patterns = ExclusionText.TextPatterns(profile);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var target = await FindVisibleAsync(connection, profile, db, objectSchema, objectName, cancellationToken).ConfigureAwait(false);
            if (!target.IsTable && !target.IsView)
            {
                throw new ToolFailureException(ErrorCodes.NotFound, $"El objeto '{target.QualifiedName}' no es una tabla ni una vista.", "Indique una tabla o una vista indexada.");
            }

            var indexes = await QueryAsync(
                connection,
                profile,
                db,
                CatalogQueries.Indexes,
                ObjectParameters(target),
                reader => ReadIndexesAsync(reader, profile, target, patterns, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new IndexListResult(db, target.Schema, target.Name, indexes);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>RF-MET-07, RF-MET-06: ambos extremos deben ser visibles y ninguna columna puede estar excluida.</remarks>
    public Task<RelationshipGraph> GetRelationshipsAsync(ProfileRuntime profile, string? database, string? schema, string? table, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var schemaFilter = MetadataArguments.OptionalIdentifier(schema, "schema");
        (string Schema, string Name)? tableName = string.IsNullOrWhiteSpace(table) ? null : MetadataArguments.ResolveObjectName(schemaFilter, table, "table");
        var patterns = ExclusionText.TextPatterns(profile);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var parameters = new CatalogParameters();
            var filter = new VisibilityFilter(profile, parameters);
            var where = new StringBuilder()
                .Append(filter.ObjectVisible("ps.name", "po.name")).Append(" AND ").Append(filter.ObjectVisible("rs.name", "ro.name"));
            if (tableName is { } tn)
            {
                var target = await FindVisibleAsync(connection, profile, db, tn.Schema, tn.Name, cancellationToken).ConfigureAwait(false);
                if (!target.IsTable)
                {
                    throw new ToolFailureException(ErrorCodes.NotFound, $"El objeto '{target.QualifiedName}' no es una tabla.", "Indique una tabla.");
                }

                where.Append(" AND (fk.parent_object_id = ").Append(parameters.Int("@id", target.ObjectId))
                    .Append(" OR fk.referenced_object_id = @id)");
            }
            else if (schemaFilter is not null)
            {
                var p = parameters.NVarChar("@schemaFilter", schemaFilter);
                where.Append(" AND (ps.name").Append(Ci).Append(" = ").Append(p).Append(" OR rs.name").Append(Ci).Append(" = ").Append(p).Append(')');
            }

            var sql = $"""
                SELECT fk.name, ps.name, po.name, rs.name, ro.name, fk.delete_referential_action_desc, fk.update_referential_action_desc,
                       fk.is_disabled, fk.is_not_trusted, pc.name, rc.name, fk.object_id
                FROM sys.foreign_keys AS fk
                JOIN sys.objects AS po ON po.object_id = fk.parent_object_id
                JOIN sys.schemas AS ps ON ps.schema_id = po.schema_id
                JOIN sys.objects AS ro ON ro.object_id = fk.referenced_object_id
                JOIN sys.schemas AS rs ON rs.schema_id = ro.schema_id
                JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
                JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE {where}
                ORDER BY ps.name, po.name, fk.name, fkc.constraint_column_id;
                """;

            return await QueryAsync(
                connection,
                profile,
                db,
                sql,
                parameters,
                async reader =>
                {
                    var edges = new List<RelationshipEdge>();
                    var truncated = false;
                    FkBuilder? current = null;
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var id = reader.GetInt(11);
                        if (current is null || current.Id != id)
                        {
                            if (current is not null && !current.Add(edges, profile, patterns))
                            {
                                truncated = true;
                                break;
                            }

                            current = new FkBuilder(
                                id,
                                reader.GetStringOrEmpty(0),
                                reader.GetStringOrEmpty(1),
                                reader.GetStringOrEmpty(2),
                                reader.GetStringOrEmpty(3),
                                reader.GetStringOrEmpty(4),
                                reader.GetStringOrEmpty(5),
                                reader.GetStringOrEmpty(6),
                                reader.GetBool(7),
                                !reader.GetBool(8));
                        }

                        current.FromColumns.Add(reader.GetStringOrEmpty(9));
                        current.ToColumns.Add(reader.GetStringOrEmpty(10));
                    }

                    if (!truncated && current is not null && !current.Add(edges, profile, patterns))
                    {
                        truncated = true;
                    }

                    return new RelationshipGraph(db, edges, truncated);
                },
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-11: filas de sys.partitions y espacio de sys.allocation_units (como sp_spaceused), sin COUNT(*) y sin
    /// requerir VIEW DATABASE STATE. Fecha de última actualización de estadísticas con STATS_DATE.
    /// </remarks>
    public Task<MetadataPage<TableStatsInfo>> GetTableStatsAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string? table,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var schemaFilter = MetadataArguments.OptionalIdentifier(schema, "schema");
        (string Schema, string Name)? tableName = string.IsNullOrWhiteSpace(table) ? null : MetadataArguments.ResolveObjectName(schemaFilter, table, "table");
        (offset, limit) = MetadataPaging.Normalize(offset, limit);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var parameters = new CatalogParameters();
            var filter = new VisibilityFilter(profile, parameters);
            var where = new StringBuilder("o.type = 'U' AND o.is_ms_shipped = 0 AND ").Append(filter.ObjectVisible("s.name", "o.name"));
            if (tableName is { } tn)
            {
                var target = await FindVisibleAsync(connection, profile, db, tn.Schema, tn.Name, cancellationToken).ConfigureAwait(false);
                if (!target.IsTable)
                {
                    throw new ToolFailureException(ErrorCodes.NotFound, $"El objeto '{target.QualifiedName}' no es una tabla.", "Indique una tabla.");
                }

                where.Append(" AND o.object_id = ").Append(parameters.Int("@id", target.ObjectId));
            }
            else if (schemaFilter is not null)
            {
                where.Append(" AND s.name").Append(Ci).Append(" = ").Append(parameters.NVarChar("@schemaFilter", schemaFilter));
            }

            parameters.Int("@offset", offset);
            parameters.Int("@limit", limit);
            var sql = $"""
                SELECT COUNT_BIG(*) FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id WHERE {where};
                SELECT s.name, o.name, r.row_count, a.reserved_pages, a.used_pages, a.data_pages, st.last_update
                FROM sys.objects AS o
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                OUTER APPLY (SELECT SUM(pa.rows) AS row_count FROM sys.partitions AS pa WHERE pa.object_id = o.object_id AND pa.index_id IN (0, 1)) AS r
                OUTER APPLY (
                    SELECT SUM(au.total_pages) AS reserved_pages, SUM(au.used_pages) AS used_pages,
                           SUM(CASE WHEN au.type = 1 THEN CASE WHEN pa.index_id < 2 THEN au.data_pages ELSE 0 END ELSE au.used_pages END) AS data_pages
                    FROM sys.partitions AS pa
                    JOIN sys.allocation_units AS au ON au.container_id = CASE WHEN au.type IN (1, 3) THEN pa.hobt_id ELSE pa.partition_id END
                    WHERE pa.object_id = o.object_id) AS a
                OUTER APPLY (SELECT MAX(STATS_DATE(sst.object_id, sst.stats_id)) AS last_update FROM sys.stats AS sst WHERE sst.object_id = o.object_id) AS st
                WHERE {where}
                ORDER BY s.name, o.name
                OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
                """;

            return await QueryAsync(
                connection,
                profile,
                db,
                sql,
                parameters,
                async reader =>
                {
                    var total = await ReadCountAsync(reader, cancellationToken).ConfigureAwait(false);
                    var items = new List<TableStatsInfo>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var itemSchema = reader.GetStringOrEmpty(0);
                        var itemName = reader.GetStringOrEmpty(1);
                        if (!IsVisible(profile, itemSchema, itemName))
                        {
                            continue;
                        }

                        var reserved = reader.GetLong(3) * 8;
                        var used = reader.GetLong(4) * 8;
                        var data = reader.GetLong(5) * 8;
                        items.Add(new TableStatsInfo(
                            itemSchema,
                            itemName,
                            reader.GetLongOrNull(2),
                            reserved,
                            used,
                            data,
                            Math.Max(0, used - data),
                            Math.Max(0, reserved - used),
                            reader.GetDateTimeOrNull(6)));
                    }

                    return MetadataPaging.Page(db, items, total, offset, limit);
                },
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Lee el primer conjunto (COUNT_BIG) y avanza al siguiente.</summary>
    private static async Task<long> ReadCountAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        long total = 0;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            total = reader.GetLong(0);
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    private static string? TemporalTypeName(int? temporalType) => temporalType switch
    {
        1 => "HISTORY",
        2 => "SYSTEM_VERSIONED",
        _ => null,
    };

    private static async Task<TableDescription> ReadTableDescriptionAsync(
        SqlDataReader reader,
        ProfileRuntime profile,
        string database,
        CatalogObject target,
        IReadOnlySet<string> classified,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken)
    {
        bool ColumnExcluded(string column) => profile.IsColumnExcluded(target.Schema, target.Name, column);

        // 1. Cabecera.
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw NotFound(target.Schema, target.Name);
        }

        var createDate = reader.GetDateTime(0);
        var modifyDate = reader.GetDateTime(1);
        var description = SafeText(profile, patterns, reader.GetStringOrNull(2));
        var temporalType = TemporalTypeName(reader.GetIntOrNull(3));
        var historySchema = reader.GetStringOrNull(4);
        var historyTable = reader.GetStringOrNull(5);
        var approximateRows = reader.GetLongOrNull(6);
        var periodStart = reader.GetStringOrNull(7);
        var periodEnd = reader.GetStringOrNull(8);
        if (historySchema is null || historyTable is null || !IsVisible(profile, historySchema, historyTable))
        {
            historySchema = null;
            historyTable = null;
        }

        TemporalInfo? temporal = temporalType is null
            ? null
            : new TemporalInfo(
                temporalType,
                periodStart is not null && !ColumnExcluded(periodStart) ? periodStart : null,
                periodEnd is not null && !ColumnExcluded(periodEnd) ? periodEnd : null,
                historySchema,
                historyTable);

        // 2. Columnas.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<ColumnDescription>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var columnName = reader.GetStringOrEmpty(1);
            if (ColumnExcluded(columnName))
            {
                continue;
            }

            var typeName = reader.GetStringOrEmpty(2);
            var isUserDefined = reader.GetBool(4);
            var baseType = reader.GetStringOrNull(5);
            var maxLength = reader.GetInt(6);
            var precision = reader.GetInt(7);
            var scale = reader.GetInt(8);
            var (length, numericPrecision, numericScale) = CatalogQueries.Dimensions(isUserDefined ? baseType ?? typeName : typeName, maxLength, precision, scale);
            columns.Add(new ColumnDescription(
                reader.GetInt(0),
                columnName,
                CatalogQueries.FormatDataType(typeName, reader.GetStringOrNull(3), isUserDefined, maxLength, precision, scale),
                isUserDefined ? CatalogQueries.FormatBaseType(baseType, maxLength, precision, scale) : null,
                length,
                numericPrecision,
                numericScale,
                reader.GetBool(9),
                reader.GetStringOrNull(10),
                ExclusionText.Sanitize(patterns, reader.GetStringOrNull(11)),
                reader.GetBool(12),
                CatalogSql.ParseLong(reader.GetStringOrNull(13)),
                CatalogSql.ParseLong(reader.GetStringOrNull(14)),
                reader.GetBool(15),
                ExclusionText.Sanitize(patterns, reader.GetStringOrNull(16)),
                reader.GetBool(17),
                reader.GetStringOrNull(18),
                reader.GetBool(19),
                reader.GetBool(20),
                reader.GetBool(21),
                reader.GetStringOrNull(22),
                reader.GetBool(23),
                SafeText(profile, patterns, reader.GetStringOrNull(24)),
                profile.IsColumnSensitive(target.Schema, target.Name, columnName) || classified.Contains(columnName),
                profile.IsColumnPii(target.Schema, target.Name, columnName)));
        }

        // 3. Clave primaria y restricciones UNIQUE.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<(string Name, string Type, bool Clustered, List<string> Columns)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var keyName = reader.GetStringOrEmpty(0);
            if (keys.Count == 0 || keys[^1].Name != keyName)
            {
                keys.Add((keyName, reader.GetStringOrEmpty(1), reader.GetBool(2), []));
            }

            keys[^1].Columns.Add(reader.GetStringOrEmpty(4));
        }

        var visibleKeys = keys.Where(k => !k.Columns.Any(ColumnExcluded) && !ExclusionText.Reveals(patterns, k.Name)).ToList();
        var primaryKey = visibleKeys.Where(k => k.Type == "PK").Select(k => new KeyConstraintInfo(k.Name, k.Columns, k.Clustered)).FirstOrDefault();
        var uniqueConstraints = visibleKeys.Where(k => k.Type == "UQ").Select(k => new KeyConstraintInfo(k.Name, k.Columns, k.Clustered)).ToList();

        // 4. Claves foráneas salientes.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var foreignKeys = new List<ForeignKeyInfo>();
        FkBuilder? currentFk = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fkId = reader.GetInt(10);
            if (currentFk is null || currentFk.Id != fkId)
            {
                if (currentFk?.ToForeignKey(profile, patterns) is { } completed)
                {
                    foreignKeys.Add(completed);
                }

                currentFk = new FkBuilder(
                    fkId,
                    reader.GetStringOrEmpty(0),
                    target.Schema,
                    target.Name,
                    reader.GetStringOrEmpty(5),
                    reader.GetStringOrEmpty(6),
                    reader.GetStringOrEmpty(1),
                    reader.GetStringOrEmpty(2),
                    reader.GetBool(3),
                    !reader.GetBool(4));
            }

            currentFk.FromColumns.Add(reader.GetStringOrEmpty(8));
            currentFk.ToColumns.Add(reader.GetStringOrEmpty(9));
        }

        if (currentFk?.ToForeignKey(profile, patterns) is { } last)
        {
            foreignKeys.Add(last);
        }

        // 5. Restricciones CHECK.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var checks = new List<CheckConstraintInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var checkName = reader.GetStringOrEmpty(0);
            var definition = reader.GetStringOrEmpty(1);
            var column = reader.GetStringOrNull(2);
            if ((column is not null && ColumnExcluded(column)) || ExclusionText.Reveals(patterns, definition) || ExclusionText.Reveals(patterns, checkName))
            {
                continue;
            }

            checks.Add(new CheckConstraintInfo(checkName, definition, column, reader.GetBool(3), !reader.GetBool(4)));
        }

        // 6 y 7. Índices.
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(reader, profile, target, patterns, cancellationToken).ConfigureAwait(false);

        return new TableDescription(
            database,
            target.Schema,
            target.Name,
            target.IsView ? "VIEW" : "TABLE",
            description,
            approximateRows,
            createDate,
            modifyDate,
            columns,
            primaryKey,
            foreignKeys,
            uniqueConstraints,
            checks,
            indexes,
            temporal);
    }

    /// <summary>Lee los conjuntos de <see cref="CatalogQueries.Indexes"/> (cabeceras y columnas).</summary>
    private static async Task<List<IndexInfo>> ReadIndexesAsync(
        SqlDataReader reader,
        ProfileRuntime profile,
        CatalogObject target,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken)
    {
        var headers = new List<(int Id, string Name, string TypeDesc, int Type, bool Unique, bool PrimaryKey, bool UniqueConstraint, string? Filter, bool Disabled, int FillFactor, long? SizeKb)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            headers.Add((
                reader.GetInt(0),
                reader.GetStringOrEmpty(1),
                reader.GetStringOrEmpty(2),
                reader.GetInt(3),
                reader.GetBool(4),
                reader.GetBool(5),
                reader.GetBool(6),
                reader.GetStringOrNull(7),
                reader.GetBool(8),
                reader.GetInt(9),
                reader.GetLongOrNull(10)));
        }

        var columns = new Dictionary<int, List<(int KeyOrdinal, int ColumnOrdinal, string Name, bool Descending, bool Included)>>();
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var indexId = reader.GetInt(0);
                if (!columns.TryGetValue(indexId, out var list))
                {
                    list = [];
                    columns[indexId] = list;
                }

                list.Add((reader.GetInt(1), reader.GetInt(2), reader.GetStringOrEmpty(3), reader.GetBool(4), reader.GetBool(5)));
            }
        }

        var result = new List<IndexInfo>();
        foreach (var header in headers)
        {
            var indexColumns = columns.TryGetValue(header.Id, out var found) ? found : [];
            if (indexColumns.Any(c => profile.IsColumnExcluded(target.Schema, target.Name, c.Name))
                || ExclusionText.Reveals(patterns, header.Filter)
                || ExclusionText.Reveals(patterns, header.Name))
            {
                continue;
            }

            var isColumnstore = header.Type is 5 or 6;
            var keyColumns = indexColumns
                .Where(c => c.KeyOrdinal > 0 || (isColumnstore && !c.Included))
                .OrderBy(c => c.KeyOrdinal > 0 ? c.KeyOrdinal : c.ColumnOrdinal)
                .Select((c, i) => new IndexKeyColumn(c.Name, i + 1, c.Descending ? "DESC" : "ASC"))
                .ToList();
            var included = indexColumns.Where(c => c.Included).OrderBy(c => c.ColumnOrdinal).Select(c => c.Name).ToList();
            result.Add(new IndexInfo(
                header.Name,
                header.TypeDesc,
                header.Unique,
                header.PrimaryKey,
                header.UniqueConstraint,
                keyColumns,
                included,
                header.Filter,
                header.Disabled,
                header.FillFactor,
                header.SizeKb));
        }

        return result;
    }

    /// <summary>Acumula las columnas de una clave foránea del grafo y la añade si es visible.</summary>
    private sealed class FkBuilder(
        int id,
        string name,
        string fromSchema,
        string fromTable,
        string toSchema,
        string toTable,
        string onDelete,
        string onUpdate,
        bool isDisabled,
        bool isTrusted)
    {
        public int Id { get; } = id;

        public List<string> FromColumns { get; } = [];

        public List<string> ToColumns { get; } = [];

        /// <summary>Indica si la clave y todas sus columnas son visibles en el perfil (RF-MET-06).</summary>
        public bool IsVisibleIn(ProfileRuntime profile, IReadOnlyList<string> patterns) =>
            IsVisible(profile, fromSchema, fromTable)
            && IsVisible(profile, toSchema, toTable)
            && !FromColumns.Any(c => profile.IsColumnExcluded(fromSchema, fromTable, c))
            && !ToColumns.Any(c => profile.IsColumnExcluded(toSchema, toTable, c))
            && !ExclusionText.Reveals(patterns, name);

        /// <summary>Clave foránea para describe_table, o null si no es visible.</summary>
        public ForeignKeyInfo? ToForeignKey(ProfileRuntime profile, IReadOnlyList<string> patterns) =>
            IsVisibleIn(profile, patterns)
                ? new ForeignKeyInfo(name, FromColumns, toSchema, toTable, ToColumns, onDelete, onUpdate, isDisabled, isTrusted)
                : null;

        /// <summary>Añade la arista si sus extremos y columnas son visibles; false si se alcanzó el máximo.</summary>
        public bool Add(List<RelationshipEdge> edges, ProfileRuntime profile, IReadOnlyList<string> patterns)
        {
            if (!IsVisibleIn(profile, patterns))
            {
                return true;
            }

            if (edges.Count >= MaxRelationshipEdges)
            {
                return false;
            }

            edges.Add(new RelationshipEdge(name, fromSchema, fromTable, FromColumns, toSchema, toTable, ToColumns, onDelete, onUpdate, isDisabled, isTrusted));
            return true;
        }
    }
}
