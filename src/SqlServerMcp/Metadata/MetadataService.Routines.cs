using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Metadata;

/// <summary>Rutinas, definiciones, dependencias y búsqueda (RF-MET-04, 05, 09, 12).</summary>
public sealed partial class MetadataService
{
    private const int SnippetRadius = 60;

    private const string DescribeRoutineSql = $"""
        SELECT o.create_date, o.modify_date, CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit),
               CONVERT(nvarchar(max), ep.value), m.definition
        FROM sys.objects AS o
        LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
        LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE o.object_id = @id AND o.name = @name;

        SELECT p.parameter_id, p.name, t.name, SCHEMA_NAME(t.schema_id), t.is_user_defined, {CatalogQueries.BaseTypeExpression},
               p.max_length, p.precision, p.scale, p.is_output, p.has_default_value, p.is_readonly, t.is_table_type
        FROM sys.parameters AS p
        JOIN sys.types AS t ON t.user_type_id = p.user_type_id
        WHERE p.object_id = @id
        ORDER BY p.parameter_id;

        SELECT c.column_id, c.name, t.name, SCHEMA_NAME(t.schema_id), t.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = @id
        ORDER BY c.column_id;
        """;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <inheritdoc />
    /// <remarks>RF-MET-04, RF-MET-06: procedimientos (P, PC), funciones escalares (FN, FS) y de tabla (IF, TF, FT).</remarks>
    public Task<MetadataPage<RoutineSummary>> ListRoutinesAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string? namePattern,
        RoutineTypeFilter type,
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

        var typeList = type switch
        {
            RoutineTypeFilter.Procedure => "'P', 'PC'",
            RoutineTypeFilter.ScalarFunction => "'FN', 'FS'",
            RoutineTypeFilter.TableFunction => "'IF', 'TF', 'FT'",
            _ => "'P', 'PC', 'FN', 'FS', 'IF', 'TF', 'FT'",
        };

        var parameters = new CatalogParameters();
        var filter = new VisibilityFilter(profile, parameters);
        var where = new StringBuilder("o.is_ms_shipped = 0 AND o.type IN (").Append(typeList).Append(") AND ").Append(filter.ObjectVisible("s.name", "o.name"));
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
            SELECT s.name, o.name, RTRIM(o.type), o.type_desc, CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit),
                   o.create_date, o.modify_date, CONVERT(nvarchar(max), ep.value)
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
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
                var items = new List<RoutineSummary>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var itemSchema = reader.GetStringOrEmpty(0);
                    var itemName = reader.GetStringOrEmpty(1);
                    var kind = CatalogQueries.RoutineKind(reader.GetStringOrEmpty(2));
                    if (kind is null || !IsVisible(profile, itemSchema, itemName))
                    {
                        continue;
                    }

                    items.Add(new RoutineSummary(
                        itemSchema,
                        itemName,
                        kind,
                        reader.GetStringOrEmpty(3),
                        reader.GetBool(4),
                        reader.GetDateTime(5),
                        reader.GetDateTime(6),
                        SafeText(profile, patterns, reader.GetStringOrNull(7))));
                }

                return MetadataPaging.Page(db, items, total, offset, limit);
            },
            cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-04: parámetros (nombre con "@", tipo, dirección, valor por defecto obtenido de la definición porque el
    /// catálogo no lo expone para T-SQL), tipo de retorno de funciones escalares (parameter_id 0) y columnas de las
    /// funciones de tabla (sys.columns de la función, sin columnas excluidas).
    /// </remarks>
    public Task<RoutineDescription> DescribeRoutineAsync(ProfileRuntime profile, string? database, string? schema, string name, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var (objectSchema, objectName) = MetadataArguments.ResolveObjectName(schema, name);
        var patterns = ExclusionText.TextPatterns(profile);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var target = await FindVisibleAsync(connection, profile, db, objectSchema, objectName, cancellationToken).ConfigureAwait(false);
            var kind = CatalogQueries.RoutineKind(target.TypeCode);
            if (kind is null)
            {
                throw new ToolFailureException(
                    ErrorCodes.NotFound,
                    $"El objeto '{target.QualifiedName}' no es un procedimiento ni una función.",
                    "Use describe_table para tablas y vistas.");
            }

            return await QueryAsync(
                connection,
                profile,
                db,
                DescribeRoutineSql,
                ObjectParameters(target),
                async reader =>
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw NotFound(target.Schema, target.Name);
                    }

                    var createDate = reader.GetDateTime(0);
                    var modifyDate = reader.GetDateTime(1);
                    var encrypted = reader.GetBool(2);
                    var description = SafeText(profile, patterns, reader.GetStringOrNull(3));
                    var defaults = RoutineParameterDefaults.Extract(reader.GetStringOrNull(4));

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    var parameters = new List<RoutineParameterInfo>();
                    string? returnType = null;
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var ordinal = reader.GetInt(0);
                        var dataType = CatalogQueries.FormatDataType(
                            reader.GetStringOrEmpty(2), reader.GetStringOrNull(3), reader.GetBool(4), reader.GetInt(6), reader.GetInt(7), reader.GetInt(8));
                        if (ordinal == 0)
                        {
                            returnType = dataType;
                            continue;
                        }

                        var parameterName = reader.GetStringOrEmpty(1);
                        var defaultValue = defaults.TryGetValue(parameterName, out var text) ? ExclusionText.Sanitize(patterns, text) : null;
                        parameters.Add(new RoutineParameterInfo(
                            ordinal,
                            parameterName,
                            dataType,
                            reader.GetBool(9) ? "OUT" : "IN",
                            reader.GetBool(10) || defaults.ContainsKey(parameterName),
                            defaultValue,
                            reader.GetBool(11),
                            reader.GetBool(12)));
                    }

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    var columns = new List<ResultColumnInfo>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var columnName = reader.GetStringOrEmpty(1);
                        if (profile.IsColumnExcluded(target.Schema, target.Name, columnName))
                        {
                            continue;
                        }

                        columns.Add(new ResultColumnInfo(
                            reader.GetInt(0),
                            columnName,
                            CatalogQueries.FormatDataType(reader.GetStringOrEmpty(2), reader.GetStringOrNull(3), reader.GetBool(4), reader.GetInt(5), reader.GetInt(6), reader.GetInt(7)),
                            reader.GetBool(8)));
                    }

                    return new RoutineDescription(
                        db,
                        target.Schema,
                        target.Name,
                        kind,
                        target.TypeDescription,
                        parameters,
                        kind == "scalar_function" ? returnType : null,
                        kind == "table_function" ? columns : [],
                        encrypted,
                        createDate,
                        modifyDate,
                        description);
                },
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-05: vistas, procedimientos, funciones y triggers (sys.sql_modules). Los módulos cifrados devuelven
    /// IsEncrypted sin definición. RF-MET-06: si el módulo referencia objetos excluidos (sys.sql_expression_dependencies)
    /// o su texto menciona esquemas, tablas o columnas excluidos, la definición no se devuelve.
    /// </remarks>
    public Task<ObjectDefinitionResult> GetObjectDefinitionAsync(ProfileRuntime profile, string? database, string? schema, string name, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var (objectSchema, objectName) = MetadataArguments.ResolveObjectName(schema, name);
        var patterns = ExclusionText.TextPatterns(profile);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var target = await FindVisibleAsync(connection, profile, db, objectSchema, objectName, cancellationToken).ConfigureAwait(false);
            if (target.IsTable)
            {
                throw new ToolFailureException(
                    ErrorCodes.NotFound,
                    $"'{target.QualifiedName}' es una tabla y no tiene definición de código.",
                    "Use describe_table para obtener su estructura.");
            }

            var parameters = ObjectParameters(target);
            var filter = new VisibilityFilter(profile, parameters);
            var sql = $"""
                SELECT m.definition, CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit),
                       CASE WHEN m.object_id IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN {filter.ReferencesExcluded("o.object_id")} THEN 1 ELSE 0 END,
                       ps.name, po.name
                FROM sys.objects AS o
                LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
                LEFT JOIN sys.objects AS po ON po.object_id = o.parent_object_id AND o.parent_object_id <> 0
                LEFT JOIN sys.schemas AS ps ON ps.schema_id = po.schema_id
                WHERE o.object_id = @id AND o.name = @name;
                """;

            return await QueryAsync(
                connection,
                profile,
                db,
                sql,
                parameters,
                async reader =>
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw NotFound(target.Schema, target.Name);
                    }

                    var definition = reader.GetStringOrNull(0);
                    var encrypted = reader.GetBool(1);
                    var isModule = reader.GetBool(2);
                    var referencesExcluded = reader.GetBool(3);
                    var parentSchema = reader.GetStringOrNull(4);
                    var parentName = reader.GetStringOrNull(5);
                    if (parentSchema is not null && parentName is not null && !IsVisible(profile, parentSchema, parentName))
                    {
                        throw NotFound(target.Schema, target.Name);
                    }

                    if (!isModule && !encrypted)
                    {
                        throw new ToolFailureException(
                            ErrorCodes.NotFound,
                            $"El objeto '{target.QualifiedName}' ({target.TypeDescription}) no tiene definición de código.",
                            "get_object_definition admite vistas, procedimientos, funciones y triggers.");
                    }

                    if (encrypted || definition is null)
                    {
                        return new ObjectDefinitionResult(db, target.Schema, target.Name, target.TypeDescription, null, true, false, false,
                            "El módulo está cifrado (WITH ENCRYPTION); SQL Server no permite obtener su definición.");
                    }

                    if (referencesExcluded || ExclusionText.Reveals(patterns, definition))
                    {
                        return new ObjectDefinitionResult(db, target.Schema, target.Name, target.TypeDescription, null, false, true, false,
                            "La definición no se muestra porque hace referencia a esquemas, tablas o columnas excluidos por la configuración del perfil.");
                    }

                    var maxChars = Math.Max(4096, profile.Limits.MaxResponseBytes / 2);
                    var truncated = definition.Length > maxChars;
                    return new ObjectDefinitionResult(db, target.Schema, target.Name, target.TypeDescription,
                        truncated ? definition[..maxChars] : definition, false, false, truncated, null);
                },
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-12 (sys.sql_expression_dependencies, con referenced_database_name para referencias entre bases).
    /// RF-MET-06: solo se devuelven objetos visibles (base permitida, esquema permitido, objeto no excluido); las
    /// referencias a servidores vinculados o a bases no permitidas se omiten.
    /// </remarks>
    public Task<DependencyResult> GetDependenciesAsync(
        ProfileRuntime profile,
        string? database,
        string? schema,
        string name,
        DependencyDirection direction,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var (objectSchema, objectName) = MetadataArguments.ResolveObjectName(schema, name);

        return RunAsync(profile, db, correlationId, async connection =>
        {
            var target = await FindVisibleAsync(connection, profile, db, objectSchema, objectName, cancellationToken).ConfigureAwait(false);
            var parameters = new CatalogParameters();
            parameters.Int("@id", target.ObjectId);
            parameters.Int("@class", target.TypeCode is "TY" or "TT" ? 6 : 1);
            parameters.NVarChar("@name", target.Name);
            parameters.NVarChar("@schema", target.Schema);
            const string sql = """
                SELECT s.name, o.name, o.type_desc, CAST(MAX(CAST(d.is_schema_bound_reference AS int)) AS bit)
                FROM sys.sql_expression_dependencies AS d
                JOIN sys.objects AS o ON o.object_id = d.referencing_id
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE d.referencing_id <> @id
                  AND ((d.referenced_id = @id AND d.referenced_class = @class)
                    OR (d.referenced_id IS NULL AND d.referenced_class = @class AND d.referenced_server_name IS NULL
                        AND d.referenced_database_name IS NULL AND d.referenced_entity_name = @name
                        AND (d.referenced_schema_name IS NULL OR d.referenced_schema_name = @schema)))
                GROUP BY s.name, o.name, o.type_desc
                ORDER BY s.name, o.name;

                SELECT d.referenced_server_name, d.referenced_database_name,
                       COALESCE(rs.name, rts.name, d.referenced_schema_name),
                       COALESCE(ro.name, rt.name, d.referenced_entity_name),
                       COALESCE(ro.type_desc, CASE WHEN rt.user_type_id IS NULL THEN NULL WHEN rt.is_table_type = 1 THEN N'TABLE_TYPE' ELSE N'USER_DEFINED_TYPE' END),
                       d.is_schema_bound_reference,
                       CASE WHEN ro.object_id IS NOT NULL OR rt.user_type_id IS NOT NULL THEN 1 ELSE 0 END
                FROM sys.sql_expression_dependencies AS d
                LEFT JOIN sys.objects AS ro ON ro.object_id = d.referenced_id AND d.referenced_class = 1 AND d.referenced_database_name IS NULL
                LEFT JOIN sys.schemas AS rs ON rs.schema_id = ro.schema_id
                LEFT JOIN sys.types AS rt ON rt.user_type_id = d.referenced_id AND d.referenced_class = 6
                LEFT JOIN sys.schemas AS rts ON rts.schema_id = rt.schema_id
                WHERE d.referencing_id = @id AND d.referenced_class IN (1, 6)
                ORDER BY 2, 3, 4;
                """;

            return await QueryAsync(
                connection,
                profile,
                db,
                sql,
                parameters,
                async reader =>
                {
                    var referencedBy = new List<DependencyInfo>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var depSchema = reader.GetStringOrEmpty(0);
                        var depName = reader.GetStringOrEmpty(1);
                        if (IsVisible(profile, depSchema, depName))
                        {
                            referencedBy.Add(new DependencyInfo(null, depSchema, depName, reader.GetStringOrNull(2), reader.GetBool(3), true));
                        }
                    }

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    var references = new Dictionary<(string?, string?, string), DependencyInfo>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var server = reader.GetStringOrNull(0);
                        var refDatabase = reader.GetStringOrNull(1);
                        var refSchema = reader.GetStringOrNull(2);
                        var refName = reader.GetStringOrNull(3);
                        if (server is not null || refName is null)
                        {
                            continue;
                        }

                        if (refDatabase is not null && !profile.IsDatabaseAllowed(refDatabase))
                        {
                            continue;
                        }

                        var schemaForCheck = refSchema ?? target.Schema;
                        if (!IsVisible(profile, schemaForCheck, refName)
                            || (refDatabase is null && string.Equals(refName, target.Name, StringComparison.Ordinal) && string.Equals(schemaForCheck, target.Schema, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        var key = (refDatabase, refSchema, refName);
                        var info = new DependencyInfo(refDatabase, refSchema, refName, reader.GetStringOrNull(4), reader.GetBool(5), reader.GetBool(6));
                        references[key] = references.TryGetValue(key, out var existing)
                            ? existing with { IsSchemaBound = existing.IsSchemaBound || info.IsSchemaBound, IsResolved = existing.IsResolved || info.IsResolved }
                            : info;
                    }

                    return new DependencyResult(
                        db,
                        target.Schema,
                        target.Name,
                        target.TypeDescription,
                        direction is DependencyDirection.Both or DependencyDirection.Referencing ? referencedBy : null,
                        direction is DependencyDirection.Both or DependencyDirection.Referenced ? references.Values.ToList() : null);
                },
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-09, RF-MET-06, RNF-02: nombre con LIKE parametrizado y ESCAPE; definición con CHARINDEX parametrizado
    /// sobre sys.sql_modules (sin distinguir mayúsculas). Se omiten los objetos no visibles, los triggers de tablas no
    /// visibles y las definiciones que mencionan o referencian elementos excluidos.
    /// </remarks>
    public Task<MetadataPage<ObjectSearchMatch>> SearchObjectsAsync(
        ProfileRuntime profile,
        string? database,
        string text,
        SearchScope searchIn,
        SearchObjectType type,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var searchText = MetadataArguments.RequireSearchText(text);
        (offset, limit) = MetadataPaging.Normalize(offset, limit);
        var patterns = ExclusionText.TextPatterns(profile);
        var searchName = searchIn is SearchScope.Name or SearchScope.Both;
        var searchDefinition = searchIn is SearchScope.Definition or SearchScope.Both;

        var typeList = type switch
        {
            SearchObjectType.Table => "'U'",
            SearchObjectType.View => "'V'",
            SearchObjectType.Procedure => "'P', 'PC'",
            SearchObjectType.Function => "'FN', 'FS', 'IF', 'TF', 'FT'",
            SearchObjectType.Trigger => "'TR'",
            SearchObjectType.Sequence => "'SO'",
            SearchObjectType.Synonym => "'SN'",
            _ => "'U', 'V', 'P', 'PC', 'FN', 'FS', 'IF', 'TF', 'FT', 'TR', 'SO', 'SN'",
        };

        var parameters = new CatalogParameters();
        var filter = new VisibilityFilter(profile, parameters);
        var namePattern = parameters.NVarChar("@namePattern", LikePatterns.FromWildcard(searchText.Trim(), containsWhenNoWildcards: true), 2048);
        var textParameter = parameters.NVarChar("@text", searchText, MetadataArguments.MaxSearchTextLength);
        parameters.Int("@offset", offset);
        parameters.Int("@limit", limit);

        var nameCondition = searchName ? $"o.name{Ci} LIKE {namePattern} ESCAPE N'\\'" : "1 = 0";
        var definitionCondition = searchDefinition
            ? $"m.definition IS NOT NULL AND {filter.TextClean("m.definition")} AND NOT {filter.ReferencesExcluded("o.object_id")}"
            : "1 = 0";
        var core = $"""
            SELECT o.object_id, s.name AS schema_name, o.name AS object_name, o.type_desc, ps.name AS parent_schema, po.name AS parent_name,
                   CASE WHEN {nameCondition} THEN 1 ELSE 0 END AS name_match, x.pos
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.objects AS po ON po.object_id = o.parent_object_id AND o.parent_object_id <> 0
            LEFT JOIN sys.schemas AS ps ON ps.schema_id = po.schema_id
            LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
            OUTER APPLY (SELECT CASE WHEN {definitionCondition} THEN CHARINDEX({textParameter}, m.definition{Ci}) ELSE 0 END AS pos) AS x
            WHERE o.is_ms_shipped = 0 AND o.type IN ({typeList})
              AND {filter.ObjectVisible("s.name", "o.name")}
              AND (po.object_id IS NULL OR {filter.ObjectVisible("ps.name", "po.name")})
              AND (({nameCondition}) OR x.pos > 0)
            """;
        var sql = $"""
            WITH q AS ({core}) SELECT COUNT_BIG(*) FROM q;
            WITH q AS ({core})
            SELECT q.schema_name, q.object_name, q.type_desc, q.parent_schema, q.parent_name, q.name_match, q.pos,
                   CASE WHEN q.pos > 0 THEN SUBSTRING(m.definition,
                        CASE WHEN q.pos > {SnippetRadius} THEN q.pos - {SnippetRadius} ELSE 1 END,
                        LEN({textParameter}) + CASE WHEN q.pos > {SnippetRadius} THEN {SnippetRadius} ELSE q.pos - 1 END + {SnippetRadius}) END
            FROM q
            LEFT JOIN sys.sql_modules AS m ON m.object_id = q.object_id
            ORDER BY q.name_match DESC, q.schema_name, q.object_name
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
                var items = new List<ObjectSearchMatch>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var itemSchema = reader.GetStringOrEmpty(0);
                    var itemName = reader.GetStringOrEmpty(1);
                    var parentSchema = reader.GetStringOrNull(3);
                    var parentName = reader.GetStringOrNull(4);
                    if (!IsVisible(profile, itemSchema, itemName)
                        || (parentSchema is not null && parentName is not null && !IsVisible(profile, parentSchema, parentName)))
                    {
                        continue;
                    }

                    var nameMatch = reader.GetBool(5);
                    var position = reader.GetInt(6);
                    var snippet = position > 0 ? FormatSnippet(reader.GetStringOrNull(7), position > SnippetRadius + 1) : null;
                    if (snippet is not null && ExclusionText.Reveals(patterns, snippet))
                    {
                        snippet = null;
                    }

                    var definitionMatch = snippet is not null;
                    if (!nameMatch && !definitionMatch)
                    {
                        continue;
                    }

                    items.Add(new ObjectSearchMatch(
                        itemSchema,
                        itemName,
                        reader.GetStringOrEmpty(2),
                        nameMatch && definitionMatch ? "both" : nameMatch ? "name" : "definition",
                        snippet,
                        parentSchema is null || parentName is null ? null : parentSchema + "." + parentName));
                }

                return MetadataPaging.Page(db, items, total, offset, limit);
            },
            cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// RF-MET-10, RF-MET-06: triggers DML (tabla padre visible), secuencias, sinónimos (se marca el destino fuera de las
    /// bases permitidas o con nombre de cuatro partes; se omiten los que apuntan a objetos excluidos) y tipos de usuario
    /// (alias, tabla y CLR). Se pagina en memoria: estos catálogos son pequeños.
    /// </remarks>
    public Task<MetadataPage<CatalogObjectInfo>> ListObjectsAsync(
        ProfileRuntime profile,
        string? database,
        CatalogObjectKind kind,
        string? schema,
        int offset,
        int limit,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var db = MetadataArguments.ResolveDatabase(profile, database);
        var schemaFilter = MetadataArguments.OptionalIdentifier(schema, "schema");
        (offset, limit) = MetadataPaging.Normalize(offset, limit);
        var patterns = ExclusionText.TextPatterns(profile);

        var parameters = new CatalogParameters();
        var filter = new VisibilityFilter(profile, parameters);
        var schemaCondition = schemaFilter is null ? string.Empty : $" AND s.name{Ci} = {parameters.NVarChar("@schemaFilter", schemaFilter)}";
        var sql = kind switch
        {
            CatalogObjectKind.Trigger => $"""
                SELECT s.name, tr.name, po.name, po.type_desc, tr.is_disabled, tr.is_instead_of_trigger,
                       CAST(ISNULL(OBJECTPROPERTY(tr.object_id, 'IsEncrypted'), 0) AS bit),
                       CAST(ISNULL(OBJECTPROPERTY(tr.object_id, 'ExecIsInsertTrigger'), 0) AS bit),
                       CAST(ISNULL(OBJECTPROPERTY(tr.object_id, 'ExecIsUpdateTrigger'), 0) AS bit),
                       CAST(ISNULL(OBJECTPROPERTY(tr.object_id, 'ExecIsDeleteTrigger'), 0) AS bit)
                FROM sys.triggers AS tr
                JOIN sys.objects AS po ON po.object_id = tr.parent_id
                JOIN sys.schemas AS s ON s.schema_id = po.schema_id
                WHERE tr.parent_class = 1 AND tr.is_ms_shipped = 0
                  AND {filter.ObjectVisible("s.name", "po.name")} AND NOT {filter.ObjectExcluded("s.name", "tr.name")}{schemaCondition}
                ORDER BY s.name, tr.name;
                """,
            CatalogObjectKind.Sequence => $"""
                SELECT s.name, sq.name, t.name, SCHEMA_NAME(t.schema_id), t.is_user_defined, sq.precision, sq.scale,
                       CONVERT(nvarchar(64), sq.start_value), CONVERT(nvarchar(64), sq.increment),
                       CONVERT(nvarchar(64), sq.minimum_value), CONVERT(nvarchar(64), sq.maximum_value),
                       CONVERT(nvarchar(64), sq.current_value), sq.is_cycling
                FROM sys.sequences AS sq
                JOIN sys.schemas AS s ON s.schema_id = sq.schema_id
                JOIN sys.types AS t ON t.user_type_id = sq.user_type_id
                WHERE {filter.ObjectVisible("s.name", "sq.name")}{schemaCondition}
                ORDER BY s.name, sq.name;
                """,
            CatalogObjectKind.Synonym => $"""
                SELECT s.name, sn.name, sn.base_object_name
                FROM sys.synonyms AS sn
                JOIN sys.schemas AS s ON s.schema_id = sn.schema_id
                WHERE {filter.ObjectVisible("s.name", "sn.name")} AND {filter.TextClean("sn.base_object_name")}{schemaCondition}
                ORDER BY s.name, sn.name;
                """,
            _ => $"""
                SELECT s.name, t.name, t.is_table_type, t.is_assembly_type, TYPE_NAME(t.system_type_id), t.max_length, t.precision, t.scale,
                       t.is_nullable, t.user_type_id
                FROM sys.types AS t
                JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE t.is_user_defined = 1 AND {filter.ObjectVisible("s.name", "t.name")}{schemaCondition}
                ORDER BY s.name, t.name;

                SELECT tt.user_type_id, c.column_id, c.name, ct.name, SCHEMA_NAME(ct.schema_id), ct.is_user_defined,
                       c.max_length, c.precision, c.scale, c.is_nullable
                FROM sys.table_types AS tt
                JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
                JOIN sys.columns AS c ON c.object_id = tt.type_table_object_id
                JOIN sys.types AS ct ON ct.user_type_id = c.user_type_id
                WHERE {filter.ObjectVisible("s.name", "tt.name")}{schemaCondition}
                ORDER BY tt.user_type_id, c.column_id;
                """,
        };

        return RunAsync(profile, db, correlationId, connection => QueryAsync(
            connection,
            profile,
            db,
            sql,
            parameters,
            async reader =>
            {
                var all = kind switch
                {
                    CatalogObjectKind.Trigger => await ReadTriggersAsync(reader, profile, patterns, cancellationToken).ConfigureAwait(false),
                    CatalogObjectKind.Sequence => await ReadSequencesAsync(reader, profile, cancellationToken).ConfigureAwait(false),
                    CatalogObjectKind.Synonym => await ReadSynonymsAsync(reader, profile, patterns, cancellationToken).ConfigureAwait(false),
                    _ => await ReadUserTypesAsync(reader, profile, cancellationToken).ConfigureAwait(false),
                };
                return MetadataPaging.PageInMemory(db, all, offset, limit);
            },
            cancellationToken), cancellationToken);
    }

    /// <summary>Normaliza un fragmento de definición: espacios colapsados y puntos suspensivos.</summary>
    internal static string? FormatSnippet(string? raw, bool truncatedAtStart)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        string collapsed;
        try
        {
            collapsed = Whitespace.Replace(raw, " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            collapsed = raw.Trim();
        }

        return (truncatedAtStart ? "…" : string.Empty) + collapsed + "…";
    }

    private static async Task<List<CatalogObjectInfo>> ReadTriggersAsync(SqlDataReader reader, ProfileRuntime profile, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        var list = new List<CatalogObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetStringOrEmpty(0);
            var name = reader.GetStringOrEmpty(1);
            var parent = reader.GetStringOrEmpty(2);
            if (!IsVisible(profile, schema, parent) || profile.IsObjectExcluded(schema, name) || ExclusionText.Reveals(patterns, name))
            {
                continue;
            }

            var events = new List<string>();
            if (reader.GetBool(7))
            {
                events.Add("INSERT");
            }

            if (reader.GetBool(8))
            {
                events.Add("UPDATE");
            }

            if (reader.GetBool(9))
            {
                events.Add("DELETE");
            }

            list.Add(new CatalogObjectInfo(
                schema,
                name,
                "trigger",
                new TriggerDetails(schema, parent, reader.GetStringOrEmpty(3), events, reader.GetBool(4), reader.GetBool(5), reader.GetBool(6)),
                null,
                null,
                null));
        }

        return list;
    }

    private static async Task<List<CatalogObjectInfo>> ReadSequencesAsync(SqlDataReader reader, ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var list = new List<CatalogObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetStringOrEmpty(0);
            var name = reader.GetStringOrEmpty(1);
            if (!IsVisible(profile, schema, name))
            {
                continue;
            }

            var dataType = CatalogQueries.FormatDataType(reader.GetStringOrEmpty(2), reader.GetStringOrNull(3), reader.GetBool(4), 0, reader.GetInt(5), reader.GetInt(6));
            list.Add(new CatalogObjectInfo(
                schema,
                name,
                "sequence",
                null,
                new SequenceDetails(dataType, reader.GetStringOrNull(7), reader.GetStringOrNull(8), reader.GetStringOrNull(9), reader.GetStringOrNull(10), reader.GetStringOrNull(11), reader.GetBool(12)),
                null,
                null));
        }

        return list;
    }

    private static async Task<List<CatalogObjectInfo>> ReadSynonymsAsync(SqlDataReader reader, ProfileRuntime profile, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        var list = new List<CatalogObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetStringOrEmpty(0);
            var name = reader.GetStringOrEmpty(1);
            var baseObject = reader.GetStringOrEmpty(2);
            if (!IsVisible(profile, schema, name))
            {
                continue;
            }

            var details = DescribeSynonymTarget(profile, schema, baseObject, patterns);
            if (details is null)
            {
                continue;
            }

            list.Add(new CatalogObjectInfo(schema, name, "synonym", null, null, details, null));
        }

        return list;
    }

    /// <summary>
    /// Clasifica el destino de un sinónimo; devuelve null si apunta a un objeto excluido (no debe revelarse, RF-MET-06).
    /// </summary>
    internal static SynonymDetails? DescribeSynonymTarget(ProfileRuntime profile, string synonymSchema, string baseObjectName, IReadOnlyList<string> patterns)
    {
        if (ExclusionText.Reveals(patterns, baseObjectName))
        {
            return null;
        }

        var parts = SqlNames.SplitMultipart(baseObjectName, 4);
        if (parts is null)
        {
            return new SynonymDetails(baseObjectName, true, false, false);
        }

        var isFourPart = parts.Count == 4;
        var targetDatabase = parts.Count >= 3 ? parts[^3] : null;
        var targetSchema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : synonymSchema;
        var targetName = parts[^1];
        if (profile.IsObjectExcluded(targetSchema, targetName))
        {
            return null;
        }

        var outside = isFourPart || (!string.IsNullOrEmpty(targetDatabase) && !profile.IsDatabaseAllowed(targetDatabase));
        return new SynonymDetails(baseObjectName, outside, isFourPart, !outside && profile.IsSchemaAllowed(targetSchema));
    }

    private static async Task<List<CatalogObjectInfo>> ReadUserTypesAsync(SqlDataReader reader, ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var types = new List<(string Schema, string Name, bool IsTable, bool IsClr, string? BaseType, bool Nullable, int Id)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetStringOrEmpty(0);
            var name = reader.GetStringOrEmpty(1);
            if (!IsVisible(profile, schema, name))
            {
                continue;
            }

            var isTable = reader.GetBool(2);
            var isClr = reader.GetBool(3);
            var baseType = isTable || isClr ? null : CatalogQueries.FormatBaseType(reader.GetStringOrNull(4), reader.GetInt(5), reader.GetInt(6), reader.GetInt(7));
            types.Add((schema, name, isTable, isClr, baseType, reader.GetBool(8), reader.GetInt(9)));
        }

        var columns = new Dictionary<int, List<ResultColumnInfo>>();
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            var names = types.ToDictionary(t => t.Id);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var typeId = reader.GetInt(0);
                var columnName = reader.GetStringOrEmpty(2);
                if (!names.TryGetValue(typeId, out var owner) || profile.IsColumnExcluded(owner.Schema, owner.Name, columnName))
                {
                    continue;
                }

                if (!columns.TryGetValue(typeId, out var list))
                {
                    list = [];
                    columns[typeId] = list;
                }

                list.Add(new ResultColumnInfo(
                    reader.GetInt(1),
                    columnName,
                    CatalogQueries.FormatDataType(reader.GetStringOrEmpty(3), reader.GetStringOrNull(4), reader.GetBool(5), reader.GetInt(6), reader.GetInt(7), reader.GetInt(8)),
                    reader.GetBool(9)));
            }
        }

        return types.Select(t => new CatalogObjectInfo(
            t.Schema,
            t.Name,
            "type",
            null,
            null,
            null,
            new UserTypeDetails(
                t.IsTable ? "table" : t.IsClr ? "clr" : "alias",
                t.BaseType,
                t.Nullable,
                columns.TryGetValue(t.Id, out var cols) ? cols : []))).ToList();
    }
}
