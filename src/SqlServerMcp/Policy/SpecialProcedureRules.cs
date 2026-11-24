using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Procedimientos del sistema que execute_ddl admite como DDL (RS-POL-04, RF-DDL-04): sys.sp_rename
/// (modificación destructiva, CONFIRMAR) y sys.sp_add/update/dropextendedproperty (PERMITIR). Sus argumentos deben
/// ser literales y referirse a objetos de un esquema autorizado para DDL; nunca a la base de datos completa.
/// </summary>
internal static class SpecialProcedureRules
{
    private static readonly string[] RenameParameters = ["@objname", "@newname", "@objtype"];

    private static readonly string[] AddOrUpdatePropertyParameters =
        ["@name", "@value", "@level0type", "@level0name", "@level1type", "@level1name", "@level2type", "@level2name"];

    private static readonly string[] DropPropertyParameters =
        ["@name", "@level0type", "@level0name", "@level1type", "@level1name", "@level2type", "@level2name"];

    private static readonly string[] ChildObjectTypes = ["COLUMN", "INDEX", "STATISTICS"];

    /// <summary>sp_rename: el objeto debe ser un literal "esquema.objeto[.hijo]" en un esquema de DDL (RF-DDL-04).</summary>
    public static void CheckRename(AnalysisContext ctx, StatementUnit unit, ExecutableProcedureReference procedure)
    {
        var args = Bind(procedure.Parameters, RenameParameters);
        if (args is null)
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, "sp_rename con parámetros no reconocidos");
            return;
        }

        var objectName = Literal(args, "@objname");
        if (objectName is null || Literal(args, "@newname") is null)
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, "sp_rename requiere @objname y @newname como literales");
            return;
        }

        var objectType = Literal(args, "@objtype")?.Trim().ToUpperInvariant();
        if (args.ContainsKey("@objtype") && objectType is null)
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, "sp_rename requiere @objtype como literal");
            return;
        }

        if (objectType == "DATABASE")
        {
            ctx.Hits.Add(PolicyRules.AdminDatabase, "sp_rename de una base de datos");
            return;
        }

        if (objectType is not (null or "OBJECT" or "COLUMN" or "INDEX" or "STATISTICS" or "USERDATATYPE"))
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, $"tipo de objeto '{objectType}' no admitido en sp_rename");
            return;
        }

        var isChild = objectType is not null && ChildObjectTypes.Contains(objectType, StringComparer.Ordinal);
        var parts = SqlNames.SplitMultipart(objectName, isChild ? 4 : 3);
        var minimum = isChild ? 3 : 2;
        if (parts is null || parts.Count < minimum)
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, $"@objname '{objectName}' debe indicar el esquema (por ejemplo ia.Tabla{(isChild ? ".Columna" : string.Empty)})");
            return;
        }

        var offset = isChild ? 1 : 0;
        var schema = PolicyNames.Clean(parts[^(2 + offset)]);
        var name = PolicyNames.Clean(parts[^(1 + offset)]);
        var database = parts.Count > minimum ? PolicyNames.Clean(parts[0]) : null;
        var target = new SqlObjectName(null, string.IsNullOrEmpty(database) ? null : database, schema, name);

        unit.Class = unit.Class with { ObjectType = objectType ?? "OBJECT" };
        ctx.AddObject(unit, target, ObjectRole.DdlTarget, objectType ?? "OBJECT");
        unit.RegisterTarget(target);
        ReferenceRules.CheckDdlTarget(ctx, target);

        if (objectType == "COLUMN")
        {
            var column = PolicyNames.Clean(parts[^1]);
            var schemaName = PolicyNames.EffectiveSchema(schema);
            ctx.AddColumn(unit, new SqlColumnReference(column, schemaName, name, false, null));
            if (ReferenceRules.IsColumnExcluded(ctx.Profile, schemaName, name, column))
            {
                ctx.Hits.Add(PolicyRules.ExcludedColumn, $"'{schemaName}.{name}.{column}'");
            }
            else if (ReferenceRules.IsColumnSensitive(ctx.Profile, schemaName, name, column))
            {
                // Renombrar una columna sensible evitaría su enmascaramiento por nombre.
                ctx.Hits.Add(PolicyRules.SensitiveColumnInference, $"renombrar la columna sensible '{schemaName}.{name}.{column}'");
            }
        }
    }

    /// <summary>Propiedades extendidas: @level0type = 'SCHEMA' literal con un esquema de DDL (nunca a nivel de base de datos).</summary>
    public static void CheckExtendedProperty(AnalysisContext ctx, StatementUnit unit, ExecutableProcedureReference procedure, SqlObjectName procedureName)
    {
        var isDrop = PolicyNames.Key(procedureName.Name) == "SP_DROPEXTENDEDPROPERTY";
        var args = Bind(procedure.Parameters, isDrop ? DropPropertyParameters : AddOrUpdatePropertyParameters);
        if (args is null)
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, $"{procedureName.Name} con parámetros no reconocidos");
            return;
        }

        foreach (var (parameter, value) in args)
        {
            if (parameter != "@value" && value is not (StringLiteral or NullLiteral))
            {
                ctx.Hits.Add(PolicyRules.DdlArguments, $"{parameter} debe ser un literal");
                return;
            }
        }

        var level0Type = Literal(args, "@level0type")?.Trim();
        var level0Name = Literal(args, "@level0name")?.Trim();
        if (!PolicyNames.EqualsIgnoreCase(level0Type, "SCHEMA") || string.IsNullOrEmpty(level0Name))
        {
            ctx.Hits.Add(PolicyRules.DdlArguments, "las propiedades extendidas deben indicar @level0type = N'SCHEMA' y un @level0name literal");
            return;
        }

        if (!ReferenceRules.CheckDdlSchema(ctx, level0Name))
        {
            return;
        }

        var level1Name = Literal(args, "@level1name")?.Trim();
        var target = string.IsNullOrEmpty(level1Name)
            ? new SqlObjectName(null, null, null, level0Name)
            : new SqlObjectName(null, null, level0Name, level1Name);
        ctx.AddObject(unit, target, ObjectRole.DdlTarget, "EXTENDED PROPERTY");
        unit.RegisterTarget(target);

        if (string.IsNullOrEmpty(level1Name))
        {
            return;
        }

        if (ReferenceRules.IsExcludedObject(ctx.Profile, level0Name, level1Name))
        {
            ctx.Hits.Add(PolicyRules.ExcludedObject, $"'{level0Name}.{level1Name}'");
            return;
        }

        var level2Type = Literal(args, "@level2type")?.Trim();
        var level2Name = Literal(args, "@level2name")?.Trim();
        if (PolicyNames.EqualsIgnoreCase(level2Type, "COLUMN") && !string.IsNullOrEmpty(level2Name)
            && ReferenceRules.IsColumnExcluded(ctx.Profile, level0Name, level1Name, level2Name))
        {
            ctx.Hits.Add(PolicyRules.ExcludedColumn, $"'{level0Name}.{level1Name}.{level2Name}'");
        }
    }

    /// <summary>
    /// Asocia los argumentos posicionales y con nombre a los parámetros del procedimiento. Devuelve null si hay
    /// parámetros desconocidos, repetidos o un posicional después de uno con nombre (fail-closed).
    /// </summary>
    private static Dictionary<string, ScalarExpression?>? Bind(IList<ExecuteParameter> parameters, string[] names)
    {
        var bound = new Dictionary<string, ScalarExpression?>(StringComparer.OrdinalIgnoreCase);
        var namedSeen = false;
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            string key;
            if (parameter.Variable is null)
            {
                if (namedSeen || i >= names.Length)
                {
                    return null;
                }

                key = names[i];
            }
            else
            {
                namedSeen = true;
                key = names.FirstOrDefault(n => PolicyNames.EqualsIgnoreCase(n, parameter.Variable.Name)) ?? string.Empty;
                if (key.Length == 0)
                {
                    return null;
                }
            }

            if (parameter.IsOutput || !bound.TryAdd(key, parameter.ParameterValue))
            {
                return null;
            }
        }

        return bound;
    }

    private static string? Literal(Dictionary<string, ScalarExpression?> args, string name) =>
        args.TryGetValue(name, out var value) && value is StringLiteral literal ? literal.Value : null;
}
