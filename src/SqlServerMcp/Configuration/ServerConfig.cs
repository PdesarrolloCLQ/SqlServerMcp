namespace SqlServerMcp.Configuration;

/// <summary>
/// Raíz del archivo de configuración (RF-CFG-01). Se deserializa de forma estricta:
/// cualquier propiedad desconocida invalida el arranque.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Referencia opcional al JSON Schema para editores ("$schema"); no afecta el comportamiento.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("$schema")]
    public string? SchemaReference { get; set; }

    /// <summary>Transporte MCP. Solo se admite "stdio" (RS-LOC-01).</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Perfil usado cuando el agente no indica uno.</summary>
    public string? DefaultProfile { get; set; }

    /// <summary>Perfiles de conexión con nombre (RF-CON-03).</summary>
    public Dictionary<string, ProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Máximos absolutos definidos por el administrador; ningún perfil puede superarlos (RS-LIM-01).</summary>
    public LimitsConfig AbsoluteLimits { get; set; } = LimitsConfig.AbsoluteDefaults();

    public AuditConfig Audit { get; set; } = new();

    public AlertsConfig Alerts { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Idioma de los mensajes ("es" por defecto, "en" opcional). Los códigos siempre en inglés (RF-OUT-05).</summary>
    public string Language { get; set; } = "es";
}

public enum OperationMode
{
    /// <summary>LECTURA: metadatos, consultas, planes y validación.</summary>
    Lectura = 0,

    /// <summary>ESCRITURA_DATOS: LECTURA + DML, insert_rows, procedimientos y transacciones.</summary>
    EscrituraDatos = 1,

    /// <summary>COMPLETO: ESCRITURA_DATOS + DDL.</summary>
    Completo = 2,
}

public enum ProfileEnvironment
{
    Development = 0,
    Test = 1,
    Production = 2,
}

public enum PolicyAction
{
    Allow = 0,
    Alert = 1,
    Confirm = 2,
    Block = 3,
}

public enum AuthenticationKind
{
    WindowsIntegrated = 0,
    SqlPassword = 1,
    EntraManagedIdentity = 2,
    EntraServicePrincipal = 3,
    EntraInteractive = 4,
    EntraDefault = 5,
}

public enum EncryptMode
{
    Mandatory = 0,
    Strict = 1,
    Optional = 2,
}

public enum ApplicationIntentKind
{
    ReadWrite = 0,
    ReadOnly = 1,
}

public enum SensitiveColumnAction
{
    Mask = 0,
    Block = 1,
}

public enum QueryIsolationLevel
{
    ReadCommitted = 0,
    Snapshot = 1,
    ReadUncommitted = 2,
    RepeatableRead = 3,
    Serializable = 4,
}

public enum ConfirmationUnavailableAction
{
    /// <summary>Rechazar con CONFIRMATION_UNAVAILABLE (por defecto, RS-HIL-05).</summary>
    Reject = 0,

    /// <summary>Permitir sin confirmación y generar alerta. Solo válido en perfiles Development.</summary>
    AllowWithAlert = 1,
}

public sealed class ProfileConfig
{
    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    /// <summary>Modo de operación; LECTURA por defecto (RF-CFG-04).</summary>
    public OperationMode Mode { get; set; } = OperationMode.Lectura;

    public ProfileEnvironment Environment { get; set; } = ProfileEnvironment.Development;

    /// <summary>Conexión principal; se usa con el login de lectura (RF-CON-07).</summary>
    public ConnectionConfig Connection { get; set; } = new();

    /// <summary>
    /// Conexión opcional para escritura (login de escritor). Solo se indican las propiedades
    /// que cambian (autenticación, usuario, credencial); servidor y base se heredan.
    /// </summary>
    public WriteConnectionConfig? WriteConnection { get; set; }

    /// <summary>Bases de datos permitidas (RF-CON-05). Vacío = solo la base inicial.</summary>
    public List<string> AllowedDatabases { get; set; } = [];

    /// <summary>Esquemas visibles y utilizables. Vacío = ["dbo"] (denegar por defecto).</summary>
    public List<string> AllowedSchemas { get; set; } = [];

    /// <summary>Esquemas en los que se permite DDL (RF-DDL-04). Vacío = ninguno.</summary>
    public List<string> DdlSchemas { get; set; } = [];

    public ExclusionConfig Excluded { get; set; } = new();

    public SensitiveColumnsConfig Sensitive { get; set; } = new();

    /// <summary>Datos personales (correo, teléfono, documento…): enmascarados por defecto.</summary>
    public PiiColumnsConfig Pii { get; set; } = new();

    public LimitsConfig Limits { get; set; } = new();

    public PolicyConfig Policy { get; set; } = new();

    /// <summary>Procedimientos permitidos (RF-PRC-03). Admite comodines "*" en el nombre.</summary>
    public List<ProcedureRuleConfig> Procedures { get; set; } = [];

    /// <summary>Procedimientos denegados explícitamente (prevalecen sobre la lista de permitidos).</summary>
    public List<string> DeniedProcedures { get; set; } = [];

    /// <summary>Nivel de aislamiento de las consultas (RF-QRY-12).</summary>
    public QueryIsolationLevel IsolationLevel { get; set; } = QueryIsolationLevel.ReadCommitted;

    /// <summary>Envolver cada consulta en una transacción que siempre se revierte (RF-QRY-08).</summary>
    public bool QueryInRollbackTransaction { get; set; } = true;
}

public class WriteConnectionConfig
{
    public AuthenticationKind? Authentication { get; set; }

    public string? UserName { get; set; }

    public string? CredentialRef { get; set; }

    public string? ApplicationName { get; set; }
}

public sealed class ConnectionConfig
{
    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public AuthenticationKind Authentication { get; set; } = AuthenticationKind.WindowsIntegrated;

    /// <summary>Usuario SQL o client id (entidad de servicio / identidad administrada asignada por el usuario).</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Referencia al secreto, nunca el secreto en sí (RS-SEC-02):
    /// "wincred:&lt;destino&gt;", "env:&lt;VARIABLE&gt;" o "dpapi:&lt;ruta de archivo&gt;".
    /// </summary>
    public string? CredentialRef { get; set; }

    public EncryptMode Encrypt { get; set; } = EncryptMode.Mandatory;

    public bool TrustServerCertificate { get; set; }

    public string? HostNameInCertificate { get; set; }

    public string ApplicationName { get; set; } = "SqlServerMcp";

    public ApplicationIntentKind ApplicationIntent { get; set; } = ApplicationIntentKind.ReadWrite;

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int MaxPoolSize { get; set; } = 10;

    public RetryConfig Retry { get; set; } = new();
}

public sealed class RetryConfig
{
    public bool Enabled { get; set; } = true;

    public int MaxRetries { get; set; } = 3;

    public int DeltaSeconds { get; set; } = 1;

    public int MaxSeconds { get; set; } = 20;
}

public sealed class ExclusionConfig
{
    /// <summary>Esquemas excluidos por completo.</summary>
    public List<string> Schemas { get; set; } = [];

    /// <summary>Tablas/vistas excluidas: "esquema.objeto" (admite "*").</summary>
    public List<string> Tables { get; set; } = [];

    /// <summary>Columnas excluidas: "esquema.tabla.columna", "tabla.columna" o "*.columna" (admite "*").</summary>
    public List<string> Columns { get; set; } = [];
}

public sealed class SensitiveColumnsConfig
{
    /// <summary>
    /// Patrones de nombre de columna, sin distinguir mayúsculas. Una palabra sin comodines ("ssn", "apikey") coincide por
    /// segmentos de palabra (CustomerSSN, api_key, ApiKey; no "Shipping" para "pin"); un patrón con "*" se compara como
    /// comodín sobre el nombre completo ("*password*" también coincide con "userpassword").
    /// </summary>
    public List<string> Patterns { get; set; } = [.. DefaultPatterns];

    /// <summary>Secretos y datos de autenticación o pago (RS-DAT-02).</summary>
    public static readonly IReadOnlyList<string> DefaultPatterns =
    [
        // Comodines (subcadena): términos largos y sin falsos positivos habituales.
        "*contrasena*", "*contraseña*", "*password*", "*passwd*", "*pwd*", "*secret*", "*secreto*",
        "*tarjeta*", "*cvv*", "*creditcard*", "*card_number*", "*cardnumber*",

        // Palabras (segmentos): términos cortos o que aparecen dentro de otras palabras.
        "token", "ssn", "socialsecurity", "apikey", "clientsecret", "privatekey", "accesskey", "refreshtoken",
        "otp", "pin", "cvc", "iban", "salt", "clave", "numerocuenta", "cuentabancaria", "creditcard", "cardnumber",
    ];

    public SensitiveColumnAction Action { get; set; } = SensitiveColumnAction.Mask;

    /// <summary>Considerar también las columnas con clasificación de sensibilidad de SQL Server.</summary>
    public bool UseSqlClassification { get; set; } = true;

    /// <summary>Texto con el que se reemplaza un valor enmascarado.</summary>
    public string MaskText { get; set; } = "****";
}

public enum PiiColumnAction
{
    /// <summary>El valor se devuelve enmascarado; la columna se puede usar en filtros, uniones y escrituras.</summary>
    Mask = 0,

    /// <summary>El valor se devuelve en claro.</summary>
    Allow = 1,
}

/// <summary>
/// Datos personales (PII): segunda categoría, con su propia acción. A diferencia de los secretos, enmascarar PII no
/// impide usarla en filtros ni escrituras (buscar un cliente por correo es un uso legítimo).
/// </summary>
public sealed class PiiColumnsConfig
{
    /// <summary>Mismas reglas de patrones que <see cref="SensitiveColumnsConfig.Patterns"/>.</summary>
    public List<string> Patterns { get; set; } = [.. DefaultPatterns];

    public static readonly IReadOnlyList<string> DefaultPatterns =
    [
        "email", "correo", "telefono", "telephone", "phone", "celular", "movil", "mobile", "direccion", "address",
        "fechanacimiento", "birthdate", "dateofbirth", "dni", "nif", "cuit", "cuil", "rfc", "curp", "pasaporte", "passport",
    ];

    public PiiColumnAction Action { get; set; } = PiiColumnAction.Mask;
}

public sealed class LimitsConfig
{
    public int CommandTimeoutSeconds { get; set; } = 30;

    public int LockTimeoutMs { get; set; } = 5000;

    public int MaxRows { get; set; } = 500;

    public int MaxResponseBytes { get; set; } = 1_048_576;

    public int MaxCellChars { get; set; } = 4000;

    public int MaxAffectedRows { get; set; } = 1000;

    public int ConfirmAboveRows { get; set; } = 100;

    public int MaxInsertRows { get; set; } = 1000;

    public int MaxStatementBytes { get; set; } = 65_536;

    public int MaxConcurrentQueries { get; set; } = 2;

    public int MaxCallsPerMinute { get; set; } = 60;

    public int TransactionIdleSeconds { get; set; } = 60;

    public int SampleMaxRows { get; set; } = 50;

    public int ConfirmationExpirySeconds { get; set; } = 300;

    /// <summary>Margen (%) sobre las filas aprobadas antes de revertir (RF-DML-04, RS-HIL-07).</summary>
    public int AffectedRowsMarginPercent { get; set; } = 10;

    /// <summary>Costo estimado del plan por encima del cual se solicita confirmación (null = desactivado).</summary>
    public double? ConfirmAboveEstimatedCost { get; set; }

    /// <summary>Filas a partir de las cuales una tabla se considera grande (alertas de índices y productos cartesianos).</summary>
    public long LargeTableRows { get; set; } = 1_000_000;

    public static LimitsConfig AbsoluteDefaults() => new()
    {
        CommandTimeoutSeconds = 300,
        LockTimeoutMs = 60_000,
        MaxRows = 10_000,
        MaxResponseBytes = 8_388_608,
        MaxCellChars = 65_536,
        MaxAffectedRows = 100_000,
        ConfirmAboveRows = 100_000,
        MaxInsertRows = 10_000,
        MaxStatementBytes = 262_144,
        MaxConcurrentQueries = 8,
        MaxCallsPerMinute = 600,
        TransactionIdleSeconds = 600,
        SampleMaxRows = 1000,
        ConfirmationExpirySeconds = 900,
        AffectedRowsMarginPercent = 50,
        ConfirmAboveEstimatedCost = null,
        LargeTableRows = long.MaxValue,
    };
}

public sealed class PolicyConfig
{
    /// <summary>CREATE / CREATE OR ALTER de objetos permitidos.</summary>
    public PolicyAction CreateAction { get; set; } = PolicyAction.Allow;

    /// <summary>ALTER no destructivo (ADD, ALTER VIEW/PROCEDURE/FUNCTION, ALTER INDEX).</summary>
    public PolicyAction AlterAction { get; set; } = PolicyAction.Allow;

    /// <summary>DROP de objetos (mínimo CONFIRMAR).</summary>
    public PolicyAction DropAction { get; set; } = PolicyAction.Confirm;

    /// <summary>TRUNCATE TABLE (mínimo CONFIRMAR).</summary>
    public PolicyAction TruncateAction { get; set; } = PolicyAction.Confirm;

    /// <summary>ALTER TABLE … DROP COLUMN, cambio de tipo de columna, sp_rename (mínimo CONFIRMAR).</summary>
    public PolicyAction DestructiveAlterAction { get; set; } = PolicyAction.Confirm;

    /// <summary>CREATE/ALTER/DROP/ENABLE/DISABLE TRIGGER DML (mínimo ALERTAR).</summary>
    public PolicyAction TriggerAction { get; set; } = PolicyAction.Confirm;

    /// <summary>UPDATE/DELETE sin WHERE o con predicado trivial (BLOQUEAR por defecto; mínimo CONFIRMAR).</summary>
    public PolicyAction UnfilteredDml { get; set; } = PolicyAction.Block;

    /// <summary>SQL dinámico (BLOQUEAR por defecto; mínimo CONFIRMAR, con análisis del literal).</summary>
    public PolicyAction DynamicSql { get; set; } = PolicyAction.Block;

    /// <summary>Exigir confirmación para todo UPDATE/DELETE/MERGE, sin importar el umbral (decisión pendiente 4).</summary>
    public bool AlwaysConfirmDml { get; set; }

    /// <summary>
    /// Permitir consultas a vistas de catálogo (sys.*, INFORMATION_SCHEMA.*) en run_query. Por defecto no: las herramientas
    /// de metadatos ya respetan las exclusiones (RF-MET-06); las vistas sensibles del Anexo C siempre se bloquean.
    /// </summary>
    public bool AllowSystemCatalogQueries { get; set; }

    /// <summary>Principales admitidos en CREATE SCHEMA … AUTHORIZATION (vacío = cláusula no permitida).</summary>
    public List<string> AllowedSchemaOwners { get; set; } = [];

    /// <summary>Permitir WITH ENCRYPTION en módulos (RF-DDL-03: bloqueado por defecto).</summary>
    public bool AllowEncryptedModules { get; set; }

    /// <summary>Qué hacer si el cliente MCP no soporta elicitation (RS-HIL-05).</summary>
    public ConfirmationUnavailableAction ConfirmationUnavailable { get; set; } = ConfirmationUnavailableAction.Reject;

    /// <summary>Intentos críticos bloqueados que suspenden la escritura de la sesión (RS-DCL-04).</summary>
    public int CriticalAttemptsThreshold { get; set; } = 3;

    public int CriticalAttemptsWindowMinutes { get; set; } = 10;

    /// <summary>Reglas adicionales (RS-POL-10). Nunca relajan las reglas inmutables.</summary>
    public List<PolicyRuleConfig> Rules { get; set; } = [];
}

public sealed class PolicyRuleConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Tipo(s) de sentencia a los que aplica: "Select", "Insert", "Update", "Delete", "Merge",
    /// "Create", "Alter", "Drop", "Truncate", "Execute", o una categoría (por ejemplo "DdlDrop").
    /// Vacío = cualquiera.
    /// </summary>
    public List<string> Statements { get; set; } = [];

    /// <summary>Atajo para un único tipo de sentencia (equivale a Statements = [Statement]).</summary>
    public string? Statement { get; set; }

    /// <summary>Esquema afectado (admite "*").</summary>
    public string? Schema { get; set; }

    /// <summary>Objeto afectado "esquema.objeto" (admite "*").</summary>
    public string? Object { get; set; }

    /// <summary>Columnas referenciadas (cualquiera de ellas dispara la regla).</summary>
    public List<string> Columns { get; set; } = [];

    /// <summary>Filas estimadas mínimas para que aplique la regla.</summary>
    public long? MinEstimatedRows { get; set; }

    /// <summary>Costo estimado mínimo del plan para que aplique la regla.</summary>
    public double? MinEstimatedCost { get; set; }

    public PolicyAction Action { get; set; } = PolicyAction.Block;

    public string Reason { get; set; } = string.Empty;
}

public enum ProcedureKind
{
    ReadOnly = 0,
    SideEffects = 1,
}

public sealed class ProcedureRuleConfig
{
    /// <summary>"esquema.procedimiento" (admite "*").</summary>
    public string Name { get; set; } = string.Empty;

    public ProcedureKind Kind { get; set; } = ProcedureKind.SideEffects;

    /// <summary>Exigir confirmación humana antes de ejecutarlo.</summary>
    public bool Confirm { get; set; }
}

public sealed class AuditConfig
{
    /// <summary>Directorio de auditoría. Admite variables de entorno (%LOCALAPPDATA%).</summary>
    public string Path { get; set; } = "%LOCALAPPDATA%\\SqlServerMcp\\audit";

    public int RetentionDays { get; set; } = 180;

    /// <summary>Encadenamiento de hashes para la integridad del registro (RS-AUD-08).</summary>
    public bool HashChain { get; set; } = true;

    /// <summary>Tamaño máximo de cada archivo antes de rotar.</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public sealed class AlertsConfig
{
    /// <summary>Escribir alertas en el registro de eventos de Windows (requiere origen registrado).</summary>
    public bool WindowsEventLog { get; set; }

    public string EventLogSource { get; set; } = "SqlServerMcp";

    /// <summary>Archivo JSON Lines de alertas. Por defecto, alerts-*.jsonl en el directorio de auditoría.</summary>
    public string? File { get; set; }

    /// <summary>Referencia (wincred:/env:/dpapi:) a la URL del webhook (Teams, Slack). Nunca la URL en claro.</summary>
    public string? WebhookRef { get; set; }

    public AlertSeverity MinimumWebhookSeverity { get; set; } = AlertSeverity.Warning;
}

public sealed class LoggingConfig
{
    /// <summary>Error, Warning, Information o Debug (RNF-12).</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Archivo de log opcional (JSON, rotación diaria). Los logs de consola van siempre a stderr.</summary>
    public string? FilePath { get; set; }

    public int RetainedFileCount { get; set; } = 30;
}
