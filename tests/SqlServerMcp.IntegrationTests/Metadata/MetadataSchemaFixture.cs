using SqlServerMcp.Configuration;

namespace SqlServerMcp.IntegrationTests.Metadata;

/// <summary>
/// Crea en el contenedor de desarrollo el esquema "metaprueba" (VentasTest) con objetos de todos los tipos que cubre
/// el módulo de metadatos (tipos alias y tabla, tabla temporal versionada, columnas calculadas/dispersas/ROWGUIDCOL,
/// índices filtrados, triggers, secuencias, sinónimos, módulos cifrados, clasificación de sensibilidad) y el esquema
/// "metaprueba" en OtraBD para las consultas entre bases. Se elimina al terminar. Ninguna otra prueba usa estos esquemas.
/// </summary>
public sealed class MetadataSchemaFixture : IAsyncLifetime
{
    public const string Schema = "metaprueba";
    public const string OtherDatabase = "OtraBD";

    private const string SetupScript = """
        CREATE SCHEMA metaprueba;
        GO
        GRANT SELECT, VIEW DEFINITION ON SCHEMA::metaprueba TO rol_mcp_lectura;
        GO
        CREATE TYPE metaprueba.Telefono FROM nvarchar(20) NULL;
        GO
        CREATE TYPE metaprueba.ListaIds AS TABLE (Id int NOT NULL PRIMARY KEY, Contrasena nvarchar(10) NULL);
        GO
        CREATE TABLE metaprueba.Cuentas
        (
            CuentaId   int IDENTITY(10, 5) NOT NULL CONSTRAINT PK_Cuentas PRIMARY KEY NONCLUSTERED,
            ClienteId  int NOT NULL CONSTRAINT FK_Cuentas_Clientes REFERENCES ventas.Clientes (ClienteId) ON DELETE CASCADE,
            Telefono   metaprueba.Telefono NULL,
            Contrasena nvarchar(50) NOT NULL,
            Huella     AS (HASHBYTES('SHA2_256', Contrasena)) PERSISTED,
            Codigo     AS (CuentaId * 2),
            Notas      nvarchar(200) SPARSE NULL,
            Fila       uniqueidentifier ROWGUIDCOL NOT NULL CONSTRAINT DF_Cuentas_Fila DEFAULT (NEWSEQUENTIALID()),
            Version    rowversion,
            CONSTRAINT CK_Cuentas_Contrasena CHECK (LEN(Contrasena) >= 8),
            CONSTRAINT CK_Cuentas_Cliente CHECK (ClienteId > 0)
        );
        GO
        CREATE CLUSTERED INDEX CIX_Cuentas_Cliente ON metaprueba.Cuentas (ClienteId DESC);
        CREATE INDEX IX_Cuentas_Clave ON metaprueba.Cuentas (Contrasena);
        CREATE INDEX IX_Cuentas_Telefono ON metaprueba.Cuentas (Telefono) INCLUDE (Notas) WHERE Telefono IS NOT NULL;
        GO
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Teléfono de contacto',
             @level0type = N'SCHEMA', @level0name = N'metaprueba', @level1type = N'TABLE', @level1name = N'Cuentas',
             @level2type = N'COLUMN', @level2name = N'Telefono';
        GO
        ADD SENSITIVITY CLASSIFICATION TO metaprueba.Cuentas.Notas WITH (LABEL = 'Confidential', INFORMATION_TYPE = 'Other');
        GO
        CREATE TABLE metaprueba.Oculta (OcultaId int NOT NULL PRIMARY KEY, Dato nvarchar(20) NULL);
        GO
        CREATE TABLE metaprueba.PagosTarjeta
        (
            PagoId    int NOT NULL CONSTRAINT PK_PagosTarjeta PRIMARY KEY,
            TarjetaId int NOT NULL CONSTRAINT FK_PagosTarjeta_Tarjetas REFERENCES ventas.TarjetasCredito (TarjetaId),
            OcultaId  int NULL CONSTRAINT FK_PagosTarjeta_Oculta REFERENCES metaprueba.Oculta (OcultaId),
            Importe   decimal(18, 2) NOT NULL
        );
        GO
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Pagos asociados a ventas.TarjetasCredito',
             @level0type = N'SCHEMA', @level0name = N'metaprueba', @level1type = N'TABLE', @level1name = N'PagosTarjeta';
        GO
        CREATE TABLE metaprueba.Precios
        (
            PrecioId    int NOT NULL CONSTRAINT PK_Precios PRIMARY KEY,
            Importe     decimal(10, 2) NOT NULL,
            ValidoDesde datetime2(0) GENERATED ALWAYS AS ROW START NOT NULL,
            ValidoHasta datetime2(0) GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidoDesde, ValidoHasta)
        )
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = metaprueba.PreciosHistorial));
        GO
        CREATE VIEW metaprueba.vCuentas AS SELECT CuentaId, ClienteId, Telefono FROM metaprueba.Cuentas;
        GO
        CREATE VIEW metaprueba.vUsuariosClave AS
        SELECT UsuarioId, Login, CASE WHEN Contrasena IS NULL THEN 0 ELSE 1 END AS TieneClave FROM ventas.Usuarios;
        GO
        CREATE VIEW metaprueba.vPagos AS SELECT p.PagoId, p.Importe FROM metaprueba.PagosTarjeta AS p JOIN metaprueba.Oculta AS o ON o.OcultaId = p.OcultaId;
        GO
        CREATE VIEW metaprueba.vEnlazada WITH SCHEMABINDING AS SELECT c.CuentaId, c.ClienteId FROM metaprueba.Cuentas AS c;
        GO
        CREATE PROCEDURE metaprueba.usp_ConDefectos
            @Minimo int = 5,
            @Texto nvarchar(20) = N'a,b' OUTPUT,
            @Fecha datetime2(3) = NULL,
            @Sin int,
            @Lista metaprueba.ListaIds READONLY
        AS
        BEGIN
            SET NOCOUNT ON;
            SELECT @Minimo AS Minimo, @Texto AS Texto, @Fecha AS Fecha, @Sin AS Sin, (SELECT COUNT(*) FROM @Lista) AS Total;
        END;
        GO
        CREATE PROCEDURE metaprueba.usp_Cifrado WITH ENCRYPTION AS SELECT 1 AS Uno;
        GO
        CREATE PROCEDURE metaprueba.usp_BuscarMarcador AS SELECT N'MarcadorBusquedaXYZ' AS Marca FROM metaprueba.vCuentas;
        GO
        CREATE FUNCTION metaprueba.fn_FacturasCliente (@ClienteId int)
        RETURNS TABLE
        AS RETURN (SELECT FacturaId, Fecha, Total FROM ventas.Facturas WHERE ClienteId = @ClienteId);
        GO
        CREATE FUNCTION metaprueba.fn_Multi (@n int = 3)
        RETURNS @t TABLE (Valor int NOT NULL, Contrasena nvarchar(10) NULL)
        AS
        BEGIN
            INSERT @t (Valor, Contrasena) VALUES (@n, NULL);
            RETURN;
        END;
        GO
        CREATE TRIGGER metaprueba.trg_Cuentas_Auditoria ON metaprueba.Cuentas AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END;
        GO
        CREATE TRIGGER metaprueba.trg_Oculta ON metaprueba.Oculta INSTEAD OF DELETE AS BEGIN SET NOCOUNT ON; END;
        GO
        CREATE SEQUENCE metaprueba.SecFacturas AS bigint START WITH 1000 INCREMENT BY 10 MINVALUE 1000 NO MAXVALUE NO CYCLE;
        GO
        CREATE SYNONYM metaprueba.snClientes FOR ventas.Clientes;
        CREATE SYNONYM metaprueba.snOtra FOR OtraBD.dbo.Algo;
        CREATE SYNONYM metaprueba.snRemoto FOR ServidorX.BaseX.dbo.TablaX;
        CREATE SYNONYM metaprueba.snTarjetas FOR ventas.TarjetasCredito;
        CREATE SYNONYM metaprueba.snOculta FOR metaprueba.Oculta;
        GO
        """;

    private const string OtherDatabaseScript = """
        CREATE SCHEMA metaprueba;
        GO
        CREATE TABLE metaprueba.Remota
        (
            RemotaId   int NOT NULL CONSTRAINT PK_Remota PRIMARY KEY,
            Valor      nvarchar(20) NOT NULL,
            Contrasena nvarchar(50) NULL
        );
        GO
        """;

    public bool Ready { get; private set; }

    /// <summary>Perfil (login lector) que permite el esquema "metaprueba" y excluye además "metaprueba.Oculta".</summary>
    public ProfileRuntime CreateProfile() =>
        MetadataIntegrationSupport.CreateProfileWithSchema(Schema, "metadatos-metaprueba", "metaprueba.Oculta");

    /// <summary>
    /// Perfil con el login administrador del contenedor y dos bases permitidas, solo para comprobar las consultas
    /// entre bases (el login lector no tiene usuario en OtraBD).
    /// </summary>
    public ProfileRuntime CreateCrossDatabaseProfile() => IntegrationEnvironment.CreateProfile(
        OperationMode.Lectura,
        c =>
        {
            c.Connection.UserName = "sa";
            c.Connection.CredentialRef = "env:SQLSERVERMCP_TEST_SA_PASSWORD";
            c.WriteConnection = null;
            c.AllowedDatabases = [IntegrationEnvironment.Database, OtherDatabase];
            c.AllowedSchemas = ["ventas", Schema];
            c.Excluded = new ExclusionConfig
            {
                Schemas = ["seguridad"],
                Tables = ["ventas.TarjetasCredito"],
                Columns = ["*.Contrasena"],
            };
        },
        "metadatos-entre-bases");

    public ValueTask InitializeAsync()
    {
        if (!IntegrationEnvironment.IsAvailable)
        {
            return ValueTask.CompletedTask;
        }

        MetadataIntegrationSupport.DropSchema(Schema);
        MetadataIntegrationSupport.DropSchema(Schema, OtherDatabase);
        MetadataIntegrationSupport.ExecuteAdminScript(SetupScript);
        MetadataIntegrationSupport.ExecuteAdminScript(OtherDatabaseScript, OtherDatabase);
        Ready = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Ready)
        {
            MetadataIntegrationSupport.DropSchema(Schema);
            MetadataIntegrationSupport.DropSchema(Schema, OtherDatabase);
        }

        return ValueTask.CompletedTask;
    }
}
