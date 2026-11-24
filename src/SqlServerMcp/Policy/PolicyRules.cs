using System.Reflection;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Policy;

/// <summary>
/// Fase de evaluación de una regla. Define el orden de evaluación (y, por tanto, la regla determinante
/// en caso de empate de acción): primero la estructura, luego las reglas inmutables críticas, las de
/// sentencia, la allowlist de la herramienta, la forma (DML sin filtro), las referencias y por último las
/// configurables (RS-POL-11: decisiones deterministas y explicables).
/// </summary>
internal enum HitPhase
{
    Structural = 0,
    Critical = 1,
    Statement = 2,
    Tool = 3,
    Shape = 4,
    Reference = 5,
    Configurable = 6,
    Runtime = 7,
}

/// <summary>Definición estática de una regla del motor (RNF-07: reglas definidas como datos; RNF-11: catálogo).</summary>
/// <param name="Id">Identificador estable de la regla.</param>
/// <param name="Group">Categoría legible (español).</param>
/// <param name="Action">Acción por defecto.</param>
/// <param name="Configurable">True si la acción proviene de la configuración del perfil (con piso mínimo).</param>
/// <param name="Severity">Severidad de alerta asociada (null = sin alerta).</param>
/// <param name="Risk">Nivel de riesgo que aporta la regla.</param>
/// <param name="Phase">Fase de evaluación.</param>
/// <param name="Message">Motivo (español) que se devuelve al agente.</param>
/// <param name="Suggestion">Sugerencia accionable (RF-OUT-03).</param>
/// <param name="ErrorCode">Código de error específico (null = POLICY_BLOCKED cuando bloquea).</param>
internal sealed record PolicyRule(
    string Id,
    string Group,
    PolicyAction Action,
    bool Configurable,
    AlertSeverity? Severity,
    RiskLevel Risk,
    HitPhase Phase,
    string Message,
    string Suggestion,
    string? ErrorCode = null)
{
    /// <summary>Las reglas no configurables son inmutables (no pueden relajarse, RS-POL-10).</summary>
    public bool Immutable => !Configurable;
}

/// <summary>
/// Catálogo de reglas del motor de políticas (sección 3, RS-POL, RS-DCL, RS-DAT, Anexo C).
/// El documento docs/POLICY_RULES.md describe estas mismas reglas.
/// </summary>
internal static class PolicyRules
{
    private const string Estructura = "Estructura y análisis";
    private const string Dcl = "Control de acceso (DCL)";
    private const string Suplantacion = "Suplantación de contexto";
    private const string Administracion = "Administración de BD e instancia";
    private const string Externo = "Acceso externo y sistema operativo";
    private const string Sesion = "Transacciones, sesión y contexto";
    private const string Dinamico = "SQL dinámico";
    private const string Referencias = "Referencias y catálogo";
    private const string Datos = "Protección de datos";
    private const string Herramienta = "Allowlist de herramienta";
    private const string Dml = "DML";
    private const string Ddl = "DDL";
    private const string Consulta = "Consulta";
    private const string Procedimientos = "Procedimientos almacenados";
    private const string Limites = "Límites en tiempo de ejecución";

    private const string SugerenciaInmutable = "Esta operación está prohibida por política inmutable; ninguna configuración la habilita. Si es necesaria, debe realizarla un DBA fuera del MCP.";

    // ---------------------------------------------------------------- Estructura (RS-POL-01/02/03, RS-INJ-02)
    public static readonly PolicyRule SizeExceeded = new("POL-SIZE-001", Estructura, PolicyAction.Block, false, null, RiskLevel.Low, HitPhase.Structural,
        "La sentencia supera el tamaño máximo permitido por el perfil.",
        "Reduzca la sentencia o divídala en operaciones más pequeñas.", Infrastructure.ErrorCodes.LimitExceeded);

    public static readonly PolicyRule ParseError = new("POL-PARSE-001", Estructura, PolicyAction.Block, false, null, RiskLevel.Low, HitPhase.Structural,
        "La sentencia no se pudo analizar: error de sintaxis T-SQL.",
        "Corrija la sintaxis indicada y vuelva a intentarlo; puede usar validate_sql para comprobarla.");

    public static readonly PolicyRule EmptyStatement = new("POL-PARSE-002", Estructura, PolicyAction.Block, false, null, RiskLevel.Low, HitPhase.Structural,
        "La sentencia está vacía o solo contiene comentarios.",
        "Envíe exactamente una sentencia T-SQL.");

    public static readonly PolicyRule TooComplex = new("POL-PARSE-003", Estructura, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Structural,
        "La sentencia excede la profundidad de anidamiento o la complejidad máxima admitida.",
        "Simplifique la sentencia (menos paréntesis, subconsultas o expresiones encadenadas).");

    public static readonly PolicyRule MultipleStatements = new("POL-BATCH-001", Estructura, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Structural,
        "Se admite exactamente una sentencia por invocación.",
        "Envíe una sola sentencia por llamada, sin ';' adicionales ni separadores GO.");

    public static readonly PolicyRule InternalError = new("POL-INTERNAL-001", Estructura, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Structural,
        "No se pudo completar el análisis de la sentencia; se rechaza por seguridad (fail-closed).",
        "Simplifique la sentencia y vuelva a intentarlo.");

    public static readonly PolicyRule UnknownStatement = new("POL-UNKNOWN-001", Estructura, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "Tipo de sentencia no reconocido o no admitido por la política.",
        "Use solo SELECT, DML (INSERT, UPDATE, DELETE, MERGE) o DDL sobre objetos permitidos. Consulte get_server_policy.");

    // ---------------------------------------------------------------- DCL (RS-DCL-01..03, sección 5.4)
    public static readonly PolicyRule DclPermissions = new("POL-DCL-001", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Las sentencias GRANT, DENY y REVOKE están prohibidas.", SugerenciaInmutable);

    public static readonly PolicyRule DclLoginsUsers = new("POL-DCL-002", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "La gestión de logins y usuarios está prohibida.", SugerenciaInmutable);

    public static readonly PolicyRule DclRoles = new("POL-DCL-003", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "La gestión de roles y de sus miembros está prohibida.", SugerenciaInmutable);

    public static readonly PolicyRule DclOwnership = new("POL-DCL-004", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "El cambio de propiedad de objetos o bases de datos está prohibido.", SugerenciaInmutable);

    public static readonly PolicyRule DclCrypto = new("POL-DCL-005", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "La gestión de certificados, claves, credenciales y firmas está prohibida.", SugerenciaInmutable);

    public static readonly PolicyRule DclGovernance = new("POL-DCL-006", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "La gestión de políticas de seguridad, clasificación de sensibilidad, enmascaramiento dinámico y auditoría está prohibida.", SugerenciaInmutable);

    public static readonly PolicyRule DclDatabaseSecurity = new("POL-DCL-007", Dcl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Cambiar TRUSTWORTHY, DB_CHAINING o CONTAINMENT de una base de datos está prohibido.", SugerenciaInmutable);

    // ---------------------------------------------------------------- Suplantación
    public static readonly PolicyRule ImpersonationStatement = new("POL-IMP-001", Suplantacion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "EXECUTE AS, REVERT y SETUSER están prohibidos.", SugerenciaInmutable);

    public static readonly PolicyRule ImpersonationExecContext = new("POL-IMP-002", Suplantacion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "EXEC … AS LOGIN/USER (cambio de contexto de ejecución) está prohibido.", SugerenciaInmutable);

    public static readonly PolicyRule ImpersonationModule = new("POL-IMP-003", Suplantacion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "La cláusula WITH EXECUTE AS (OWNER, SELF o un usuario) está prohibida en módulos.",
        "Elimine la cláusula WITH EXECUTE AS o use EXECUTE AS CALLER.");

    // ---------------------------------------------------------------- Administración
    public static readonly PolicyRule AdminDatabase = new("POL-ADM-001", Administracion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Crear, modificar o eliminar bases de datos está prohibido.", SugerenciaInmutable);

    public static readonly PolicyRule AdminBackupRestore = new("POL-ADM-002", Administracion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "BACKUP y RESTORE están prohibidos.", SugerenciaInmutable);

    public static readonly PolicyRule AdminDbcc = new("POL-ADM-003", Administracion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Los comandos DBCC están prohibidos.", SugerenciaInmutable);

    public static readonly PolicyRule AdminInstance = new("POL-ADM-004", Administracion, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "La administración de la instancia (SHUTDOWN, KILL, CHECKPOINT, RECONFIGURE, sp_configure, configuración del servidor, endpoints, grupos de disponibilidad, Resource Governor, sesiones de eventos) está prohibida.",
        SugerenciaInmutable);

    // ---------------------------------------------------------------- Acceso externo (Anexo C)
    public static readonly PolicyRule ExternalData = new("POL-EXT-001", Externo, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "El acceso a datos o servicios externos (OPENROWSET, OPENDATASOURCE, OPENQUERY, BULK INSERT, objetos EXTERNAL, llamadas a modelos o API externas) está prohibido.",
        SugerenciaInmutable);

    public static readonly PolicyRule ExternalLinkedServer = new("POL-EXT-002", Externo, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "El uso de servidores vinculados (nombres de cuatro partes, EXEC … AT, procedimientos de servidores vinculados) está prohibido.",
        "Use solo objetos de las bases de datos permitidas del perfil, con nombres de hasta tres partes.");

    public static readonly PolicyRule ExternalOs = new("POL-EXT-003", Externo, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Los procedimientos extendidos (xp_*), la automatización OLE (sp_OA*), los scripts externos y el código CLR están prohibidos.",
        SugerenciaInmutable);

    public static readonly PolicyRule ExternalMailJobs = new("POL-EXT-004", Externo, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "El correo de base de datos, los trabajos del Agente SQL y cualquier objeto de msdb están prohibidos.",
        SugerenciaInmutable);

    public static readonly PolicyRule ExternalServerObjects = new("POL-EXT-005", Externo, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Los triggers DDL y LOGON, las notificaciones de eventos y los objetos y comandos de Service Broker están prohibidos.",
        SugerenciaInmutable);

    // ---------------------------------------------------------------- Sesión, transacciones, contexto, esperas
    public static readonly PolicyRule TransactionControl = new("POL-TCL-001", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "El control de transacciones en texto libre (BEGIN/COMMIT/ROLLBACK/SAVE TRAN, SET IMPLICIT_TRANSACTIONS) está prohibido: las transacciones las gestiona el MCP.",
        "Use las herramientas begin_transaction, commit_transaction y rollback_transaction.");

    public static readonly PolicyRule DistributedTransaction = new("POL-TCL-002", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Statement,
        "Las transacciones distribuidas están prohibidas.",
        "Use transacciones locales gestionadas por el MCP.");

    public static readonly PolicyRule SessionOption = new("POL-SET-001", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "Las sentencias SET que modifican el estado de la sesión están prohibidas (las opciones de sesión las fija el MCP).",
        "Elimine la sentencia SET; los límites, el aislamiento y el plan de ejecución se controlan con las herramientas del MCP.");

    public static readonly PolicyRule QuotedIdentifier = new("POL-SET-002", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "SET QUOTED_IDENTIFIER está prohibido en cualquier posición (altera la interpretación de identificadores).",
        "Elimine SET QUOTED_IDENTIFIER y delimite identificadores con corchetes.");

    public static readonly PolicyRule UseDatabase = new("POL-CTX-001", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "USE está prohibido: la base de datos se fija en la conexión del perfil.",
        "Use nombres de tres partes hacia bases de datos permitidas o cambie de perfil.");

    public static readonly PolicyRule WaitFor = new("POL-WAIT-001", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "WAITFOR está prohibido.",
        "Elimine WAITFOR; las esperas prolongadas consumen recursos del servidor.");

    public static readonly PolicyRule ControlFlowAtRoot = new("POL-PROC-001", Sesion, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Statement,
        "Los bloques de control de flujo, variables y cursores (BEGIN…END, IF, WHILE, DECLARE, TRY…CATCH, PRINT, THROW…) solo se admiten dentro del cuerpo de un módulo.",
        "Envíe una única sentencia SELECT, DML o DDL; para lógica procedimental cree un procedimiento con execute_ddl.");

    // ---------------------------------------------------------------- SQL dinámico (sección 3)
    public static readonly PolicyRule DynamicSqlBlocked = new("POL-DYN-001", Dinamico, PolicyAction.Block, true, AlertSeverity.Warning, RiskLevel.High, HitPhase.Statement,
        "El SQL dinámico (EXEC('…'), EXEC(@sql), sp_executesql) está bloqueado en este perfil.",
        "Escriba la sentencia directamente, sin SQL dinámico.");

    public static readonly PolicyRule DynamicSqlLiteral = new("POL-DYN-002", Dinamico, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Configurable,
        "SQL dinámico con literal analizado: requiere confirmación.",
        "Si es posible, escriba la sentencia directamente sin SQL dinámico.");

    public static readonly PolicyRule DynamicSqlOpaque = new("POL-DYN-003", Dinamico, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Statement,
        "SQL dinámico no analizable (variables, expresiones, EXEC @procedimiento o literal inválido): se bloquea siempre.",
        "Use solo literales de texto completos, o escriba la sentencia directamente.");

    // ---------------------------------------------------------------- Catálogo y referencias (RS-POL-07/08)
    public static readonly PolicyRule SensitiveCatalog = new("POL-CAT-001", Referencias, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Statement,
        "Acceso a una vista o función de catálogo sensible (logins, credenciales, claves, auditoría o registro de transacciones).",
        "Esta información no está disponible a través del MCP.");

    public static readonly PolicyRule DatabaseNotAllowed = new("POL-REF-001", Referencias, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Reference,
        "Referencia a una base de datos que no está en la lista permitida del perfil.",
        "Use solo las bases de datos permitidas (consulte get_server_policy o list_databases).");

    public static readonly PolicyRule SystemDatabase = new("POL-REF-002", Referencias, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Reference,
        "Referencia a una base de datos del sistema o a una tabla temporal global (##): no permitida.",
        "Use objetos de las bases de datos permitidas; tempdb solo para tablas temporales de sesión (#tabla).");

    public static readonly PolicyRule SchemaNotAllowed = new("POL-REF-003", Referencias, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Reference,
        "Referencia a un esquema no permitido por el perfil.",
        "Califique los objetos con un esquema permitido (los nombres sin esquema se interpretan como dbo); consulte list_schemas.");

    public static readonly PolicyRule SystemCatalog = new("POL-REF-004", Referencias, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Reference,
        "Las consultas a vistas y funciones de catálogo del sistema (sys, INFORMATION_SCHEMA) no están habilitadas en este perfil.",
        "Use las herramientas de metadatos (list_tables, describe_table, list_routines, get_dependencies…).");

    // ---------------------------------------------------------------- Sinónimos
    public static readonly PolicyRule SynonymTarget = new("POL-SYN-001", Referencias, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Critical,
        "Un sinónimo solo puede apuntar a tablas, vistas, procedimientos o funciones de usuario de las bases y esquemas permitidos del perfil; no a objetos del sistema (sp_*, xp_*, sys, INFORMATION_SCHEMA, master, msdb, tempdb, model, la base de recursos), a otros sinónimos ni a objetos inexistentes.",
        SugerenciaInmutable);

    public static readonly PolicyRule SynonymUnresolved = new("POL-SYN-002", Referencias, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Critical,
        "No fue posible resolver a qué objeto apunta un sinónimo referenciado; por seguridad la sentencia se bloquea.",
        "Use el nombre del objeto base en lugar del sinónimo, o reintente si la base de datos no estaba disponible.");

    public static readonly PolicyRule ModuleDefinitionUnavailable = new("POL-PRC-DEF-001", Procedimientos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Critical,
        "No fue posible obtener y analizar la definición del procedimiento (o de uno que invoca): cifrado, CLR, sin permiso VIEW DEFINITION o anidamiento excesivo. Por seguridad no se ejecuta.",
        "Conceda VIEW DEFINITION al login de lectura sobre el esquema del procedimiento o pida a un DBA que lo ejecute fuera del MCP.");

    // ---------------------------------------------------------------- Datos (RS-DAT-01/02/06)
    public static readonly PolicyRule ExcludedObject = new("POL-DAT-001", Datos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "Referencia a un esquema, tabla o vista excluido por la configuración.",
        "Este objeto no está disponible a través del MCP.");

    public static readonly PolicyRule ExcludedColumn = new("POL-DAT-002", Datos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "Referencia a una columna excluida por la configuración.",
        "Elimine la columna de la sentencia; no está disponible a través del MCP.");

    public static readonly PolicyRule SensitiveColumnBlocked = new("POL-DAT-003", Datos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "Referencia a una columna sensible: el perfil bloquea su uso.",
        "Elimine la columna sensible de la sentencia.");

    public static readonly PolicyRule SensitiveColumnInference = new("POL-DAT-004", Datos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "Una columna sensible solo puede aparecer sin transformar en la lista SELECT principal (se enmascara); su uso en filtros, uniones, agrupaciones, ordenamientos, funciones, escrituras o subconsultas permitiría inferir su valor.",
        "Elimine la columna sensible de esa posición; si la necesita en el resultado, selecciónela directamente para que se enmascare.");

    public static readonly PolicyRule SensitiveColumnNotMaskable = new("POL-DAT-005", Datos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "No se puede garantizar la protección de columnas sensibles o excluidas en el resultado: columna sensible con alias en posición indeterminada, FOR JSON/XML, o SELECT * cuyo resultado se renombra, se combina o se reutiliza (UNION, lista de columnas de una CTE o tabla derivada, INSERT … SELECT, subconsultas, cursores).",
        "Enumere explícitamente solo las columnas necesarias, sin '*' y sin alias sobre columnas sensibles, y evite FOR JSON/XML con columnas sensibles.");

    public static readonly PolicyRule SensitiveColumnOutput = new("POL-DAT-006", Datos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "Una columna sensible no puede devolverse en la cláusula OUTPUT.",
        "Elimine la columna sensible de la cláusula OUTPUT.");

    // ---------------------------------------------------------------- Allowlist por herramienta (RS-POL-04)
    public static readonly PolicyRule ToolNotAllowed = new("POL-TOOL-001", Herramienta, PolicyAction.Block, false, null, RiskLevel.Low, HitPhase.Tool,
        "El tipo de sentencia no está permitido en esta herramienta.",
        "Use la herramienta adecuada para el tipo de sentencia.");

    public static readonly PolicyRule SelectIntoInQuery = new("POL-TOOL-002", Herramienta, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Tool,
        "SELECT … INTO crea una tabla nueva y no está permitido en las herramientas de consulta.",
        "Use un SELECT sin INTO; para crear tablas use execute_ddl con CREATE TABLE.");

    public static readonly PolicyRule InsertExec = new("POL-DML-EXEC", Herramienta, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Tool,
        "INSERT … EXEC no está permitido en texto libre: oculta la ejecución de un procedimiento.",
        "Ejecute el procedimiento con execute_procedure.");

    public static readonly PolicyRule ComposableDml = new("POL-DML-COMPOSABLE", Herramienta, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Statement,
        "DML anidado en la cláusula FROM (DML componible con OUTPUT) no está permitido.",
        "Ejecute la modificación como una sentencia DML independiente con execute_dml.");

    // ---------------------------------------------------------------- Consulta y DML (sección 3, RF-DML-02, RS-POL-09)
    public static readonly PolicyRule Query = new("POL-QRY-001", Consulta, PolicyAction.Allow, true, null, RiskLevel.Low, HitPhase.Configurable,
        "Consulta de lectura (SELECT).", "Sin acciones adicionales.");

    public static readonly PolicyRule Insert = new("POL-DML-INS", Dml, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "INSERT permitido con los límites del perfil.", "Sin acciones adicionales.");

    public static readonly PolicyRule Update = new("POL-DML-UPD", Dml, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "UPDATE con filtro permitido con los límites del perfil.", "Sin acciones adicionales.");

    public static readonly PolicyRule Delete = new("POL-DML-DEL", Dml, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "DELETE con filtro permitido con los límites del perfil.", "Sin acciones adicionales.");

    public static readonly PolicyRule Merge = new("POL-DML-MRG", Dml, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "MERGE permitido con los límites del perfil.", "Sin acciones adicionales.");

    public static readonly PolicyRule AlwaysConfirmDml = new("POL-DML-ALWAYS", Dml, PolicyAction.Confirm, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "El perfil exige confirmación humana para todo UPDATE, DELETE y MERGE.", "Espere la aprobación del usuario.");

    public static readonly PolicyRule UnfilteredDml = new("POL-DML-001", Dml, PolicyAction.Block, true, AlertSeverity.Warning, RiskLevel.High, HitPhase.Shape,
        "UPDATE o DELETE sin cláusula WHERE.",
        "Agregue una cláusula WHERE que limite las filas afectadas.");

    public static readonly PolicyRule TrivialPredicate = new("POL-DML-002", Dml, PolicyAction.Block, true, AlertSeverity.Warning, RiskLevel.High, HitPhase.Shape,
        "UPDATE o DELETE con un predicado trivialmente verdadero o independiente de las filas (por ejemplo WHERE 1=1, 'a'='a', col = col, OR con constante verdadera): se trata como ausencia de WHERE.",
        "Agregue una cláusula WHERE real que limite las filas afectadas.");

    // ---------------------------------------------------------------- DDL (RF-DDL, sección 3)
    public static readonly PolicyRule DdlCreate = new("POL-DDL-CREATE", Ddl, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "DDL de creación (CREATE / CREATE OR ALTER).", "Sin acciones adicionales.");

    public static readonly PolicyRule DdlAlter = new("POL-DDL-ALTER", Ddl, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "DDL de modificación no destructiva (ALTER).", "Sin acciones adicionales.");

    public static readonly PolicyRule DdlDrop = new("POL-DDL-DROP", Ddl, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Configurable,
        "DDL destructivo: DROP de objetos.", "Revise los objetos dependientes; la operación requiere aprobación.");

    public static readonly PolicyRule DdlTruncate = new("POL-DDL-TRUNCATE", Ddl, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Configurable,
        "DDL destructivo: TRUNCATE TABLE elimina todas las filas.", "Confirme que desea vaciar la tabla.");

    public static readonly PolicyRule DdlDestructiveAlter = new("POL-DDL-DESTRUCTIVE", Ddl, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Configurable,
        "Modificación destructiva (DROP COLUMN, ALTER COLUMN, desactivar SYSTEM_VERSIONING, deshabilitar índices, SWITCH, sp_rename).",
        "Revise el impacto en los datos y en los objetos dependientes.");

    public static readonly PolicyRule DdlTrigger = new("POL-DDL-TRIGGER", Ddl, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Configurable,
        "Creación, modificación, eliminación, habilitación o deshabilitación de triggers DML.", "Revise la lógica del trigger antes de aprobar.");

    public static readonly PolicyRule DdlStatistics = new("POL-DDL-STATS", Ddl, PolicyAction.Allow, true, null, RiskLevel.Low, HitPhase.Configurable,
        "Gestión de estadísticas (CREATE, UPDATE, DROP STATISTICS).", "Sin acciones adicionales.");

    public static readonly PolicyRule DdlExtendedProperty = new("POL-DDL-EXTPROP", Ddl, PolicyAction.Allow, true, null, RiskLevel.Low, HitPhase.Configurable,
        "Propiedades extendidas (sp_addextendedproperty, sp_updateextendedproperty, sp_dropextendedproperty).", "Sin acciones adicionales.");

    public static readonly PolicyRule DdlTempObject = new("POL-DDL-TEMP", Ddl, PolicyAction.Allow, true, null, RiskLevel.Low, HitPhase.Configurable,
        "DDL sobre tablas temporales de sesión (#tabla).", "Sin acciones adicionales.");

    public static readonly PolicyRule DdlConfirmObjects = new("POL-DDL-CONFIRM", Ddl, PolicyAction.Confirm, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "Funciones y esquemas de partición y objetos de texto completo requieren confirmación.", "Revise el impacto antes de aprobar.");

    public static readonly PolicyRule DdlNoSchema = new("POL-DDL-NOSCHEMA", Ddl, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Reference,
        "El objeto DDL debe indicar el esquema explícitamente.",
        "Califique el nombre con un esquema autorizado para DDL (por ejemplo ia.MiTabla).");

    public static readonly PolicyRule DdlSchemaNotAllowed = new("POL-DDL-SCHEMA", Ddl, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Reference,
        "El esquema destino no está autorizado para DDL en este perfil.",
        "Cree o modifique objetos solo en los esquemas de DDL del perfil (consulte get_server_policy).");

    public static readonly PolicyRule DdlSchemaOwner = new("POL-DDL-OWNER", Ddl, PolicyAction.Block, false, AlertSeverity.Critical, RiskLevel.Critical, HitPhase.Reference,
        "CREATE SCHEMA … AUTHORIZATION solo se admite hacia un propietario configurado; no se transfiere propiedad a otros principales.",
        "Omita la cláusula AUTHORIZATION.");

    public static readonly PolicyRule DdlEncryption = new("POL-DDL-ENC", Ddl, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Reference,
        "WITH ENCRYPTION está prohibido en módulos: impide auditar el código.",
        "Elimine la opción WITH ENCRYPTION.");

    public static readonly PolicyRule DdlArguments = new("POL-DDL-ARGS", Ddl, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Reference,
        "Los argumentos de sp_rename o de los procedimientos de propiedades extendidas deben ser literales válidos sobre objetos de un esquema autorizado para DDL.",
        "Indique literales como N'ia.Tabla' y, para propiedades extendidas, @level0type = N'SCHEMA' con un esquema de DDL.");

    // ---------------------------------------------------------------- Procedimientos (RF-PRC-02/03)
    public static readonly PolicyRule ProcedureAllowed = new("POL-PRC-001", Procedimientos, PolicyAction.Allow, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "Ejecución de un procedimiento autorizado.", "Sin acciones adicionales.");

    public static readonly PolicyRule ProcedureSystemReadOnly = new("POL-PRC-SYS-RO", Procedimientos, PolicyAction.Allow, true, null, RiskLevel.Low, HitPhase.Configurable,
        "Procedimiento del sistema de solo lectura incluido en la lista de excepciones.", "Sin acciones adicionales.");

    public static readonly PolicyRule ProcedureSystem = new("POL-PRC-SYS", Procedimientos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Reference,
        "Los procedimientos del sistema (sp_*, xp_*) están bloqueados, salvo sp_help, sp_helptext y sp_describe_first_result_set.",
        "Use las herramientas de metadatos del MCP en lugar de procedimientos del sistema.");

    public static readonly PolicyRule ProcedureDenied = new("POL-PRC-DENY", Procedimientos, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Reference,
        "El procedimiento está en la lista de procedimientos denegados del perfil.",
        "Este procedimiento no se puede ejecutar a través del MCP.");

    public static readonly PolicyRule ProcedureNotAllowed = new("POL-PRC-NOTALLOWED", Procedimientos, PolicyAction.Block, false, null, RiskLevel.Medium, HitPhase.Reference,
        "El procedimiento no está en la lista de procedimientos permitidos del perfil.",
        "Consulte get_server_policy para ver los procedimientos permitidos.");

    public static readonly PolicyRule ProcedureConfirm = new("POL-PRC-CONFIRM", Procedimientos, PolicyAction.Confirm, true, null, RiskLevel.Medium, HitPhase.Configurable,
        "El procedimiento requiere confirmación humana según la configuración del perfil.", "Espere la aprobación del usuario.");

    public static readonly PolicyRule ProcedureInvalidName = new("POL-PRC-INVALID", Procedimientos, PolicyAction.Block, false, null, RiskLevel.Low, HitPhase.Structural,
        "El nombre del procedimiento no es un identificador válido.",
        "Indique el procedimiento como esquema.nombre (máximo 128 caracteres por parte).");

    // ---------------------------------------------------------------- Tiempo de ejecución (RS-LIM, RS-POL-14, RF-DML-03/05)
    public static readonly PolicyRule MaxAffectedRows = new("POL-LIM-001", Limites, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Runtime,
        "Las filas afectadas estimadas superan el máximo permitido por el perfil.",
        "Agregue filtros para afectar menos filas o divida la operación en lotes.", Infrastructure.ErrorCodes.LimitExceeded);

    public static readonly PolicyRule DmlThreshold = new("POL-DML-THRESHOLD", Limites, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Runtime,
        "Las filas afectadas estimadas superan el umbral de confirmación del perfil.", "Espere la aprobación del usuario.");

    public static readonly PolicyRule TransactionThreshold = new("POL-DML-TX-THRESHOLD", Limites, PolicyAction.Confirm, true, null, RiskLevel.High, HitPhase.Runtime,
        "El acumulado de filas afectadas en la transacción explícita supera el umbral de confirmación del perfil.",
        "Espere la aprobación del usuario; dividir la operación en varias sentencias no evita el umbral.");

    public static readonly PolicyRule TransactionMaxAffectedRows = new("POL-LIM-TX", Limites, PolicyAction.Block, false, AlertSeverity.Warning, RiskLevel.High, HitPhase.Runtime,
        "El acumulado de filas afectadas en la transacción explícita supera el máximo permitido por el perfil.",
        "Confirme o revierta la transacción actual y divida el trabajo en transacciones más pequeñas.", Infrastructure.ErrorCodes.LimitExceeded);

    public static readonly PolicyRule EstimatedCost = new("POL-COST-001", Limites, PolicyAction.Confirm, true, AlertSeverity.Info, RiskLevel.Medium, HitPhase.Runtime,
        "El costo estimado del plan supera el umbral de confirmación del perfil.",
        "Agregue filtros o índices adecuados, o espere la aprobación del usuario.");

    public static readonly PolicyRule CartesianProduct = new("POL-LIM-CART", Limites, PolicyAction.Confirm, true, AlertSeverity.Info, RiskLevel.High, HitPhase.Runtime,
        "Producto cartesiano evidente (JOIN sin condición) sobre una tabla grande.",
        "Agregue la condición de unión (ON …) entre las tablas.");

    public static readonly PolicyRule LargeTableIndex = new("POL-DDL-IDX-LARGE", Limites, PolicyAction.Alert, true, AlertSeverity.Warning, RiskLevel.Medium, HitPhase.Runtime,
        "Creación de un índice sobre una tabla grande.",
        "Considere ONLINE = ON y ejecutarlo en una ventana de mantenimiento.");

    private static readonly Lazy<IReadOnlyList<PolicyRule>> AllRules = new(() =>
        typeof(PolicyRules).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(PolicyRule))
            .Select(f => (PolicyRule)f.GetValue(null)!)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList());

    /// <summary>Todas las reglas del catálogo, ordenadas por identificador.</summary>
    public static IReadOnlyList<PolicyRule> All => AllRules.Value;
}
