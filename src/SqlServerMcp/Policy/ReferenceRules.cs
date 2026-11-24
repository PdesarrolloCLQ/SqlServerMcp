using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Validación de referencias a objetos (RS-POL-07, RS-POL-08, RS-DAT-01, RF-DDL-04): servidores vinculados,
/// bases de datos permitidas y del sistema, catálogo sensible, esquemas permitidos y objetos excluidos.
/// </summary>
/// <remarks>
/// Las listas de permitidos se comparan con el nombre tal como se escribió (sin plegar acentos ni anchos) y las
/// listas de denegación o exclusión también con sus variantes plegadas: plegar solo amplía lo que se bloquea.
/// </remarks>
internal static class ReferenceRules
{
    public static bool IsCatalogSchema(string? schema) =>
        PolicyNames.EqualsIgnoreCase(schema, "sys") || PolicyNames.EqualsIgnoreCase(schema, "INFORMATION_SCHEMA");

    /// <summary>
    /// Esquema efectivo de una referencia: el indicado, sys para las vistas de compatibilidad y las funciones
    /// fn_* sin esquema (que SQL Server resuelve en sys), o dbo.
    /// </summary>
    public static string EffectiveSchema(SqlObjectName name, bool isFunction)
    {
        if (!string.IsNullOrEmpty(name.Schema))
        {
            return name.Schema;
        }

        if (Denylist.IsLegacyCompatibilityView(name.Name))
        {
            return "sys";
        }

        if (isFunction && name.Name.StartsWith("fn_", StringComparison.OrdinalIgnoreCase))
        {
            return "sys";
        }

        return PolicyNames.DefaultSchema;
    }

    /// <summary>Valida una referencia de lectura, escritura, función u otra (no destino DDL).</summary>
    /// <returns>False si la referencia se bloqueó.</returns>
    public static bool CheckObject(AnalysisContext ctx, SqlObjectName name, ObjectRole role, bool isFunction = false)
    {
        var profile = ctx.Profile;
        var hits = ctx.Hits;
        var detail = $"'{PolicyNames.Display(name)}'";

        if (name.Server is not null)
        {
            hits.Add(PolicyRules.ExternalLinkedServer, detail);
            return false;
        }

        if (PolicyNames.IsGlobalTemp(name.Name))
        {
            hits.Add(PolicyRules.SystemDatabase, detail);
            return false;
        }

        if (PolicyNames.IsLocalTemp(name.Name))
        {
            return true;
        }

        // El catálogo sensible se bloquea en cualquier base de datos (incluso master.sys.sql_logins).
        if (Denylist.IsSensitiveCatalogObject(name.Schema, name.Name))
        {
            hits.Add(PolicyRules.SensitiveCatalog, detail);
            return false;
        }

        if (!CheckDatabase(ctx, name, detail))
        {
            return false;
        }

        var schema = EffectiveSchema(name, isFunction);
        if (IsCatalogSchema(schema))
        {
            if ((role is ObjectRole.Read or ObjectRole.Function or ObjectRole.Other) && profile.Policy.AllowSystemCatalogQueries)
            {
                return true;
            }

            hits.Add(PolicyRules.SystemCatalog, detail);
            return false;
        }

        if (IsExcludedObject(profile, schema, name.Name))
        {
            hits.Add(PolicyRules.ExcludedObject, detail);
            return false;
        }

        if (!IsSchemaAllowed(profile, schema))
        {
            hits.Add(PolicyRules.SchemaNotAllowed, $"esquema '{schema}' en {detail}");
            return false;
        }

        return true;
    }

    /// <summary>Valida el destino de una sentencia DDL (RF-DDL-04): esquema explícito y autorizado para DDL, objeto no excluido.</summary>
    public static bool CheckDdlTarget(AnalysisContext ctx, SqlObjectName name)
    {
        var profile = ctx.Profile;
        var hits = ctx.Hits;
        var detail = $"'{PolicyNames.Display(name)}'";

        if (name.Server is not null)
        {
            hits.Add(PolicyRules.ExternalLinkedServer, detail);
            return false;
        }

        if (PolicyNames.IsGlobalTemp(name.Name))
        {
            hits.Add(PolicyRules.SystemDatabase, detail);
            return false;
        }

        if (PolicyNames.IsLocalTemp(name.Name))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(name.Database) && !PolicyNames.EqualsIgnoreCase(name.Database, profile.InitialDatabase))
        {
            if (Denylist.IsMsdb(name.Database))
            {
                hits.Add(PolicyRules.ExternalMailJobs, detail);
            }
            else if (ProfileRuntime.IsSystemDatabase(name.Database))
            {
                hits.Add(PolicyRules.SystemDatabase, detail);
            }
            else
            {
                hits.Add(PolicyRules.DdlSchemaNotAllowed, $"el DDL solo se admite en la base de datos inicial del perfil ({detail})");
            }

            return false;
        }

        if (string.IsNullOrEmpty(name.Schema))
        {
            hits.Add(PolicyRules.DdlNoSchema, detail);
            return false;
        }

        if (!CheckDdlSchema(ctx, name.Schema, detail))
        {
            return false;
        }

        if (IsExcludedObject(profile, name.Schema, name.Name))
        {
            hits.Add(PolicyRules.ExcludedObject, detail);
            return false;
        }

        return true;
    }

    /// <summary>Esquema autorizado para DDL (CREATE/DROP SCHEMA, ALTER SCHEMA, propiedades extendidas, destinos DDL).</summary>
    public static bool CheckDdlSchema(AnalysisContext ctx, string schema, string? detail = null)
    {
        var profile = ctx.Profile;
        var hits = ctx.Hits;
        detail ??= $"esquema '{schema}'";
        if (IsCatalogSchema(schema) || PolicyNames.EqualsIgnoreCase(schema, "guest"))
        {
            hits.Add(PolicyRules.SystemCatalog, detail);
            return false;
        }

        if (PolicyNames.Variants(schema).Any(profile.IsSchemaExcluded))
        {
            hits.Add(PolicyRules.ExcludedObject, detail);
            return false;
        }

        if (!profile.IsDdlSchemaAllowed(schema))
        {
            hits.Add(PolicyRules.DdlSchemaNotAllowed, detail);
            return false;
        }

        return true;
    }

    /// <summary>Base de datos de una referencia de tres partes: permitida, no del sistema y nunca msdb.</summary>
    public static bool CheckDatabase(AnalysisContext ctx, SqlObjectName name, string detail)
    {
        if (string.IsNullOrEmpty(name.Database))
        {
            return true;
        }

        if (Denylist.IsMsdb(name.Database))
        {
            ctx.Hits.Add(PolicyRules.ExternalMailJobs, detail);
            return false;
        }

        if (ProfileRuntime.IsSystemDatabase(name.Database))
        {
            ctx.Hits.Add(PolicyRules.SystemDatabase, detail);
            return false;
        }

        if (!ctx.Profile.AllowedDatabases.Contains(name.Database))
        {
            ctx.Hits.Add(PolicyRules.DatabaseNotAllowed, detail);
            return false;
        }

        return true;
    }

    public static bool IsSchemaAllowed(ProfileRuntime profile, string schema) =>
        profile.IsSchemaAllowed(schema) && !PolicyNames.Variants(schema).Any(profile.IsSchemaExcluded);

    /// <summary>Objeto excluido, comparando también la variante sin diacríticos.</summary>
    public static bool IsExcludedObject(ProfileRuntime profile, string schema, string name)
    {
        foreach (var s in PolicyNames.Variants(schema))
        {
            foreach (var n in PolicyNames.Variants(name))
            {
                if (profile.IsObjectExcluded(s, n))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsColumnExcluded(ProfileRuntime profile, string? schema, string? table, string column) =>
        AnyVariant(profile.IsColumnExcluded, schema, table, column);

    public static bool IsColumnSensitive(ProfileRuntime profile, string? schema, string? table, string column) =>
        AnyVariant(profile.IsColumnSensitive, schema, table, column);

    public static bool IsColumnPii(ProfileRuntime profile, string? schema, string? table, string column) =>
        AnyVariant(profile.IsColumnPii, schema, table, column);

    private static bool AnyVariant(Func<string?, string?, string, bool> check, string? schema, string? table, string column)
    {
        foreach (var s in NullableVariants(schema))
        {
            foreach (var t in NullableVariants(table))
            {
                foreach (var c in PolicyNames.Variants(column))
                {
                    if (check(s, t, c))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static IEnumerable<string?> NullableVariants(string? value)
    {
        if (value is null)
        {
            yield return null;
            yield break;
        }

        foreach (var v in PolicyNames.Variants(value))
        {
            yield return v;
        }
    }
}

/// <summary>
/// Destino de CREATE SYNONYM: se le aplican las mismas reglas que si se usara directamente y, además, solo se admiten
/// objetos de usuario dentro del alcance del perfil (POL-SYN-001). El tipo real del objeto (tabla, vista, procedimiento o
/// función) se comprueba en tiempo de ejecución contra el catálogo.
/// </summary>
internal static class SynonymRules
{
    public static bool CheckTarget(AnalysisContext ctx, SqlObjectName name)
    {
        var hits = ctx.Hits;
        var detail = $"destino '{PolicyNames.Display(name)}'";

        if (name.Server is not null)
        {
            hits.Add(PolicyRules.ExternalLinkedServer, detail);
            return false;
        }

        // Mismas reglas que al usar el objeto directamente: sp_executesql → POL-DYN-001, xp_cmdshell → POL-EXT-003…
        if (Denylist.ProcedureRule(name.Name) is { } denied)
        {
            hits.Add(denied, detail);
            return false;
        }

        if (Denylist.IsSensitiveCatalogObject(name.Schema, name.Name))
        {
            hits.Add(PolicyRules.SensitiveCatalog, detail);
            return false;
        }

        if (IsResourceDatabase(name.Database) || !ReferenceRules.CheckDatabase(ctx, name, detail))
        {
            if (IsResourceDatabase(name.Database))
            {
                hits.Add(PolicyRules.SynonymTarget, detail);
            }

            return false;
        }

        if (Denylist.HasSystemProcedurePrefix(name.Name)
            || name.Name.StartsWith("fn_", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(name.Schema)
            || ReferenceRules.IsCatalogSchema(ReferenceRules.EffectiveSchema(name, isFunction: false))
            || Denylist.IsLegacyCompatibilityView(name.Name) && string.IsNullOrEmpty(name.Schema))
        {
            hits.Add(PolicyRules.SynonymTarget, $"{detail} es un objeto del sistema");
            return false;
        }

        return ReferenceRules.CheckObject(ctx, name, ObjectRole.Other);
    }

    private static bool IsResourceDatabase(string? database) =>
        database is not null && PolicyNames.EqualsIgnoreCase(database, "mssqlsystemresource");
}

/// <summary>Reglas de ejecución de procedimientos (RF-PRC-02, RF-PRC-03, sección 5.4, Anexo C).</summary>
internal static class ProcedureRules
{
    /// <summary>
    /// Evalúa un procedimiento. Registra en el contexto las coincidencias que bloquean y devuelve la regla de
    /// decisión (permitir o confirmar) cuando el procedimiento es ejecutable; null si quedó bloqueado.
    /// </summary>
    public static PolicyRule? Check(AnalysisContext ctx, SqlObjectName name, out string detail)
    {
        var profile = ctx.Profile;
        var hits = ctx.Hits;
        detail = $"'{PolicyNames.Display(name)}'";

        if (name.Server is not null)
        {
            hits.Add(PolicyRules.ExternalLinkedServer, detail);
            return null;
        }

        if (Denylist.ProcedureRule(name.Name) is { } denied)
        {
            hits.Add(denied, detail);
            return null;
        }

        if (!ReferenceRules.CheckDatabase(ctx, name, detail))
        {
            return null;
        }

        if (Denylist.HasSystemProcedurePrefix(name.Name))
        {
            var systemSchema = string.IsNullOrEmpty(name.Schema)
                || PolicyNames.EqualsIgnoreCase(name.Schema, "sys")
                || PolicyNames.EqualsIgnoreCase(name.Schema, PolicyNames.DefaultSchema);
            if (systemSchema && Denylist.IsReadOnlySystemProcedure(name.Name))
            {
                return PolicyRules.ProcedureSystemReadOnly;
            }

            hits.Add(PolicyRules.ProcedureSystem, detail);
            return null;
        }

        var schema = PolicyNames.EffectiveSchema(name.Schema);
        if (ReferenceRules.IsCatalogSchema(schema))
        {
            hits.Add(PolicyRules.ProcedureSystem, detail);
            return null;
        }

        if (ReferenceRules.IsExcludedObject(profile, schema, name.Name))
        {
            hits.Add(PolicyRules.ExcludedObject, detail);
            return null;
        }

        if (!ReferenceRules.IsSchemaAllowed(profile, schema))
        {
            hits.Add(PolicyRules.SchemaNotAllowed, $"esquema '{schema}' en {detail}");
            return null;
        }

        // Denegados: también con variantes plegadas (amplía el bloqueo). Permitidos: solo el nombre exacto.
        if (profile.Config.DeniedProcedures.Any(pattern => Matches(pattern, schema, name.Name, includeFoldedVariants: true)))
        {
            hits.Add(PolicyRules.ProcedureDenied, detail);
            return null;
        }

        if (profile.Config.Procedures.Count > 0)
        {
            var entries = profile.Config.Procedures.Where(e => Matches(e.Name, schema, name.Name, includeFoldedVariants: false)).ToList();
            if (entries.Count == 0)
            {
                hits.Add(PolicyRules.ProcedureNotAllowed, detail);
                return null;
            }

            return entries.Any(e => e.Confirm) ? PolicyRules.ProcedureConfirm : PolicyRules.ProcedureAllowed;
        }

        return PolicyRules.ProcedureAllowed;
    }

    /// <summary>Patrón "esquema.procedimiento" o "procedimiento" (cualquier esquema), con comodines.</summary>
    public static bool Matches(string pattern, string schema, string name, bool includeFoldedVariants)
    {
        var parts = SqlNames.SplitMultipart(pattern, 2);
        if (parts is null)
        {
            return false;
        }

        IEnumerable<string> names = includeFoldedVariants ? PolicyNames.Variants(name) : [name];
        IEnumerable<string> schemas = includeFoldedVariants ? PolicyNames.Variants(schema) : [schema];
        foreach (var n in names)
        {
            if (parts.Count == 1)
            {
                if (SqlNames.WildcardMatch(parts[0], n))
                {
                    return true;
                }

                continue;
            }

            foreach (var s in schemas)
            {
                if (SqlNames.WildcardMatch(parts[0], s) && SqlNames.WildcardMatch(parts[1], n))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
