using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Policy;

/// <summary>
/// Normalización de nombres para las comparaciones del motor (RS-POL-06): sin delimitadores (ScriptDom ya
/// los quita), sin espacios finales (SQL Server los ignora al resolver identificadores), sin distinguir
/// mayúsculas y, como variante adicional, sin diacríticos (colaciones insensibles a acentos).
/// </summary>
internal static class PolicyNames
{
    public const string DefaultSchema = "dbo";

    /// <summary>Valor normalizado de un identificador ScriptDom (null si no existe).</summary>
    public static string? Value(Identifier? identifier) => identifier is null ? null : Clean(identifier.Value);

    /// <summary>Quita espacios finales y caracteres de control; conserva mayúsculas.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.TrimEnd();
    }

    /// <summary>Convierte un SchemaObjectName de ScriptDom en <see cref="SqlObjectName"/> normalizado.</summary>
    /// <remarks>Un esquema vacío ("db..obj") se conserva como cadena vacía para distinguirlo; use <see cref="EffectiveSchema"/>.</remarks>
    public static SqlObjectName From(SchemaObjectName name)
    {
        var server = Value(name.ServerIdentifier);
        var database = Value(name.DatabaseIdentifier);
        var schema = Value(name.SchemaIdentifier);
        var baseName = Value(name.BaseIdentifier) ?? string.Empty;
        return new SqlObjectName(
            string.IsNullOrEmpty(server) ? null : server,
            string.IsNullOrEmpty(database) ? null : database,
            schema,
            baseName);
    }

    /// <summary>Nombre de la tabla a la que pertenece un ChildObjectName ("esquema.tabla.hijo").</summary>
    public static SqlObjectName ParentOf(ChildObjectName name)
    {
        var server = Value(name.ServerIdentifier);
        var database = Value(name.DatabaseIdentifier);
        return new SqlObjectName(
            string.IsNullOrEmpty(server) ? null : server,
            string.IsNullOrEmpty(database) ? null : database,
            Value(name.SchemaIdentifier),
            Value(name.BaseIdentifier) ?? string.Empty);
    }

    /// <summary>Esquema efectivo: el indicado o dbo si se omitió ("obj" o "db..obj").</summary>
    public static string EffectiveSchema(string? schema) => string.IsNullOrEmpty(schema) ? DefaultSchema : schema;

    public static bool HasExplicitSchema(SqlObjectName name) => !string.IsNullOrEmpty(name.Schema);

    public static bool IsOnePart(SqlObjectName name) =>
        name.Server is null && name.Database is null && string.IsNullOrEmpty(name.Schema);

    public static bool IsLocalTemp(string name) => name.StartsWith('#') && !name.StartsWith("##", StringComparison.Ordinal);

    public static bool IsGlobalTemp(string name) => name.StartsWith("##", StringComparison.Ordinal);

    public static bool EqualsIgnoreCase(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Quita diacríticos ("Contraseña" → "Contrasena") y aplica la descomposición de compatibilidad (formas de ancho
    /// completo "ｘｐ_cmdshell" → "xp_cmdshell") para comparar como lo hacen las colaciones insensibles a acentos y
    /// a ancho (RS-POL-06). Solo se usa para ampliar coincidencias de listas de denegación y exclusión, nunca para
    /// ampliar listas de permitidos.
    /// </summary>
    public static string Fold(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var ascii = true;
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            return value;
        }

        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    /// <summary>Variantes de comparación de un nombre: el original y, si difiere, sin diacríticos.</summary>
    public static IEnumerable<string> Variants(string value)
    {
        yield return value;
        var folded = Fold(value);
        if (!string.Equals(folded, value, StringComparison.Ordinal))
        {
            yield return folded;
        }
    }

    /// <summary>Clave de comparación insensible a mayúsculas, espacios finales y acentos (para listas de denegación).</summary>
    public static string Key(string? value) => Fold(Clean(value)).ToUpperInvariant();

    /// <summary>Representación legible "esquema.objeto" (o con base de datos/servidor si existen) para mensajes.</summary>
    public static string Display(SqlObjectName name)
    {
        var sb = new StringBuilder();
        if (name.Server is not null)
        {
            sb.Append(name.Server).Append('.');
        }

        if (name.Database is not null)
        {
            sb.Append(name.Database).Append('.');
        }

        if (!string.IsNullOrEmpty(name.Schema))
        {
            sb.Append(name.Schema).Append('.');
        }
        else if (name.Database is not null)
        {
            sb.Append('.');
        }

        sb.Append(name.Name);
        return sb.ToString();
    }

    /// <summary>Nombre de función para <see cref="SqlStatementInfo.Functions"/>.</summary>
    public static string FunctionDisplay(string? database, string? schema, string name)
    {
        if (!string.IsNullOrEmpty(database))
        {
            return $"{database}.{schema}.{name}";
        }

        return string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";
    }
}
