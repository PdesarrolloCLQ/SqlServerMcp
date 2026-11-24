# Catálogo de reglas de política

Catálogo generado a partir de `src/SqlServerMcp/Policy/PolicyRules.cs` (RNF-11). Cada decisión del motor indica la regla determinante, su motivo y una sugerencia (RS-POL-11).

- **Acciones**: PERMITIR (se ejecuta y audita), ALERTAR (se ejecuta y genera alerta), CONFIRMAR (requiere aprobación humana por elicitation), BLOQUEAR (se rechaza con `POLICY_BLOCKED` u otro código indicado).
- **Inmutable**: no puede relajarse por configuración, variables de entorno ni argumentos (RS-POL-10, RS-DCL-02). **Configurable**: la acción proviene de `policy` del perfil, con un piso mínimo (DROP/TRUNCATE/DROP COLUMN ≥ CONFIRMAR; DML sin filtro y SQL dinámico ≥ CONFIRMAR; triggers ≥ ALERTAR).
- **Reglas propias** (`policy.rules` del perfil): si coinciden varias, prevalece la más restrictiva, acotada al piso de la categoría; su identificador aparece como regla determinante.
- La decisión final es la acción más restrictiva entre todas las reglas que coinciden; ante empate, la primera en orden de evaluación (estructura → críticas → sentencia → herramienta → forma → referencias → configurables → tiempo de ejecución).

## Administración de BD e instancia

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-ADM-001` | BLOQUEAR | Inmutable | Crítica | Crítico | Crear, modificar o eliminar bases de datos está prohibido. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-ADM-002` | BLOQUEAR | Inmutable | Crítica | Crítico | BACKUP y RESTORE están prohibidos. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-ADM-003` | BLOQUEAR | Inmutable | Crítica | Crítico | Los comandos DBCC están prohibidos. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-ADM-004` | BLOQUEAR | Inmutable | Crítica | Crítico | La administración de la instancia (SHUTDOWN, KILL, CHECKPOINT, RECONFIGURE, sp_configure, configuración del servidor, endpoints, grupos de disponibilidad, Resource Governor, sesiones de eventos) está prohibida. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |

## Estructura y análisis

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-BATCH-001` | BLOQUEAR | Inmutable | Advertencia | Medio | Se admite exactamente una sentencia por invocación. | Envíe una sola sentencia por llamada, sin ';' adicionales ni separadores GO. |
| `POL-INTERNAL-001` | BLOQUEAR | Inmutable | Advertencia | Medio | No se pudo completar el análisis de la sentencia; se rechaza por seguridad (fail-closed). | Simplifique la sentencia y vuelva a intentarlo. |
| `POL-PARSE-001` | BLOQUEAR | Inmutable | — | Bajo | La sentencia no se pudo analizar: error de sintaxis T-SQL. | Corrija la sintaxis indicada y vuelva a intentarlo; puede usar validate_sql para comprobarla. |
| `POL-PARSE-002` | BLOQUEAR | Inmutable | — | Bajo | La sentencia está vacía o solo contiene comentarios. | Envíe exactamente una sentencia T-SQL. |
| `POL-PARSE-003` | BLOQUEAR | Inmutable | Advertencia | Medio | La sentencia excede la profundidad de anidamiento o la complejidad máxima admitida. | Simplifique la sentencia (menos paréntesis, subconsultas o expresiones encadenadas). |
| `POL-SIZE-001` | BLOQUEAR (`LIMIT_EXCEEDED`) | Inmutable | — | Bajo | La sentencia supera el tamaño máximo permitido por el perfil. | Reduzca la sentencia o divídala en operaciones más pequeñas. |
| `POL-UNKNOWN-001` | BLOQUEAR | Inmutable | Advertencia | Medio | Tipo de sentencia no reconocido o no admitido por la política. | Use solo SELECT, DML (INSERT, UPDATE, DELETE, MERGE) o DDL sobre objetos permitidos. Consulte get_server_policy. |

## Referencias y catálogo

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-CAT-001` | BLOQUEAR | Inmutable | Advertencia | Alto | Acceso a una vista o función de catálogo sensible (logins, credenciales, claves, auditoría o registro de transacciones). | Esta información no está disponible a través del MCP. |
| `POL-REF-001` | BLOQUEAR | Inmutable | Advertencia | Medio | Referencia a una base de datos que no está en la lista permitida del perfil. | Use solo las bases de datos permitidas (consulte get_server_policy o list_databases). |
| `POL-REF-002` | BLOQUEAR | Inmutable | Advertencia | Medio | Referencia a una base de datos del sistema o a una tabla temporal global (##): no permitida. | Use objetos de las bases de datos permitidas; tempdb solo para tablas temporales de sesión (#tabla). |
| `POL-REF-003` | BLOQUEAR | Inmutable | — | Medio | Referencia a un esquema no permitido por el perfil. | Califique los objetos con un esquema permitido (los nombres sin esquema se interpretan como dbo); consulte list_schemas. |
| `POL-REF-004` | BLOQUEAR | Inmutable | — | Medio | Las consultas a vistas y funciones de catálogo del sistema (sys, INFORMATION_SCHEMA) no están habilitadas en este perfil. | Use las herramientas de metadatos (list_tables, describe_table, list_routines, get_dependencies…). |
| `POL-SYN-001` | BLOQUEAR | Inmutable | Crítica | Crítico | Un sinónimo solo puede apuntar a tablas, vistas, procedimientos o funciones de usuario de las bases y esquemas permitidos del perfil; no a objetos del sistema (sp_*, xp_*, sys, INFORMATION_SCHEMA, master, msdb, tempdb, model, la base de recursos), a otros sinónimos ni a objetos inexistentes. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-SYN-002` | BLOQUEAR | Inmutable | Advertencia | Alto | No fue posible resolver a qué objeto apunta un sinónimo referenciado; por seguridad la sentencia se bloquea. | Use el nombre del objeto base en lugar del sinónimo, o reintente si la base de datos no estaba disponible. |

## Límites en tiempo de ejecución

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-COST-001` | CONFIRMAR | Configurable | Informativa | Medio | El costo estimado del plan supera el umbral de confirmación del perfil. | Agregue filtros o índices adecuados, o espere la aprobación del usuario. |
| `POL-DDL-IDX-LARGE` | ALERTAR | Configurable | Advertencia | Medio | Creación de un índice sobre una tabla grande. | Considere ONLINE = ON y ejecutarlo en una ventana de mantenimiento. |
| `POL-DML-THRESHOLD` | CONFIRMAR | Configurable | — | Alto | Las filas afectadas estimadas superan el umbral de confirmación del perfil. | Espere la aprobación del usuario. |
| `POL-DML-TX-THRESHOLD` | CONFIRMAR | Configurable | — | Alto | El acumulado de filas afectadas en la transacción explícita supera el umbral de confirmación del perfil. | Espere la aprobación del usuario; dividir la operación en varias sentencias no evita el umbral. |
| `POL-LIM-001` | BLOQUEAR (`LIMIT_EXCEEDED`) | Inmutable | Advertencia | Alto | Las filas afectadas estimadas superan el máximo permitido por el perfil. | Agregue filtros para afectar menos filas o divida la operación en lotes. |
| `POL-LIM-CART` | CONFIRMAR | Configurable | Informativa | Alto | Producto cartesiano evidente (JOIN sin condición) sobre una tabla grande. | Agregue la condición de unión (ON …) entre las tablas. |
| `POL-LIM-TX` | BLOQUEAR (`LIMIT_EXCEEDED`) | Inmutable | Advertencia | Alto | El acumulado de filas afectadas en la transacción explícita supera el máximo permitido por el perfil. | Confirme o revierta la transacción actual y divida el trabajo en transacciones más pequeñas. |

## Transacciones, sesión y contexto

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-CTX-001` | BLOQUEAR | Inmutable | Advertencia | Medio | USE está prohibido: la base de datos se fija en la conexión del perfil. | Use nombres de tres partes hacia bases de datos permitidas o cambie de perfil. |
| `POL-PROC-001` | BLOQUEAR | Inmutable | Advertencia | Medio | Los bloques de control de flujo, variables y cursores (BEGIN…END, IF, WHILE, DECLARE, TRY…CATCH, PRINT, THROW…) solo se admiten dentro del cuerpo de un módulo. | Envíe una única sentencia SELECT, DML o DDL; para lógica procedimental cree un procedimiento con execute_ddl. |
| `POL-SET-001` | BLOQUEAR | Inmutable | Advertencia | Medio | Las sentencias SET que modifican el estado de la sesión están prohibidas (las opciones de sesión las fija el MCP). | Elimine la sentencia SET; los límites, el aislamiento y el plan de ejecución se controlan con las herramientas del MCP. |
| `POL-SET-002` | BLOQUEAR | Inmutable | Advertencia | Medio | SET QUOTED_IDENTIFIER está prohibido en cualquier posición (altera la interpretación de identificadores). | Elimine SET QUOTED_IDENTIFIER y delimite identificadores con corchetes. |
| `POL-TCL-001` | BLOQUEAR | Inmutable | Advertencia | Medio | El control de transacciones en texto libre (BEGIN/COMMIT/ROLLBACK/SAVE TRAN, SET IMPLICIT_TRANSACTIONS) está prohibido: las transacciones las gestiona el MCP. | Use las herramientas begin_transaction, commit_transaction y rollback_transaction. |
| `POL-TCL-002` | BLOQUEAR | Inmutable | Advertencia | Alto | Las transacciones distribuidas están prohibidas. | Use transacciones locales gestionadas por el MCP. |
| `POL-WAIT-001` | BLOQUEAR | Inmutable | Advertencia | Medio | WAITFOR está prohibido. | Elimine WAITFOR; las esperas prolongadas consumen recursos del servidor. |

## Protección de datos

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-DAT-001` | BLOQUEAR | Inmutable | Advertencia | Alto | Referencia a un esquema, tabla o vista excluido por la configuración. | Este objeto no está disponible a través del MCP. |
| `POL-DAT-002` | BLOQUEAR | Inmutable | Advertencia | Alto | Referencia a una columna excluida por la configuración. | Elimine la columna de la sentencia; no está disponible a través del MCP. |
| `POL-DAT-003` | BLOQUEAR | Inmutable | Advertencia | Alto | Referencia a una columna sensible: el perfil bloquea su uso. | Elimine la columna sensible de la sentencia. |
| `POL-DAT-004` | BLOQUEAR | Inmutable | Advertencia | Alto | Una columna sensible solo puede aparecer sin transformar en la lista SELECT principal (se enmascara); su uso en filtros, uniones, agrupaciones, ordenamientos, funciones, escrituras o subconsultas permitiría inferir su valor. | Elimine la columna sensible de esa posición; si la necesita en el resultado, selecciónela directamente para que se enmascare. |
| `POL-DAT-005` | BLOQUEAR | Inmutable | Advertencia | Alto | No se puede garantizar la protección de columnas sensibles o excluidas en el resultado: columna sensible con alias en posición indeterminada, FOR JSON/XML, o SELECT * cuyo resultado se renombra, se combina o se reutiliza (UNION, lista de columnas de una CTE o tabla derivada, INSERT … SELECT, subconsultas, cursores). | Enumere explícitamente solo las columnas necesarias, sin '*' y sin alias sobre columnas sensibles, y evite FOR JSON/XML con columnas sensibles. |
| `POL-DAT-006` | BLOQUEAR | Inmutable | Advertencia | Alto | Una columna sensible no puede devolverse en la cláusula OUTPUT. | Elimine la columna sensible de la cláusula OUTPUT. |

## Control de acceso (DCL)

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-DCL-001` | BLOQUEAR | Inmutable | Crítica | Crítico | Las sentencias GRANT, DENY y REVOKE están prohibidas. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-DCL-002` | BLOQUEAR | Inmutable | Crítica | Crítico | La gestión de logins y usuarios está prohibida. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-DCL-003` | BLOQUEAR | Inmutable | Crítica | Crítico | La gestión de roles y de sus miembros está prohibida. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-DCL-004` | BLOQUEAR | Inmutable | Crítica | Crítico | El cambio de propiedad de objetos o bases de datos está prohibido. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-DCL-005` | BLOQUEAR | Inmutable | Crítica | Crítico | La gestión de certificados, claves, credenciales y firmas está prohibida. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-DCL-006` | BLOQUEAR | Inmutable | Crítica | Crítico | La gestión de políticas de seguridad, clasificación de sensibilidad, enmascaramiento dinámico y auditoría está prohibida. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-DCL-007` | BLOQUEAR | Inmutable | Crítica | Crítico | Cambiar TRUSTWORTHY, DB_CHAINING o CONTAINMENT de una base de datos está prohibido. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |

## DDL

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-DDL-ALTER` | PERMITIR | Configurable | — | Medio | DDL de modificación no destructiva (ALTER). | Sin acciones adicionales. |
| `POL-DDL-ARGS` | BLOQUEAR | Inmutable | — | Medio | Los argumentos de sp_rename o de los procedimientos de propiedades extendidas deben ser literales válidos sobre objetos de un esquema autorizado para DDL. | Indique literales como N'ia.Tabla' y, para propiedades extendidas, @level0type = N'SCHEMA' con un esquema de DDL. |
| `POL-DDL-CONFIRM` | CONFIRMAR | Configurable | — | Medio | Funciones y esquemas de partición y objetos de texto completo requieren confirmación. | Revise el impacto antes de aprobar. |
| `POL-DDL-CREATE` | PERMITIR | Configurable | — | Medio | DDL de creación (CREATE / CREATE OR ALTER). | Sin acciones adicionales. |
| `POL-DDL-DESTRUCTIVE` | CONFIRMAR | Configurable | — | Alto | Modificación destructiva (DROP COLUMN, ALTER COLUMN, desactivar SYSTEM_VERSIONING, deshabilitar índices, SWITCH, sp_rename). | Revise el impacto en los datos y en los objetos dependientes. |
| `POL-DDL-DROP` | CONFIRMAR | Configurable | — | Alto | DDL destructivo: DROP de objetos. | Revise los objetos dependientes; la operación requiere aprobación. |
| `POL-DDL-ENC` | BLOQUEAR | Inmutable | Advertencia | Medio | WITH ENCRYPTION está prohibido en módulos: impide auditar el código. | Elimine la opción WITH ENCRYPTION. |
| `POL-DDL-EXTPROP` | PERMITIR | Configurable | — | Bajo | Propiedades extendidas (sp_addextendedproperty, sp_updateextendedproperty, sp_dropextendedproperty). | Sin acciones adicionales. |
| `POL-DDL-NOSCHEMA` | BLOQUEAR | Inmutable | — | Medio | El objeto DDL debe indicar el esquema explícitamente. | Califique el nombre con un esquema autorizado para DDL (por ejemplo ia.MiTabla). |
| `POL-DDL-OWNER` | BLOQUEAR | Inmutable | Crítica | Crítico | CREATE SCHEMA … AUTHORIZATION solo se admite hacia un propietario configurado; no se transfiere propiedad a otros principales. | Omita la cláusula AUTHORIZATION. |
| `POL-DDL-SCHEMA` | BLOQUEAR | Inmutable | Advertencia | Medio | El esquema destino no está autorizado para DDL en este perfil. | Cree o modifique objetos solo en los esquemas de DDL del perfil (consulte get_server_policy). |
| `POL-DDL-STATS` | PERMITIR | Configurable | — | Bajo | Gestión de estadísticas (CREATE, UPDATE, DROP STATISTICS). | Sin acciones adicionales. |
| `POL-DDL-TEMP` | PERMITIR | Configurable | — | Bajo | DDL sobre tablas temporales de sesión (#tabla). | Sin acciones adicionales. |
| `POL-DDL-TRIGGER` | CONFIRMAR | Configurable | — | Alto | Creación, modificación, eliminación, habilitación o deshabilitación de triggers DML. | Revise la lógica del trigger antes de aprobar. |
| `POL-DDL-TRUNCATE` | CONFIRMAR | Configurable | — | Alto | DDL destructivo: TRUNCATE TABLE elimina todas las filas. | Confirme que desea vaciar la tabla. |

## DML

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-DML-001` | BLOQUEAR | Configurable | Advertencia | Alto | UPDATE o DELETE sin cláusula WHERE. | Agregue una cláusula WHERE que limite las filas afectadas. |
| `POL-DML-002` | BLOQUEAR | Configurable | Advertencia | Alto | UPDATE o DELETE con un predicado trivialmente verdadero o independiente de las filas (por ejemplo WHERE 1=1, 'a'='a', col = col, OR con constante verdadera): se trata como ausencia de WHERE. | Agregue una cláusula WHERE real que limite las filas afectadas. |
| `POL-DML-ALWAYS` | CONFIRMAR | Configurable | — | Medio | El perfil exige confirmación humana para todo UPDATE, DELETE y MERGE. | Espere la aprobación del usuario. |
| `POL-DML-DEL` | PERMITIR | Configurable | — | Medio | DELETE con filtro permitido con los límites del perfil. | Sin acciones adicionales. |
| `POL-DML-INS` | PERMITIR | Configurable | — | Medio | INSERT permitido con los límites del perfil. | Sin acciones adicionales. |
| `POL-DML-MRG` | PERMITIR | Configurable | — | Medio | MERGE permitido con los límites del perfil. | Sin acciones adicionales. |
| `POL-DML-UPD` | PERMITIR | Configurable | — | Medio | UPDATE con filtro permitido con los límites del perfil. | Sin acciones adicionales. |

## Allowlist de herramienta

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-DML-COMPOSABLE` | BLOQUEAR | Inmutable | Advertencia | Alto | DML anidado en la cláusula FROM (DML componible con OUTPUT) no está permitido. | Ejecute la modificación como una sentencia DML independiente con execute_dml. |
| `POL-DML-EXEC` | BLOQUEAR | Inmutable | — | Medio | INSERT … EXEC no está permitido en texto libre: oculta la ejecución de un procedimiento. | Ejecute el procedimiento con execute_procedure. |
| `POL-TOOL-001` | BLOQUEAR | Inmutable | — | Bajo | El tipo de sentencia no está permitido en esta herramienta. | Use la herramienta adecuada para el tipo de sentencia. |
| `POL-TOOL-002` | BLOQUEAR | Inmutable | — | Medio | SELECT … INTO crea una tabla nueva y no está permitido en las herramientas de consulta. | Use un SELECT sin INTO; para crear tablas use execute_ddl con CREATE TABLE. |

## SQL dinámico

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-DYN-001` | BLOQUEAR | Configurable | Advertencia | Alto | El SQL dinámico (EXEC('…'), EXEC(@sql), sp_executesql) está bloqueado en este perfil. | Escriba la sentencia directamente, sin SQL dinámico. |
| `POL-DYN-002` | CONFIRMAR | Configurable | — | Alto | SQL dinámico con literal analizado: requiere confirmación. | Si es posible, escriba la sentencia directamente sin SQL dinámico. |
| `POL-DYN-003` | BLOQUEAR | Inmutable | Advertencia | Alto | SQL dinámico no analizable (variables, expresiones, EXEC @procedimiento o literal inválido): se bloquea siempre. | Use solo literales de texto completos, o escriba la sentencia directamente. |

## Acceso externo y sistema operativo

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-EXT-001` | BLOQUEAR | Inmutable | Crítica | Crítico | El acceso a datos o servicios externos (OPENROWSET, OPENDATASOURCE, OPENQUERY, BULK INSERT, objetos EXTERNAL, llamadas a modelos o API externas) está prohibido. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-EXT-002` | BLOQUEAR | Inmutable | Crítica | Crítico | El uso de servidores vinculados (nombres de cuatro partes, EXEC … AT, procedimientos de servidores vinculados) está prohibido. | Use solo objetos de las bases de datos permitidas del perfil, con nombres de hasta tres partes. |
| `POL-EXT-003` | BLOQUEAR | Inmutable | Crítica | Crítico | Los procedimientos extendidos (xp_*), la automatización OLE (sp_OA*), los scripts externos y el código CLR están prohibidos. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-EXT-004` | BLOQUEAR | Inmutable | Crítica | Crítico | El correo de base de datos, los trabajos del Agente SQL y cualquier objeto de msdb están prohibidos. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-EXT-005` | BLOQUEAR | Inmutable | Crítica | Crítico | Los triggers DDL y LOGON, las notificaciones de eventos y los objetos y comandos de Service Broker están prohibidos. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |

## Suplantación de contexto

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-IMP-001` | BLOQUEAR | Inmutable | Crítica | Crítico | EXECUTE AS, REVERT y SETUSER están prohibidos. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-IMP-002` | BLOQUEAR | Inmutable | Crítica | Crítico | EXEC … AS LOGIN/USER (cambio de contexto de ejecución) está prohibido. | Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP. |
| `POL-IMP-003` | BLOQUEAR | Inmutable | Crítica | Crítico | La cláusula WITH EXECUTE AS (OWNER, SELF o un usuario) está prohibida en módulos. | Elimine la cláusula WITH EXECUTE AS o use EXECUTE AS CALLER. |

## Procedimientos almacenados

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-PRC-001` | PERMITIR | Configurable | — | Medio | Ejecución de un procedimiento autorizado. | Sin acciones adicionales. |
| `POL-PRC-CONFIRM` | CONFIRMAR | Configurable | — | Medio | El procedimiento requiere confirmación humana según la configuración del perfil. | Espere la aprobación del usuario. |
| `POL-PRC-DEF-001` | BLOQUEAR | Inmutable | Advertencia | Alto | No fue posible obtener y analizar la definición del procedimiento (o de uno que invoca): cifrado, CLR, sin permiso VIEW DEFINITION o anidamiento excesivo. Por seguridad no se ejecuta. | Conceda VIEW DEFINITION al login de lectura sobre el esquema del procedimiento o pida a un DBA que lo ejecute fuera del MCP. |
| `POL-PRC-DENY` | BLOQUEAR | Inmutable | Advertencia | Medio | El procedimiento está en la lista de procedimientos denegados del perfil. | Este procedimiento no se puede ejecutar a través del MCP. |
| `POL-PRC-INVALID` | BLOQUEAR | Inmutable | — | Bajo | El nombre del procedimiento no es un identificador válido. | Indique el procedimiento como esquema.nombre (máximo 128 caracteres por parte). |
| `POL-PRC-NOTALLOWED` | BLOQUEAR | Inmutable | — | Medio | El procedimiento no está en la lista de procedimientos permitidos del perfil. | Consulte get_server_policy para ver los procedimientos permitidos. |
| `POL-PRC-SYS` | BLOQUEAR | Inmutable | Advertencia | Alto | Los procedimientos del sistema (sp_*, xp_*) están bloqueados, salvo sp_help, sp_helptext y sp_describe_first_result_set. | Use las herramientas de metadatos del MCP en lugar de procedimientos del sistema. |
| `POL-PRC-SYS-RO` | PERMITIR | Configurable | — | Bajo | Procedimiento del sistema de solo lectura incluido en la lista de excepciones. | Sin acciones adicionales. |

## Consulta

| Regla | Acción por defecto | Tipo | Alerta | Riesgo | Motivo | Sugerencia |
|---|---|---|---|---|---|---|
| `POL-QRY-001` | PERMITIR | Configurable | — | Bajo | Consulta de lectura (SELECT). | Sin acciones adicionales. |

