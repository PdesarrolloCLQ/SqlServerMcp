# SqlServerMcp

Servidor **MCP (Model Context Protocol)** local para **Microsoft SQL Server**. Permite que un agente de IA (Claude Code, Claude Desktop, VS Code con GitHub Copilot, Cursor…) consulte información, manipule datos y cree objetos de base de datos **de forma controlada, auditable y segura**.

El diseño parte de una premisa: **el agente no es confiable**. La seguridad no depende de lo que se le pida al modelo, sino de controles en el servidor (reglas sobre cada sentencia, confirmación humana, límites, auditoría) y en SQL Server (logins de mínimo privilegio). Implementa la especificación `Documento_Requisitos_SqlServerMcp.docx`.

**Contenido:** [Conceptos](#conceptos-clave) · [Instalación](#instalación-paso-a-paso) · [Configuración](#configuración-explicada) · [Permisos](#permisos-qué-cambiar-para-cada-necesidad) · [Uso diario](#uso-diario) · [Problemas frecuentes](#problemas-frecuentes) · [Línea de comandos](#línea-de-comandos) · [Arquitectura](#arquitectura)

## Conceptos clave

| Término | Qué es | Dónde se define |
|---|---|---|
| **Esquema** (de SQL Server) | Carpeta lógica dentro de una base de datos. En `ventas.Clientes`, `ventas` es el esquema y `Clientes` la tabla. El esquema por defecto es `dbo`. El MCP y los permisos se controlan **por esquema**. | En la base de datos |
| **`$schema`** (del JSON) | Primera línea del archivo de configuración. Solo sirve para que el editor (VS Code) autocomplete y valide el archivo. **No afecta al servidor**; puede borrarla. No tiene relación con los esquemas de SQL Server. | `sqlservermcp.json` |
| **Perfil** | Un destino con sus reglas: servidor, base, modo, logins, esquemas permitidos, exclusiones, límites y políticas. Puede tener varios (desarrollo, otra base, producción…). | `sqlservermcp.json` → `profiles` |
| **Modo** | Qué herramientas existen: `LECTURA` (consultar), `ESCRITURA_DATOS` (+ INSERT/UPDATE/DELETE, procedimientos, transacciones), `COMPLETO` (+ CREATE/ALTER/DROP). | `mode` del perfil |
| **Logins** | Cada perfil usa dos usuarios de SQL Server: el **lector** (consultas y metadatos) y el **escritor** (cambios). El MCP elige cuál según la operación. | `connection` y `writeConnection` |
| **Roles de SQL Server** | Donde viven los permisos reales: `rol_mcp_lectura` (lector) y `rol_mcp_escritura` (escritor). Los crea el script del DBA. | SQL Server (`scripts/setup-principals.sql`) |
| **Política** | Decisión sobre cada sentencia: `ALLOW` (ejecutar), `ALERT` (ejecutar y alertar), `CONFIRM` (pedir aprobación al usuario), `BLOCK` (rechazar). | `policy` del perfil + reglas fijas |

### Cómo se decide si algo se ejecuta

Una operación debe pasar **las dos capas**. Si el MCP se configura mal, SQL Server sigue impidiendo lo que el login no tiene permitido.

```
Petición del agente
 ├─ 1. ¿El modo del perfil publica la herramienta?                 MCP
 ├─ 2. Política: ALLOW / ALERT / CONFIRM / BLOCK                    MCP
 │      (esquemas permitidos, exclusiones, reglas fijas y propias)
 ├─ 3. Si es CONFIRM, el usuario aprueba en el cliente MCP          Usted
 ├─ 4. Elige el login: lector o escritor                            MCP
 └─ 5. SQL Server comprueba los permisos del rol de ese login       SQL Server
```

Reglas fijas que **ninguna configuración desactiva**: una sola sentencia por llamada; nunca `GRANT`/`DENY`/`REVOKE`, `EXECUTE AS`, `BACKUP`/`RESTORE`, `xp_cmdshell`, `OPENROWSET`, servidores vinculados ni bases del sistema. Catálogo completo en [`docs/POLICY_RULES.md`](docs/POLICY_RULES.md).

## Instalación paso a paso

Requisitos: Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download), SQL Server 2016 o superior y un cliente MCP. Los comandos son de **PowerShell** y se ejecutan desde la carpeta del repositorio.

### 1. Preparar SQL Server (DBA, una vez por base de datos)

[`scripts/setup-principals.sql`](scripts/setup-principals.sql) crea los logins `mcp_lector` y `mcp_escritor`, sus roles con permisos mínimos, el esquema de trabajo y el bloqueo del esquema sensible. Se ejecuta con un usuario administrador, **fuera del MCP**.

| Variable | Significado | Ejemplo |
|---|---|---|
| `Database` | Base de datos a la que tendrá acceso el agente | `VentasDev` |
| `DataSchema` | Esquema donde están sus tablas; el agente podrá leerlo y modificar datos | `dbo` o `ventas` |
| `WorkSchema` | Esquema de trabajo: el **único** donde el agente puede crear o modificar tablas. Se crea si no existe | `ia` |
| `DeniedSchema` | Esquema sensible que nadie del MCP puede ver. Si no existe, se ignora | `seguridad` |
| `ReaderPassword` / `WriterPassword` | Contraseñas de los logins. Si los logins ya existen, no se cambian | — |

**Con SSMS:** abra el script (*File → Open*), active *Query → SQLCMD Mode*, pegue al inicio las variables y ejecute con F5. No guarde el archivo con las contraseñas.

```sql
:setvar Database "VentasDev"
:setvar DataSchema "ventas"
:setvar WorkSchema "ia"
:setvar DeniedSchema "seguridad"
:setvar ReaderPassword "<contraseña del lector>"
:setvar WriterPassword "<contraseña del escritor>"
```

El resultado final debe mostrar `es_sysadmin = 0` y `es_db_owner = 0` para ambos logins. El MCP **se niega a iniciar** (código 3) si un login es `sysadmin`, `db_owner` o tiene privilegios equivalentes.

### 2. Publicar el ejecutable

```powershell
dotnet publish src/SqlServerMcp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o C:\Tools\SqlServerMcp
```

Opcional, para escribir `SqlServerMcp` desde cualquier carpeta: agregue `C:\Tools\SqlServerMcp` al PATH de su usuario (*Inicio → "Editar las variables de entorno de esta cuenta" → Path → Editar → Nuevo*) y abra una terminal nueva.

### 3. Crear el archivo de configuración

```powershell
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\SqlServerMcp"
```

```powershell
Copy-Item .\config\sqlservermcp.example.json "$env:LOCALAPPDATA\SqlServerMcp\sqlservermcp.json"
```

Opcional, para que VS Code autocomplete el archivo (es lo que usa la línea `$schema`):

```powershell
C:\Tools\SqlServerMcp\SqlServerMcp.exe --write-config-schema "$env:LOCALAPPDATA\SqlServerMcp\sqlservermcp.schema.json"
```

Edítelo y ajuste al menos el perfil `desarrollo` (servidor, base, esquemas). Vea [Configuración explicada](#configuración-explicada).

```powershell
notepad "$env:LOCALAPPDATA\SqlServerMcp\sqlservermcp.json"
```

> En PowerShell use `$env:LOCALAPPDATA`; `%LOCALAPPDATA%` solo funciona en `cmd` y dentro del archivo JSON.

### 4. Guardar las contraseñas en Windows

Las contraseñas **nunca** van en el archivo; este solo guarda una referencia (`credentialRef`). Cada comando pide la contraseña sin mostrarla:

```powershell
cmdkey /generic:SqlServerMcp/dev-lector /user:mcp_lector /pass
```

```powershell
cmdkey /generic:SqlServerMcp/dev-escritor /user:mcp_escritor /pass
```

| Referencia en el archivo | Dónde está el secreto |
|---|---|
| `wincred:SqlServerMcp/dev-lector` | Administrador de credenciales de Windows (comando `cmdkey` anterior) |
| `dpapi:C:\ruta\secreto.bin` | Archivo cifrado para su usuario: `SqlServerMcp --protect-secret C:\ruta\secreto.bin` |
| `env:VARIABLE` | Variable de entorno que pasa el cliente MCP |

### 5. Validar

```powershell
C:\Tools\SqlServerMcp\SqlServerMcp.exe --check-config --config "$env:LOCALAPPDATA\SqlServerMcp\sqlservermcp.json"
```

Debe mostrar `Configuración válida` y los perfiles activos. Las advertencias no impiden el arranque.

### 6. Registrar en el cliente MCP

**Claude Code** (`-s user` lo deja disponible en todos sus proyectos):

```powershell
claude mcp add -s user sqlservermcp -- C:\Tools\SqlServerMcp\SqlServerMcp.exe --config "$env:LOCALAPPDATA\SqlServerMcp\sqlservermcp.json"
```

Compruebe con `claude mcp list` que aparezca `✔ Connected`.

**Claude Desktop** (`claude_desktop_config.json`), **Cursor** (`.cursor/mcp.json`) o `.mcp.json` de proyecto; en **VS Code** (`.vscode/mcp.json`) cambie `mcpServers` por `servers` y agregue `"type": "stdio"`:

```json
{
  "mcpServers": {
    "sqlservermcp": {
      "command": "C:\\Tools\\SqlServerMcp\\SqlServerMcp.exe",
      "args": ["--config", "C:\\Users\\<usuario>\\AppData\\Local\\SqlServerMcp\\sqlservermcp.json"]
    }
  }
}
```

### 7. Probar

Abra una **sesión nueva** del cliente y pida, por ejemplo: *"Usa get_server_policy y test_connection de sqlservermcp"* y luego *"¿Qué tablas hay en el esquema ventas?"*.

## Configuración explicada

El archivo es JSON estricto: una propiedad mal escrita o desconocida hace que el servidor no inicie y `--check-config` indica cuál es. **Los cambios se aplican al reiniciar el MCP** (en Claude Code, abriendo una sesión nueva).

### Raíz del archivo

| Propiedad | Para qué sirve |
|---|---|
| `defaultProfile` | Perfil que se usa cuando el agente no indica uno. |
| `profiles` | Los perfiles, cada uno con un nombre (`"desarrollo": { … }`). |
| `audit` | Carpeta (`path`) y días de retención (`retentionDays`) de la auditoría. |
| `alerts` | Canales opcionales de alertas: registro de eventos de Windows y webhook (Teams/Slack). |
| `logging` | Nivel de log (`Error`, `Warning`, `Information`, `Debug`). |

### Propiedades de un perfil

| Propiedad | Para qué sirve | Valores / por defecto |
|---|---|---|
| `enabled` | Desactivar un perfil sin borrarlo. | `true` |
| `description` | Texto que ve el agente para elegir perfil; descríbalo bien. | — |
| `mode` | Herramientas disponibles. | `LECTURA` (defecto), `ESCRITURA_DATOS`, `COMPLETO` |
| `environment` | Tipo de entorno. En `Production` se recomienda `LECTURA`. | `Development`, `Test`, `Production` |
| `connection.server` | Servidor y puerto (`servidor,puerto`). | `localhost,14330` |
| `connection.database` | Base de datos inicial. | — |
| `connection.authentication` | Tipo de autenticación. | `SqlPassword`, `WindowsIntegrated`, `EntraInteractive`, `EntraDefault`… |
| `connection.userName` / `credentialRef` | Login lector y referencia a su contraseña. | `mcp_lector` / `wincred:…` |
| `connection.encrypt` | Cifrado de la conexión. | `Mandatory` (defecto), `Strict` |
| `connection.trustServerCertificate` | Aceptar certificados autofirmados (contenedores, desarrollo). **No lo use en producción.** | `false` |
| `writeConnection` | Login escritor; solo `userName` y `credentialRef`, el resto se hereda. Sin él, todo usa el lector. | — |
| `allowedDatabases` | Bases a las que puede acceder el perfil. | Vacío = solo la inicial |
| `allowedSchemas` | Esquemas que el agente puede ver y usar. | Vacío = solo `dbo` |
| `ddlSchemas` | Esquemas donde puede crear/modificar objetos (solo modo `COMPLETO`). | Vacío = ninguno |
| `excluded` | Lo que el agente **nunca ve**: `schemas`, `tables` (`esquema.tabla`), `columns` (`*.Contrasena`, `tabla.columna`). | — |
| `sensitive` | Secretos: columnas que se ven con el valor oculto (`****`) y que no pueden usarse en filtros ni escrituras. Ya detecta *password*, *token*, *tarjeta*, *ssn*, *apikey*, *pin*, *iban*… (lista completa en `get_server_policy`). `action`: `Mask` o `Block`. | `Mask` |
| `pii` | Datos personales (*email*, *telefono*, *direccion*, *dni*, *pasaporte*…): se enmascaran en el resultado pero sí pueden usarse en filtros y escrituras. `action`: `Mask` o `Allow`. | `Mask` |
| `limits` | Topes: `maxRows` (500), `commandTimeoutSeconds` (30), `maxAffectedRows` (1000), `confirmAboveRows` (100: más filas piden confirmación), `maxCallsPerMinute` (60)… | Ver `get_server_policy` |
| `policy` | Qué hacer con cada tipo de sentencia: `dropAction`, `truncateAction` (mínimo `CONFIRM`), `unfilteredDml` (UPDATE/DELETE sin WHERE, `BLOCK`), `dynamicSql` (`BLOCK`), `alwaysConfirmDml`, y `rules` propias. | Las protecciones no se pueden relajar por debajo del mínimo |
| `procedures` | Procedimientos que el agente puede ejecutar: `name`, `kind` (`ReadOnly` se ejecuta con el lector, `SideEffects` con el escritor) y `confirm`. | Vacío = ninguno |

Los patrones de `sensitive` y `pii` no distinguen mayúsculas, separadores (`_`, `-`) ni acentos. Una palabra sin comodines coincide por **segmentos** del nombre: `pin` marca `PinCode` pero no `Shipping`; `apikey` marca `ApiKey`, `api_key` y `APIKey`. Un patrón con `*` (por ejemplo `*password*`) compara el nombre completo.

Ejemplo de regla propia en `policy.rules`: `{ "id": "R-LOCAL-01", "statement": "Delete", "object": "ventas.Facturas", "action": "BLOCK", "reason": "Las facturas no se eliminan" }`.

### Crear un perfil para otra base de datos

1. Ejecute el [paso 1](#1-preparar-sql-server-dba-una-vez-por-base-de-datos) sobre la nueva base (los logins se reutilizan si están en la misma instancia).
2. Dentro de `profiles`, después de la `}` del último perfil, ponga una coma y agregue:

```json
"inventario": {
  "description": "Base de inventario (pruebas)",
  "mode": "ESCRITURA_DATOS",
  "environment": "Development",
  "connection": {
    "server": "localhost,14330",
    "database": "Inventario",
    "authentication": "SqlPassword",
    "userName": "mcp_lector",
    "credentialRef": "wincred:SqlServerMcp/dev-lector",
    "encrypt": "Mandatory",
    "trustServerCertificate": true
  },
  "writeConnection": { "userName": "mcp_escritor", "credentialRef": "wincred:SqlServerMcp/dev-escritor" },
  "allowedDatabases": [ "Inventario" ],
  "allowedSchemas": [ "dbo" ]
}
```

3. Valide con `--check-config` y abra una sesión nueva del cliente.

Si la base está **en otro servidor**, cree allí los logins con contraseñas nuevas, guárdelas con `cmdkey` bajo otros nombres (por ejemplo `SqlServerMcp/inv-lector`) y cambie `server` y los dos `credentialRef`.

## Permisos: qué cambiar para cada necesidad

Casi siempre hay que tocar **las dos capas**: el archivo (lo que el MCP permite) y SQL Server (lo que el login puede hacer).

| Quiero que el agente… | En `sqlservermcp.json` | En SQL Server (como DBA) |
|---|---|---|
| Solo consulte | `"mode": "LECTURA"` | Nada más (el lector solo tiene `SELECT`) |
| Modifique datos | `"mode": "ESCRITURA_DATOS"` y `writeConnection` | Ya incluido para `DataSchema` |
| Use otro esquema | Agregarlo a `allowedSchemas` | `GRANT SELECT, VIEW DEFINITION ON SCHEMA::[otro] TO rol_mcp_lectura;` y `GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, VIEW DEFINITION ON SCHEMA::[otro] TO rol_mcp_escritura;` |
| Cree o modifique tablas | `"mode": "COMPLETO"` y el esquema en `ddlSchemas` | Solo en el esquema de trabajo (`ia`), que ya tiene `ALTER` |
| No vea una tabla o columna | Agregarla a `excluded` | Opcional: `DENY SELECT ON …` |
| Ejecute un procedimiento de consulta | `procedures`: `{ "name": "dbo.MiProc", "kind": "ReadOnly" }` | `GRANT EXECUTE ON dbo.MiProc TO rol_mcp_lectura;` |
| Ejecute un procedimiento que modifica | `procedures`: `{ "name": "dbo.MiProc", "kind": "SideEffects", "confirm": true }` | Ya incluido en el esquema de datos |
| No borre nunca en una tabla | `policy.rules`: regla `Delete` con `BLOCK` | Opcional: `DENY DELETE ON …` |

**Endurecimiento recomendado (defensa en profundidad).** El MCP resuelve los sinónimos, analiza la definición de cada procedimiento antes de ejecutarlo y bloquea el SQL dinámico, pero conviene que SQL Server tampoco lo permita. `get_server_policy` (`securityWarnings`) avisa si estos permisos siguen activos:

```sql
-- En la base de datos del perfil (si el agente no necesita crear sinónimos):
DENY CREATE SYNONYM TO mcp_escritor;
-- En master: los permisos sobre procedimientos del sistema solo se pueden denegar allí.
USE master;
CREATE USER mcp_escritor FOR LOGIN mcp_escritor;
DENY EXECUTE ON sys.sp_executesql TO mcp_escritor;
```

`EXEC(@sql)` no se puede denegar con permisos: lo bloquea siempre el analizador del MCP (`POL-DYN-001`).

## Uso diario

- **Perfil:** cada herramienta acepta el parámetro `profile` y **el agente elige** según lo que usted le pida ("usa el perfil inventario"); si no indica ninguno, se usa `defaultProfile`. Toda respuesta incluye `profile` y `database`. Para impedir que el agente cambie de perfil, registre una instancia dedicada con `--profile <nombre>` (recomendado en producción).
- **Primero `get_server_policy`:** muestra perfiles, modo, esquemas, límites y si el cliente admite confirmaciones (`clientSupportsConfirmation`).
- **Confirmaciones:** `DROP`, `TRUNCATE`, DML que afecte más de `confirmAboveRows` filas (dentro de una transacción explícita cuenta el **acumulado** de la transacción) y procedimientos con `confirm` piden aprobación en el cliente.
- **Procedimientos:** antes de ejecutar uno, el MCP analiza su definición (y la de los que invoca) con las mismas reglas que `execute_ddl`; si contiene SQL dinámico u otra operación prohibida, no se ejecuta. El login de lectura necesita `VIEW DEFINITION` sobre el esquema del procedimiento. Si el cliente no lo admite, se rechazan con `CONFIRMATION_UNAVAILABLE` (por seguridad).
- **Herramientas:**

| Modo | Herramientas |
|---|---|
| `LECTURA` | `get_server_policy`, `test_connection`, `list_databases`, `list_schemas`, `list_tables`, `describe_table`, `list_routines`, `describe_routine`, `get_object_definition`, `list_indexes`, `get_relationships`, `get_dependencies`, `search_objects`, `get_table_stats`, `list_objects`, `sample_table`, `run_query`, `get_execution_plan`, `validate_sql` |
| `ESCRITURA_DATOS` | + `execute_dml`, `insert_rows`, `execute_procedure`, `begin_transaction`, `commit_transaction`, `rollback_transaction` |
| `COMPLETO` | + `execute_ddl` |

- **Códigos de error frecuentes:** `POLICY_BLOCKED` (una regla lo impide; la respuesta indica cuál y sugiere alternativa), `CONFIRMATION_REQUIRED`/`CONFIRMATION_DENIED`/`CONFIRMATION_UNAVAILABLE`, `LIMIT_EXCEEDED`, `NOT_FOUND` (no existe o está excluido), `SQL_ERROR` (error de SQL Server, con número y mensaje), `CONNECTION_ERROR`, `SESSION_SUSPENDED`.
- **Suspensión:** tras 3 intentos críticos bloqueados en 10 minutos, la escritura se suspende hasta reiniciar el MCP.
- **Auditoría:** todo queda en `%LOCALAPPDATA%\SqlServerMcp\audit\` (`audit-*`, `schema-changes-*` con la definición anterior de cada objeto modificado, `alerts-*`). Verifique que no fue alterada con `SqlServerMcp --verify-audit <carpeta>`.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| El MCP no aparece o no conecta en el cliente | Abra una sesión nueva; revise `claude mcp list` y valide con `--check-config`. |
| `CONNECTION_ERROR` con un contenedor o certificado autofirmado | Ponga `"trustServerCertificate": true` en `connection` (solo desarrollo). |
| `CONNECTION_ERROR` por login fallido | Falta la credencial en Windows o no coincide el nombre de `credentialRef`: repita el `cmdkey`. |
| El servidor no inicia (código 3) | Un login es `sysadmin`, `db_owner` o similar. Use los logins del script. |
| `SQL_ERROR` 229 (*permission denied*) | El MCP lo permite pero SQL Server no: falta un `GRANT` (vea [Permisos](#permisos-qué-cambiar-para-cada-necesidad)). |
| `NOT_FOUND` o `POLICY_BLOCKED` con una tabla que existe | Su esquema no está en `allowedSchemas` o la tabla está en `excluded`. |
| `CONFIRMATION_UNAVAILABLE` | El cliente no admite aprobaciones (elicitation). Use otro cliente o haga la operación manualmente. |
| `copy … %LOCALAPPDATA% …` falla en PowerShell | Use `$env:LOCALAPPDATA`. |
| El servidor termina con `DllNotFoundException` (SNI) al conectar | Ejecutable publicado con una versión anterior que dependía de `Microsoft.Data.SqlClient.SNI.dll` suelto. Vuelva a publicar: ahora el `.exe` incluye las bibliotecas nativas y no necesita archivos adicionales. |
| `dotnet publish` falla porque el `.exe` está en uso | Cierre las sesiones del cliente que usan el MCP (o renombre el `.exe` en uso) y vuelva a publicar. |

## Línea de comandos

| Comando | Qué hace |
|---|---|
| `SqlServerMcp --config <archivo> [--profile <nombre>]` | Inicia el servidor (lo ejecuta el cliente MCP). `--profile` limita la instancia a un perfil. |
| `SqlServerMcp --check-config [--config <archivo>]` | Valida la configuración sin conectarse. |
| `SqlServerMcp --protect-secret <archivo>` | Cifra un secreto con DPAPI para usarlo como `dpapi:<archivo>`. |
| `SqlServerMcp --verify-audit <carpeta\|archivo>` | Verifica que la auditoría no fue alterada. |
| `SqlServerMcp --write-config-schema <archivo>` | Genera el JSON Schema del archivo de configuración. |
| `SqlServerMcp --version` / `--help` | Versión y ayuda. |

Sin `--config`, el archivo se busca en la variable `SQLSERVERMCP_CONFIG`, junto al ejecutable y en `%LOCALAPPDATA%\SqlServerMcp\sqlservermcp.json`. Las variables `SQLSERVERMCP__<ruta>` (por ejemplo `SQLSERVERMCP__profiles__desarrollo__limits__maxRows=200`) cambian solo parámetros no sensibles. Códigos de salida: `0` correcto, `1` uso incorrecto, `2` configuración inválida, `3` inicio rechazado por seguridad, `4` auditoría alterada.

## Arquitectura

```
Agente IA ─► Cliente MCP ─stdio/JSON-RPC─► SqlServerMcp ──TDS cifrado──► SQL Server (logins de mínimo privilegio)
```

| Carpeta (`src/SqlServerMcp/`) | Responsabilidad |
|---|---|
| `Configuration/` | Carga estricta y validación del JSON, perfiles y límites. |
| `Policy/` | Motor de políticas: parser oficial de T-SQL (`TSql170Parser`), análisis del árbol sintáctico completo, reglas fijas, configurables y denylist. |
| `Tools/` | Herramientas MCP, registro por modo, filtros de argumentos y formato de respuesta. |
| `Confirmation/` | Aprobación del usuario mediante *elicitation* de MCP, ligada al hash de la sentencia. |
| `Execution/` | Conexiones, consultas, planes, DML con estimación de filas, procedimientos, DDL con respaldo de definiciones y transacciones. |
| `Metadata/` | Descubrimiento de esquemas, tablas, rutinas, índices y dependencias respetando exclusiones. |
| `Audit/` | Auditoría JSON Lines con cadena de hashes, cambios de esquema, alertas y monitor de intentos. |
| `Security/` | Secretos (Windows, DPAPI, entorno) y verificación de privilegios al iniciar. |

Construido con **.NET 10**, el **SDK oficial de MCP para C#** (`ModelContextProtocol` 2.4, transporte stdio), **Microsoft.SqlServer.TransactSql.ScriptDom** (las sentencias se clasifican sobre el árbol sintáctico, nunca por palabras clave), **Microsoft.Data.SqlClient** 7.1 y **Serilog** (logs a stderr; stdout queda reservado al protocolo).

## Pruebas

```powershell
dotnet test --project tests/SqlServerMcp.Tests/SqlServerMcp.Tests.csproj
```

```powershell
dotnet test --project tests/SqlServerMcp.IntegrationTests/SqlServerMcp.IntegrationTests.csproj
```

- **Unitarias** (836): motor de políticas con corpus de evasiones, configuración, auditoría y esquemas de las herramientas.
- **Integración y punta a punta** (136): ejecutan el servidor real con el cliente MCP oficial contra SQL Server, validan cada respuesta contra su esquema de salida y cubren los criterios de aceptación CA-01 a CA-30. Levantan SQL Server en un contenedor con Testcontainers (requiere Docker) o usan la instancia de `SQLSERVERMCP_TEST_SERVER` y las contraseñas `SQLSERVERMCP_TEST_SA_PASSWORD`, `SQLSERVERMCP_TEST_READER_PASSWORD` y `SQLSERVERMCP_TEST_WRITER_PASSWORD`.
