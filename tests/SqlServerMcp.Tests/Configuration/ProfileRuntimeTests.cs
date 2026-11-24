using SqlServerMcp.Configuration;

namespace SqlServerMcp.Tests.Configuration;

public sealed class ProfileRuntimeTests
{
    private static ProfileRuntime Create()
    {
        var config = new ProfileConfig
        {
            Mode = OperationMode.EscrituraDatos,
            Connection = new ConnectionConfig { Server = "s", Database = "VentasDev" },
            AllowedDatabases = ["VentasDev", "Historico"],
            AllowedSchemas = ["ventas", "ia"],
            DdlSchemas = ["ia"],
            Excluded = new ExclusionConfig { Schemas = ["seguridad"], Tables = ["ventas.TarjetasCredito", "ventas.tmp_*"], Columns = ["*.Contrasena", "ventas.Clientes.Notas"] },
        };
        return new ProfileRuntime("dev", config, config.Limits);
    }

    [Fact]
    public void BasesDeDatos_SoloPermitidasYNuncaDelSistema()
    {
        var p = Create();
        Assert.True(p.IsDatabaseAllowed("ventasdev"));
        Assert.True(p.IsDatabaseAllowed("Historico"));
        Assert.False(p.IsDatabaseAllowed("OtraBD"));
        Assert.False(p.IsDatabaseAllowed("master"));
        Assert.True(p.IsDatabaseAllowed(null));
    }

    [Fact]
    public void Esquemas_DenegarPorDefecto()
    {
        var p = Create();
        Assert.True(p.IsSchemaAllowed("VENTAS"));
        Assert.False(p.IsSchemaAllowed("dbo"));
        Assert.False(p.IsSchemaAllowed(null));
        Assert.False(p.IsSchemaAllowed("seguridad"));
        Assert.True(p.IsDdlSchemaAllowed("ia"));
        Assert.False(p.IsDdlSchemaAllowed("ventas"));
    }

    [Fact]
    public void ObjetosExcluidos_PorNombreYComodin()
    {
        var p = Create();
        Assert.True(p.IsObjectExcluded("ventas", "TarjetasCredito"));
        Assert.True(p.IsObjectExcluded("ventas", "tmp_carga"));
        Assert.True(p.IsObjectExcluded("seguridad", "Cualquiera"));
        Assert.False(p.IsObjectExcluded("ventas", "Clientes"));
    }

    [Fact]
    public void ColumnasExcluidasYSensibles_CriterioConservador()
    {
        var p = Create();
        Assert.True(p.IsColumnExcluded("ventas", "Usuarios", "Contrasena"));
        Assert.True(p.IsColumnExcluded(null, null, "contrasena"));
        Assert.True(p.IsColumnExcluded("ventas", "Clientes", "Notas"));
        Assert.True(p.IsColumnExcluded(null, null, "Notas"));       // tabla desconocida: conservador
        Assert.False(p.IsColumnExcluded("ventas", "Facturas", "Notas"));
        Assert.True(p.IsColumnSensitive(null, null, "UserPassword"));
        Assert.True(p.IsColumnSensitive("ventas", "Pagos", "NumeroTarjeta"));
        Assert.False(p.IsColumnSensitive("ventas", "Clientes", "Nombre"));
    }

    [Fact]
    public void Modo_Jerarquico()
    {
        var p = Create();
        Assert.True(p.ModeAllows(OperationMode.Lectura));
        Assert.True(p.ModeAllows(OperationMode.EscrituraDatos));
        Assert.False(p.ModeAllows(OperationMode.Completo));
    }
}
