using Microsoft.Data.SqlClient;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Tests.Metadata;

public class ExclusionFilterTests
{
    [Fact]
    public void TextPatterns_TomaLaParteMasEspecificaDeCadaExclusion()
    {
        var profile = MetadataTestProfiles.Create();
        var patterns = ExclusionText.TextPatterns(profile);

        Assert.Contains("seguridad", patterns);
        Assert.Contains("TarjetasCredito", patterns);
        Assert.Contains("temp", patterns); // "temp.*" → parte de esquema
        Assert.Contains("Auditoria*", patterns);
        Assert.Contains("Contrasena", patterns);
        Assert.Contains("Token", patterns);
        Assert.DoesNotContain("*", patterns);
    }

    [Theory]
    [InlineData("SELECT * FROM ventas.TarjetasCredito", true)]
    [InlineData("select contrasena from x", true)]
    [InlineData("SELECT * FROM [seguridad].[Claves]", true)]
    [InlineData("SELECT * FROM dbo.AuditoriaCambios", true)]
    [InlineData("SELECT ClienteId, Nombre FROM ventas.Clientes", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Reveals_DetectaElementosExcluidos(string? text, bool expected)
    {
        var profile = MetadataTestProfiles.Create();
        Assert.Equal(expected, ExclusionText.Reveals(profile, text));
    }

    [Fact]
    public void Sanitize_DevuelveNullSiRevela()
    {
        var patterns = ExclusionText.TextPatterns(MetadataTestProfiles.Create());
        Assert.Null(ExclusionText.Sanitize(patterns, "Tabla con Contrasena"));
        Assert.Equal("Clientes de la compañía", ExclusionText.Sanitize(patterns, "Clientes de la compañía"));
    }

    [Fact]
    public void VisibilityFilter_ElSqlSoloContieneParametros()
    {
        var profile = MetadataTestProfiles.Create();
        var parameters = new CatalogParameters();
        var filter = new VisibilityFilter(profile, parameters);

        var sql = string.Join(
            "\n",
            filter.ObjectVisible("s.name", "o.name"),
            filter.TextClean("m.definition"),
            filter.ReferencesExcluded("o.object_id"));

        // Ningún nombre de la configuración aparece en el texto SQL: todos viajan como parámetros (RS-INJ-01).
        Assert.DoesNotContain("ventas", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TarjetasCredito", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contrasena", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@f0", sql, StringComparison.Ordinal);
        Assert.Contains("ESCAPE N'\\'", sql, StringComparison.Ordinal);
        Assert.Contains("COLLATE Latin1_General_100_CI_AS", sql, StringComparison.Ordinal);

        using var command = new SqlCommand();
        parameters.CopyTo(command.Parameters);
        var values = command.Parameters.Cast<SqlParameter>().Select(p => p.Value?.ToString()).ToList();
        Assert.Contains("ventas", values);
        Assert.Contains("ia", values);
        Assert.DoesNotContain("seguridad", values.Take(2)); // los permitidos efectivos excluyen "seguridad"
        Assert.Contains("TarjetasCredito", values);
        Assert.Contains("%Contrasena%", values);
    }

    [Fact]
    public void VisibilityFilter_SinEsquemasVisibles_NoDevuelveNada()
    {
        var profile = MetadataTestProfiles.Create(c => c.AllowedSchemas = ["seguridad"]);
        var filter = new VisibilityFilter(profile, new CatalogParameters());
        Assert.True(filter.HasNoVisibleSchemas);
        Assert.Equal("(1 = 0)", filter.SchemaAllowed("s.name"));
    }

    [Fact]
    public void VisibilityFilter_SinExclusiones_PredicadosNeutros()
    {
        var profile = MetadataTestProfiles.Create(c => c.Excluded = new SqlServerMcp.Configuration.ExclusionConfig());
        var filter = new VisibilityFilter(profile, new CatalogParameters());
        Assert.Equal("(1 = 0)", filter.ObjectExcluded("s.name", "o.name"));
        Assert.Equal("(1 = 1)", filter.TextClean("m.definition"));
        Assert.Equal("(1 = 0)", filter.ReferencesExcluded("o.object_id"));
    }

    [Fact]
    public void CatalogParameters_DeclaraTiposParaSpExecutesql()
    {
        var parameters = new CatalogParameters();
        parameters.Int("@id", 5);
        parameters.NVarChar("@name", "x");
        parameters.NextNVarChar("y", 5000);
        parameters.Bit("@flag", true);
        Assert.Equal("@id int, @name nvarchar(128), @f0 nvarchar(max), @flag bit", parameters.Declarations);
        Assert.Equal(4, parameters.Count);
    }
}
