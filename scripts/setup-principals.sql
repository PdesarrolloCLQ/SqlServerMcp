/*
  SqlServerMcp — Script de referencia para principales de mínimo privilegio (Anexo A del ERS).

  EJECUCIÓN MANUAL: lo ejecuta el DBA, fuera del MCP. El MCP nunca ejecuta sentencias DCL.
  Ajuste nombres de esquemas, base de datos, contraseñas y método de autenticación a su entorno.
  Preferible: logins de Windows o de Microsoft Entra ID en lugar de logins SQL.

  Variables de sqlcmd (modo SQLCMD en SSMS o sqlcmd -v):
    $(Database)        base de datos objetivo                 (ej. VentasDev)
    $(DataSchema)      esquema de datos de negocio            (ej. ventas)
    $(WorkSchema)      esquema de trabajo para el agente      (ej. ia)
    $(DeniedSchema)    esquema sensible a denegar             (ej. seguridad)
    $(ReaderPassword)  contraseña del login lector
    $(WriterPassword)  contraseña del login escritor
*/
:on error exit

-- 1. Logins de instancia
IF SUSER_ID(N'mcp_lector') IS NULL
    CREATE LOGIN mcp_lector WITH PASSWORD = N'$(ReaderPassword)', CHECK_POLICY = ON, DEFAULT_DATABASE = [$(Database)];
IF SUSER_ID(N'mcp_escritor') IS NULL
    CREATE LOGIN mcp_escritor WITH PASSWORD = N'$(WriterPassword)', CHECK_POLICY = ON, DEFAULT_DATABASE = [$(Database)];
GO

USE [$(Database)];
GO

-- 2. Usuarios de base de datos
IF USER_ID(N'mcp_lector') IS NULL CREATE USER mcp_lector FOR LOGIN mcp_lector WITH DEFAULT_SCHEMA = [$(DataSchema)];
IF USER_ID(N'mcp_escritor') IS NULL CREATE USER mcp_escritor FOR LOGIN mcp_escritor WITH DEFAULT_SCHEMA = [$(DataSchema)];

-- 3. Esquema de trabajo con propietario dedicado sin login (rompe el encadenamiento de propiedad con dbo, RS-PRV-04)
IF USER_ID(N'mcp_propietario') IS NULL CREATE USER mcp_propietario WITHOUT LOGIN;
GO
IF SCHEMA_ID(N'$(WorkSchema)') IS NULL EXEC (N'CREATE SCHEMA [$(WorkSchema)] AUTHORIZATION mcp_propietario');
GO

-- 4. Rol de lectura: SELECT, VIEW DEFINITION y SHOWPLAN (RS-PRV-02)
IF DATABASE_PRINCIPAL_ID(N'rol_mcp_lectura') IS NULL CREATE ROLE rol_mcp_lectura;
GRANT SELECT, VIEW DEFINITION ON SCHEMA::[$(DataSchema)] TO rol_mcp_lectura;
GRANT SELECT, VIEW DEFINITION ON SCHEMA::[$(WorkSchema)] TO rol_mcp_lectura;
GRANT SHOWPLAN TO rol_mcp_lectura;
-- Dependencias entre objetos (RF-MET-12, RF-DDL-07): la vista exige SELECT explícito (por defecto solo db_owner)
-- y VIEW DEFINITION a nivel de base de datos para devolver filas. El DENY del paso 6 sigue ocultando el esquema sensible.
GRANT SELECT ON sys.sql_expression_dependencies TO rol_mcp_lectura;
GRANT VIEW DEFINITION TO rol_mcp_lectura;
ALTER ROLE rol_mcp_lectura ADD MEMBER mcp_lector;
-- Los procedimientos configurados como "ReadOnly" en el perfil se ejecutan con el login lector: conceda EXECUTE
-- solo sobre esos procedimientos, por ejemplo: GRANT EXECUTE ON [$(DataSchema)].usp_Consulta TO rol_mcp_lectura;

-- 5. Rol de escritura: DML en el esquema de datos; DML y DDL solo en el esquema de trabajo (RS-PRV-03)
IF DATABASE_PRINCIPAL_ID(N'rol_mcp_escritura') IS NULL CREATE ROLE rol_mcp_escritura;
-- REFERENCES permite crear claves foráneas y objetos WITH SCHEMABINDING que apunten a las tablas del esquema.
-- Efecto en el esquema de datos: una clave foránea desde el esquema de trabajo impide borrar las filas referenciadas,
-- y una vista con SCHEMABINDING impide modificar la tabla de la que depende. Quítelo de la primera línea si no lo desea.
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, REFERENCES, VIEW DEFINITION ON SCHEMA::[$(DataSchema)] TO rol_mcp_escritura;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, VIEW DEFINITION ON SCHEMA::[$(WorkSchema)] TO rol_mcp_escritura;
GRANT CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE TYPE, CREATE SYNONYM, SHOWPLAN TO rol_mcp_escritura;
GRANT SELECT ON sys.sql_expression_dependencies TO rol_mcp_escritura;
GRANT VIEW DEFINITION TO rol_mcp_escritura;
-- Solo efectivo donde tenga ALTER sobre el esquema (el de trabajo).
ALTER ROLE rol_mcp_escritura ADD MEMBER mcp_escritor;

-- 6. Red de seguridad sobre información sensible (RS-PRV-05)
IF SCHEMA_ID(N'$(DeniedSchema)') IS NOT NULL
    EXEC (N'DENY SELECT, INSERT, UPDATE, DELETE, EXECUTE, VIEW DEFINITION ON SCHEMA::[$(DeniedSchema)] TO rol_mcp_lectura, rol_mcp_escritura');
GO

-- 6b. Endurecimiento opcional (defensa en profundidad; el MCP también lo controla). Descomente si aplica:
--   DENY CREATE SYNONYM TO mcp_escritor;          -- si el agente no necesita crear sinónimos
--   Los permisos de procedimientos del sistema solo se pueden denegar en master:
--   USE master; CREATE USER mcp_escritor FOR LOGIN mcp_escritor; DENY EXECUTE ON sys.sp_executesql TO mcp_escritor;
--   (EXEC(@sql) no se puede denegar con permisos: lo bloquea el analizador del MCP.)

-- 7. Verificación (debe devolver 0 en ambas columnas para cada login)
EXECUTE AS LOGIN = N'mcp_escritor';
SELECT SUSER_SNAME() AS login_name, IS_SRVROLEMEMBER('sysadmin') AS es_sysadmin, IS_ROLEMEMBER('db_owner') AS es_db_owner;
SELECT * FROM fn_my_permissions(NULL, 'DATABASE');
REVERT;
EXECUTE AS LOGIN = N'mcp_lector';
SELECT SUSER_SNAME() AS login_name, IS_SRVROLEMEMBER('sysadmin') AS es_sysadmin, IS_ROLEMEMBER('db_owner') AS es_db_owner;
REVERT;
GO
