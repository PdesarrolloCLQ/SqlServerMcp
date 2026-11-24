using System.Collections.Frozen;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerMcp.Policy;

/// <summary>Procedimientos con tratamiento especial en execute_ddl / SQL dinámico.</summary>
internal enum SpecialProcedure
{
    None = 0,
    Rename = 1,
    ExtendedProperty = 2,
    ExecuteSql = 3,
}

/// <summary>Clasificación de una sentencia (sección 3 del ERS).</summary>
/// <param name="Category">Categoría de la política.</param>
/// <param name="Operation">Operación legible (SELECT, CREATE, DROP…).</param>
/// <param name="ObjectType">Tipo de objeto para DDL (TABLE, VIEW, PROCEDURE…).</param>
/// <param name="ImmutableRule">Regla inmutable que bloquea el tipo de sentencia en cualquier posición (DCL, administración…).</param>
/// <param name="RequiresConfirmation">Objetos que requieren confirmación por defecto (partición, texto completo).</param>
/// <param name="Special">Procedimiento con tratamiento especial.</param>
internal sealed record StatementClass(
    StatementCategory Category,
    string? Operation,
    string? ObjectType,
    PolicyRule? ImmutableRule = null,
    bool RequiresConfirmation = false,
    SpecialProcedure Special = SpecialProcedure.None)
{
    public static readonly StatementClass Unknown = new(StatementCategory.Unknown, null, null);
}

/// <summary>
/// Clasifica cada sentencia por su tipo exacto de ScriptDom mediante un mapa explícito (RS-POL-01, RS-POL-04).
/// Denegar por defecto: cualquier tipo que no esté en el mapa es <see cref="StatementCategory.Unknown"/> y se bloquea.
/// Nunca se clasifica por palabras clave ni por prefijos del nombre del tipo.
/// </summary>
internal static class StatementClassifier
{
    private static readonly FrozenDictionary<Type, StatementClass> Fixed;
    private static readonly FrozenDictionary<Type, Func<TSqlStatement, StatementClass>> Computed;

    static StatementClassifier()
    {
        var f = new Dictionary<Type, StatementClass>();
        var c = new Dictionary<Type, Func<TSqlStatement, StatementClass>>();

        void Add<T>(StatementCategory category, string? objectType = null, PolicyRule? rule = null, bool confirm = false, string? operation = null)
            where T : TSqlStatement =>
            f[typeof(T)] = new StatementClass(category, operation ?? OperationOf(typeof(T)), objectType, rule, confirm);

        void Imm<T>(StatementCategory category, PolicyRule rule, string? objectType = null)
            where T : TSqlStatement => Add<T>(category, objectType, rule);

        void Comp<T>(Func<T, StatementClass> classify)
            where T : TSqlStatement => c[typeof(T)] = s => classify((T)s);

        // ---- Consulta y DML
        Comp<SelectStatement>(s => s.Into is null
            ? new StatementClass(StatementCategory.Query, "SELECT", null)
            : new StatementClass(StatementCategory.SelectInto, "SELECT INTO", "TABLE"));
        Add<InsertStatement>(StatementCategory.Insert, operation: "INSERT");
        Add<UpdateStatement>(StatementCategory.Update, operation: "UPDATE");
        Add<DeleteStatement>(StatementCategory.Delete, operation: "DELETE");
        Add<MergeStatement>(StatementCategory.Merge, operation: "MERGE");
        Comp<ExecuteStatement>(ClassifyExecute);

        // ---- DDL de tablas
        Add<CreateTableStatement>(StatementCategory.DdlCreate, "TABLE");
        Add<AlterTableAddTableElementStatement>(StatementCategory.DdlAlter, "TABLE");
        Comp<AlterTableAlterColumnStatement>(s =>
            s.IsMasked || s.MaskingFunction is not null
            || s.AlterTableAlterColumnOption is AlterTableAlterColumnOption.AddMaskingFunction or AlterTableAlterColumnOption.DropMaskingFunction
                ? new StatementClass(StatementCategory.Dcl, "ALTER", "TABLE", PolicyRules.DclGovernance)
                : new StatementClass(StatementCategory.DdlDestructiveAlter, "ALTER", "TABLE"));
        Comp<AlterTableDropTableElementStatement>(s =>
            s.AlterTableDropTableElements.Any(e => e.TableElementType == TableElementType.Column)
                ? new StatementClass(StatementCategory.DdlDestructiveAlter, "ALTER", "TABLE")
                : new StatementClass(StatementCategory.DdlAlter, "ALTER", "TABLE"));
        Comp<AlterTableSetStatement>(s =>
            s.Options.OfType<SystemVersioningTableOption>().Any(o => o.OptionState == OptionState.Off)
                ? new StatementClass(StatementCategory.DdlDestructiveAlter, "ALTER", "TABLE")
                : new StatementClass(StatementCategory.DdlAlter, "ALTER", "TABLE"));
        Add<AlterTableConstraintModificationStatement>(StatementCategory.DdlAlter, "TABLE");
        Add<AlterTableRebuildStatement>(StatementCategory.DdlAlter, "TABLE");
        Add<AlterTableChangeTrackingModificationStatement>(StatementCategory.DdlAlter, "TABLE");
        Add<AlterTableFileTableNamespaceStatement>(StatementCategory.DdlAlter, "TABLE");
        Add<AlterTableAlterIndexStatement>(StatementCategory.DdlAlter, "TABLE");
        Add<AlterTableAddClusterByStatement>(StatementCategory.DdlAlter, "TABLE");
        Add<AlterTableAlterPartitionStatement>(StatementCategory.DdlAlter, "TABLE", confirm: true);
        Add<AlterTableSwitchStatement>(StatementCategory.DdlDestructiveAlter, "TABLE", operation: "ALTER");
        Comp<AlterTableTriggerModificationStatement>(s => new StatementClass(
            StatementCategory.DdlTrigger, s.TriggerEnforcement == TriggerEnforcement.Enable ? "ENABLE" : "DISABLE", "TRIGGER"));
        Add<DropTableStatement>(StatementCategory.DdlDrop, "TABLE");
        Add<TruncateTableStatement>(StatementCategory.DdlTruncate, "TABLE", operation: "TRUNCATE");

        // ---- Vistas, procedimientos, funciones
        Add<CreateViewStatement>(StatementCategory.DdlCreate, "VIEW");
        Add<CreateOrAlterViewStatement>(StatementCategory.DdlCreate, "VIEW");
        Add<AlterViewStatement>(StatementCategory.DdlAlter, "VIEW");
        Add<DropViewStatement>(StatementCategory.DdlDrop, "VIEW");
        Add<CreateProcedureStatement>(StatementCategory.DdlCreate, "PROCEDURE");
        Add<CreateOrAlterProcedureStatement>(StatementCategory.DdlCreate, "PROCEDURE");
        Add<AlterProcedureStatement>(StatementCategory.DdlAlter, "PROCEDURE");
        Add<DropProcedureStatement>(StatementCategory.DdlDrop, "PROCEDURE");
        Add<CreateFunctionStatement>(StatementCategory.DdlCreate, "FUNCTION");
        Add<CreateOrAlterFunctionStatement>(StatementCategory.DdlCreate, "FUNCTION");
        Add<AlterFunctionStatement>(StatementCategory.DdlAlter, "FUNCTION");
        Add<DropFunctionStatement>(StatementCategory.DdlDrop, "FUNCTION");

        // ---- Triggers: DML permitidos (con confirmación); DDL/LOGON bloqueados (Anexo C)
        Comp<CreateTriggerStatement>(s => ClassifyTrigger(s.TriggerObject?.TriggerScope ?? TriggerScope.Normal, "CREATE"));
        Comp<CreateOrAlterTriggerStatement>(s => ClassifyTrigger(s.TriggerObject?.TriggerScope ?? TriggerScope.Normal, "CREATE OR ALTER"));
        Comp<AlterTriggerStatement>(s => ClassifyTrigger(s.TriggerObject?.TriggerScope ?? TriggerScope.Normal, "ALTER"));
        Comp<DropTriggerStatement>(s => ClassifyTrigger(s.TriggerScope, "DROP"));
        Comp<EnableDisableTriggerStatement>(s => ClassifyTrigger(
            s.TriggerObject?.TriggerScope ?? TriggerScope.Normal, s.TriggerEnforcement == TriggerEnforcement.Enable ? "ENABLE" : "DISABLE"));

        // ---- Índices y estadísticas
        Add<CreateIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Add<CreateColumnStoreIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Add<CreateSpatialIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Add<CreateXmlIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Add<CreateSelectiveXmlIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Add<CreateJsonIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Add<CreateVectorIndexStatement>(StatementCategory.DdlCreate, "INDEX");
        Comp<AlterIndexStatement>(s => s.AlterIndexType == AlterIndexType.Disable
            ? new StatementClass(StatementCategory.DdlDestructiveAlter, "ALTER", "INDEX")
            : new StatementClass(StatementCategory.DdlAlter, "ALTER", "INDEX"));
        Add<DropIndexStatement>(StatementCategory.DdlDrop, "INDEX");
        Add<CreateStatisticsStatement>(StatementCategory.Statistics, "STATISTICS");
        Add<UpdateStatisticsStatement>(StatementCategory.Statistics, "STATISTICS", operation: "UPDATE");
        Add<DropStatisticsStatement>(StatementCategory.Statistics, "STATISTICS");

        // ---- Esquemas, secuencias, sinónimos, tipos
        Add<CreateSchemaStatement>(StatementCategory.DdlCreate, "SCHEMA");
        Add<AlterSchemaStatement>(StatementCategory.DdlAlter, "SCHEMA", operation: "ALTER SCHEMA TRANSFER");
        Add<DropSchemaStatement>(StatementCategory.DdlDrop, "SCHEMA");
        Add<CreateSequenceStatement>(StatementCategory.DdlCreate, "SEQUENCE");
        Add<AlterSequenceStatement>(StatementCategory.DdlAlter, "SEQUENCE");
        Add<DropSequenceStatement>(StatementCategory.DdlDrop, "SEQUENCE");
        Add<CreateSynonymStatement>(StatementCategory.DdlCreate, "SYNONYM");
        Add<DropSynonymStatement>(StatementCategory.DdlDrop, "SYNONYM");
        Add<CreateTypeUddtStatement>(StatementCategory.DdlCreate, "TYPE");
        Add<CreateTypeTableStatement>(StatementCategory.DdlCreate, "TYPE");
        Add<DropTypeStatement>(StatementCategory.DdlDrop, "TYPE");
        Imm<CreateTypeUdtStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "TYPE");

        // ---- Partición y texto completo (CONFIRMAR por defecto)
        Add<CreatePartitionFunctionStatement>(StatementCategory.DdlCreate, "PARTITION FUNCTION", confirm: true);
        Add<AlterPartitionFunctionStatement>(StatementCategory.DdlAlter, "PARTITION FUNCTION", confirm: true);
        Add<DropPartitionFunctionStatement>(StatementCategory.DdlDrop, "PARTITION FUNCTION", confirm: true);
        Add<CreatePartitionSchemeStatement>(StatementCategory.DdlCreate, "PARTITION SCHEME", confirm: true);
        Add<AlterPartitionSchemeStatement>(StatementCategory.DdlAlter, "PARTITION SCHEME", confirm: true);
        Add<DropPartitionSchemeStatement>(StatementCategory.DdlDrop, "PARTITION SCHEME", confirm: true);
        Add<CreateFullTextCatalogStatement>(StatementCategory.DdlCreate, "FULLTEXT CATALOG", confirm: true);
        Add<AlterFullTextCatalogStatement>(StatementCategory.DdlAlter, "FULLTEXT CATALOG", confirm: true);
        Add<DropFullTextCatalogStatement>(StatementCategory.DdlDrop, "FULLTEXT CATALOG", confirm: true);
        Add<CreateFullTextIndexStatement>(StatementCategory.DdlCreate, "FULLTEXT INDEX", confirm: true);
        Add<AlterFullTextIndexStatement>(StatementCategory.DdlAlter, "FULLTEXT INDEX", confirm: true);
        Add<DropFullTextIndexStatement>(StatementCategory.DdlDrop, "FULLTEXT INDEX", confirm: true);

        // ---- Control de flujo, variables y cursores (solo dentro de módulos)
        Add<BeginEndBlockStatement>(StatementCategory.ProceduralControl, operation: "BEGIN…END");
        Add<BeginEndAtomicBlockStatement>(StatementCategory.ProceduralControl, operation: "BEGIN ATOMIC…END");
        Add<IfStatement>(StatementCategory.ProceduralControl, operation: "IF");
        Add<WhileStatement>(StatementCategory.ProceduralControl, operation: "WHILE");
        Add<TryCatchStatement>(StatementCategory.ProceduralControl, operation: "TRY…CATCH");
        Add<DeclareVariableStatement>(StatementCategory.ProceduralControl, operation: "DECLARE");
        Add<DeclareTableVariableStatement>(StatementCategory.ProceduralControl, operation: "DECLARE");
        Add<SetVariableStatement>(StatementCategory.ProceduralControl, operation: "SET");
        Add<PrintStatement>(StatementCategory.ProceduralControl, operation: "PRINT");
        Add<ThrowStatement>(StatementCategory.ProceduralControl, operation: "THROW");
        Add<RaiseErrorStatement>(StatementCategory.ProceduralControl, operation: "RAISERROR");
        Add<RaiseErrorLegacyStatement>(StatementCategory.ProceduralControl, operation: "RAISERROR");
        Add<ReturnStatement>(StatementCategory.ProceduralControl, operation: "RETURN");
        Add<GoToStatement>(StatementCategory.ProceduralControl, operation: "GOTO");
        Add<LabelStatement>(StatementCategory.ProceduralControl, operation: "LABEL");
        Add<BreakStatement>(StatementCategory.ProceduralControl, operation: "BREAK");
        Add<ContinueStatement>(StatementCategory.ProceduralControl, operation: "CONTINUE");
        Add<DeclareCursorStatement>(StatementCategory.Cursor, operation: "DECLARE CURSOR");
        Add<OpenCursorStatement>(StatementCategory.Cursor, operation: "OPEN");
        Add<FetchCursorStatement>(StatementCategory.Cursor, operation: "FETCH");
        Add<CloseCursorStatement>(StatementCategory.Cursor, operation: "CLOSE");
        Add<DeallocateCursorStatement>(StatementCategory.Cursor, operation: "DEALLOCATE");

        // ---- Transacciones y opciones de sesión
        Add<BeginTransactionStatement>(StatementCategory.TransactionControl, operation: "BEGIN TRANSACTION");
        Add<CommitTransactionStatement>(StatementCategory.TransactionControl, operation: "COMMIT");
        Add<RollbackTransactionStatement>(StatementCategory.TransactionControl, operation: "ROLLBACK");
        Add<SaveTransactionStatement>(StatementCategory.TransactionControl, operation: "SAVE TRANSACTION");
        Comp<PredicateSetStatement>(s => (s.Options & SetOptions.ImplicitTransactions) != 0
            ? new StatementClass(StatementCategory.TransactionControl, "SET", null)
            : new StatementClass(StatementCategory.SessionOption, "SET", null));
        Add<SetCommandStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetRowCountStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetTransactionIsolationLevelStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetIdentityInsertStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetStatisticsStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetOffsetsStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetTextSizeStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<SetErrorLevelStatement>(StatementCategory.SessionOption, operation: "SET");
        Add<UseStatement>(StatementCategory.ContextChange, operation: "USE");
        Add<WaitForStatement>(StatementCategory.Wait, operation: "WAITFOR");

        // ---- Suplantación (POL-IMP-001)
        Imm<ExecuteAsStatement>(StatementCategory.Impersonation, PolicyRules.ImpersonationStatement);
        Imm<RevertStatement>(StatementCategory.Impersonation, PolicyRules.ImpersonationStatement);
        Imm<SetUserStatement>(StatementCategory.Impersonation, PolicyRules.ImpersonationStatement);

        // ---- DCL: permisos (POL-DCL-001)
        Imm<GrantStatement>(StatementCategory.Dcl, PolicyRules.DclPermissions);
        Imm<DenyStatement>(StatementCategory.Dcl, PolicyRules.DclPermissions);
        Imm<RevokeStatement>(StatementCategory.Dcl, PolicyRules.DclPermissions);
        Imm<GrantStatement80>(StatementCategory.Dcl, PolicyRules.DclPermissions);
        Imm<DenyStatement80>(StatementCategory.Dcl, PolicyRules.DclPermissions);
        Imm<RevokeStatement80>(StatementCategory.Dcl, PolicyRules.DclPermissions);

        // ---- DCL: logins y usuarios (POL-DCL-002)
        Imm<CreateLoginStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "LOGIN");
        Imm<AlterLoginAddDropCredentialStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "LOGIN");
        Imm<AlterLoginEnableDisableStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "LOGIN");
        Imm<AlterLoginOptionsStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "LOGIN");
        Imm<DropLoginStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "LOGIN");
        Imm<CreateUserStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "USER");
        Imm<AlterUserStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "USER");
        Imm<DropUserStatement>(StatementCategory.Dcl, PolicyRules.DclLoginsUsers, "USER");

        // ---- DCL: roles (POL-DCL-003)
        Imm<CreateRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "ROLE");
        Imm<AlterRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "ROLE");
        Imm<DropRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "ROLE");
        Imm<CreateServerRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "SERVER ROLE");
        Imm<AlterServerRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "SERVER ROLE");
        Imm<DropServerRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "SERVER ROLE");
        Imm<CreateApplicationRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "APPLICATION ROLE");
        Imm<AlterApplicationRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "APPLICATION ROLE");
        Imm<DropApplicationRoleStatement>(StatementCategory.Dcl, PolicyRules.DclRoles, "APPLICATION ROLE");

        // ---- DCL: propiedad (POL-DCL-004)
        Imm<AlterAuthorizationStatement>(StatementCategory.Dcl, PolicyRules.DclOwnership);

        // ---- DCL: criptografía (POL-DCL-005)
        Imm<CreateCertificateStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CERTIFICATE");
        Imm<AlterCertificateStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CERTIFICATE");
        Imm<DropCertificateStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CERTIFICATE");
        Imm<BackupCertificateStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CERTIFICATE");
        Imm<CreateMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<AlterMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<DropMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<BackupMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<RestoreMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<OpenMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<CloseMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "MASTER KEY");
        Imm<AlterServiceMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SERVICE MASTER KEY");
        Imm<BackupServiceMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SERVICE MASTER KEY");
        Imm<RestoreServiceMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SERVICE MASTER KEY");
        Imm<CreateSymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SYMMETRIC KEY");
        Imm<AlterSymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SYMMETRIC KEY");
        Imm<DropSymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SYMMETRIC KEY");
        Imm<OpenSymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SYMMETRIC KEY");
        Imm<CloseSymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SYMMETRIC KEY");
        Imm<CreateAsymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "ASYMMETRIC KEY");
        Imm<AlterAsymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "ASYMMETRIC KEY");
        Imm<DropAsymmetricKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "ASYMMETRIC KEY");
        Imm<CreateCredentialStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CREDENTIAL");
        Imm<AlterCredentialStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CREDENTIAL");
        Imm<DropCredentialStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CREDENTIAL");
        Imm<AddSignatureStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SIGNATURE");
        Imm<DropSignatureStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "SIGNATURE");
        Imm<CreateColumnEncryptionKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "COLUMN ENCRYPTION KEY");
        Imm<AlterColumnEncryptionKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "COLUMN ENCRYPTION KEY");
        Imm<DropColumnEncryptionKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "COLUMN ENCRYPTION KEY");
        Imm<CreateColumnMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "COLUMN MASTER KEY");
        Imm<DropColumnMasterKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "COLUMN MASTER KEY");
        Imm<CreateDatabaseEncryptionKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "DATABASE ENCRYPTION KEY");
        Imm<AlterDatabaseEncryptionKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "DATABASE ENCRYPTION KEY");
        Imm<DropDatabaseEncryptionKeyStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "DATABASE ENCRYPTION KEY");
        Imm<CreateCryptographicProviderStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CRYPTOGRAPHIC PROVIDER");
        Imm<AlterCryptographicProviderStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CRYPTOGRAPHIC PROVIDER");
        Imm<DropCryptographicProviderStatement>(StatementCategory.Dcl, PolicyRules.DclCrypto, "CRYPTOGRAPHIC PROVIDER");

        // ---- DCL: gobierno de datos (POL-DCL-006)
        Imm<CreateSecurityPolicyStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SECURITY POLICY");
        Imm<AlterSecurityPolicyStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SECURITY POLICY");
        Imm<DropSecurityPolicyStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SECURITY POLICY");
        Imm<AddSensitivityClassificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SENSITIVITY CLASSIFICATION");
        Imm<DropSensitivityClassificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SENSITIVITY CLASSIFICATION");
        Imm<CreateServerAuditStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SERVER AUDIT");
        Imm<AlterServerAuditStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SERVER AUDIT");
        Imm<DropServerAuditStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SERVER AUDIT");
        Imm<CreateServerAuditSpecificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SERVER AUDIT SPECIFICATION");
        Imm<AlterServerAuditSpecificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SERVER AUDIT SPECIFICATION");
        Imm<DropServerAuditSpecificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "SERVER AUDIT SPECIFICATION");
        Imm<CreateDatabaseAuditSpecificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "DATABASE AUDIT SPECIFICATION");
        Imm<AlterDatabaseAuditSpecificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "DATABASE AUDIT SPECIFICATION");
        Imm<DropDatabaseAuditSpecificationStatement>(StatementCategory.Dcl, PolicyRules.DclGovernance, "DATABASE AUDIT SPECIFICATION");

        // ---- Administración: bases de datos (POL-ADM-001; TRUSTWORTHY/DB_CHAINING/CONTAINMENT → POL-DCL-007)
        Imm<CreateDatabaseStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<DropDatabaseStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseAddFileGroupStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseAddFileStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseCollateStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseModifyFileGroupStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseModifyFileStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseModifyNameStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabasePerformCutoverStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseRebuildLogStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseRemoveFileGroupStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseRemoveFileStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseScopedConfigurationClearStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Imm<AlterDatabaseScopedConfigurationSetStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "DATABASE");
        Comp<AlterDatabaseSetStatement>(s => s.Options.Any(o => o.OptionKind is DatabaseOptionKind.Trustworthy or DatabaseOptionKind.DBChaining or DatabaseOptionKind.Containment)
            ? new StatementClass(StatementCategory.Dcl, "ALTER", "DATABASE", PolicyRules.DclDatabaseSecurity)
            : new StatementClass(StatementCategory.Administration, "ALTER", "DATABASE", PolicyRules.AdminDatabase));
        Imm<CreateFederationStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "FEDERATION");
        Imm<AlterFederationStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "FEDERATION");
        Imm<DropFederationStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "FEDERATION");
        Imm<UseFederationStatement>(StatementCategory.Administration, PolicyRules.AdminDatabase, "FEDERATION");

        // ---- Administración: respaldo, DBCC, instancia
        Imm<BackupDatabaseStatement>(StatementCategory.Administration, PolicyRules.AdminBackupRestore, "DATABASE");
        Imm<BackupTransactionLogStatement>(StatementCategory.Administration, PolicyRules.AdminBackupRestore, "DATABASE");
        Imm<RestoreStatement>(StatementCategory.Administration, PolicyRules.AdminBackupRestore, "DATABASE");
        Imm<DbccStatement>(StatementCategory.Administration, PolicyRules.AdminDbcc);
        Imm<ShutdownStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<KillStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<KillQueryNotificationSubscriptionStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<KillStatsJobStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<CheckpointStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<ReconfigureStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<DiskStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationSetBufferPoolExtensionStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationSetDiagnosticsLogStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationSetExternalAuthenticationStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationSetFailoverClusterPropertyStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationSetHadrClusterStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<AlterServerConfigurationSetSoftNumaStatement>(StatementCategory.Administration, PolicyRules.AdminInstance);
        Imm<CreateEndpointStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "ENDPOINT");
        Imm<AlterEndpointStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "ENDPOINT");
        Imm<DropEndpointStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "ENDPOINT");
        Imm<CreateAvailabilityGroupStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "AVAILABILITY GROUP");
        Imm<AlterAvailabilityGroupStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "AVAILABILITY GROUP");
        Imm<DropAvailabilityGroupStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "AVAILABILITY GROUP");
        Imm<AlterResourceGovernorStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "RESOURCE GOVERNOR");
        Imm<CreateResourcePoolStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "RESOURCE POOL");
        Imm<AlterResourcePoolStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "RESOURCE POOL");
        Imm<DropResourcePoolStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "RESOURCE POOL");
        Imm<CreateWorkloadGroupStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "WORKLOAD GROUP");
        Imm<AlterWorkloadGroupStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "WORKLOAD GROUP");
        Imm<DropWorkloadGroupStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "WORKLOAD GROUP");
        Imm<CreateWorkloadClassifierStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "WORKLOAD CLASSIFIER");
        Imm<DropWorkloadClassifierStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "WORKLOAD CLASSIFIER");
        Imm<CreateEventSessionStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "EVENT SESSION");
        Imm<AlterEventSessionStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "EVENT SESSION");
        Imm<DropEventSessionStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "EVENT SESSION");
        Imm<EventSessionStatement>(StatementCategory.Administration, PolicyRules.AdminInstance, "EVENT SESSION");

        // ---- Acceso externo: datos externos y carga masiva (POL-EXT-001)
        Imm<BulkInsertStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData);
        Imm<InsertBulkStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData);
        Imm<CopyStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData);
        Imm<CreateExternalDataSourceStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL DATA SOURCE");
        Imm<AlterExternalDataSourceStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL DATA SOURCE");
        Imm<DropExternalDataSourceStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL DATA SOURCE");
        Imm<CreateExternalFileFormatStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL FILE FORMAT");
        Imm<DropExternalFileFormatStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL FILE FORMAT");
        Imm<CreateExternalTableStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL TABLE");
        Imm<DropExternalTableStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL TABLE");
        Imm<CreateExternalStreamStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL STREAM");
        Imm<DropExternalStreamStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL STREAM");
        Imm<CreateExternalStreamingJobStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL STREAMING JOB");
        Imm<DropExternalStreamingJobStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL STREAMING JOB");
        Imm<CreateExternalModelStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL MODEL");
        Imm<AlterExternalModelStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL MODEL");
        Imm<DropExternalModelStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalData, "EXTERNAL MODEL");

        // ---- Acceso externo: código externo y CLR (POL-EXT-003)
        Imm<CreateAssemblyStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "ASSEMBLY");
        Imm<AlterAssemblyStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "ASSEMBLY");
        Imm<DropAssemblyStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "ASSEMBLY");
        Imm<CreateAggregateStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "AGGREGATE");
        Imm<DropAggregateStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "AGGREGATE");
        Imm<CreateExternalLibraryStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL LIBRARY");
        Imm<AlterExternalLibraryStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL LIBRARY");
        Imm<DropExternalLibraryStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL LIBRARY");
        Imm<CreateExternalLanguageStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL LANGUAGE");
        Imm<AlterExternalLanguageStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL LANGUAGE");
        Imm<DropExternalLanguageStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL LANGUAGE");
        Imm<CreateExternalResourcePoolStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL RESOURCE POOL");
        Imm<AlterExternalResourcePoolStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL RESOURCE POOL");
        Imm<DropExternalResourcePoolStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL RESOURCE POOL");
        Imm<ExternalResourcePoolStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL RESOURCE POOL");
        Imm<CreateExternalFunctionStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL FUNCTION");
        Imm<AlterExternalFunctionStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL FUNCTION");
        Imm<CreateOrAlterExternalFunctionStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalOs, "EXTERNAL FUNCTION");

        // ---- Acceso externo: objetos de servidor y mensajería (POL-EXT-005)
        Imm<CreateEventNotificationStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "EVENT NOTIFICATION");
        Imm<DropEventNotificationStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "EVENT NOTIFICATION");
        Imm<CreateQueueStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "QUEUE");
        Imm<AlterQueueStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "QUEUE");
        Imm<DropQueueStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "QUEUE");
        Imm<CreateServiceStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "SERVICE");
        Imm<AlterServiceStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "SERVICE");
        Imm<DropServiceStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "SERVICE");
        Imm<CreateContractStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "CONTRACT");
        Imm<DropContractStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "CONTRACT");
        Imm<CreateRouteStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "ROUTE");
        Imm<AlterRouteStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "ROUTE");
        Imm<DropRouteStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "ROUTE");
        Imm<CreateMessageTypeStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "MESSAGE TYPE");
        Imm<AlterMessageTypeStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "MESSAGE TYPE");
        Imm<DropMessageTypeStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "MESSAGE TYPE");
        Imm<CreateRemoteServiceBindingStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "REMOTE SERVICE BINDING");
        Imm<AlterRemoteServiceBindingStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "REMOTE SERVICE BINDING");
        Imm<DropRemoteServiceBindingStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "REMOTE SERVICE BINDING");
        Imm<CreateBrokerPriorityStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "BROKER PRIORITY");
        Imm<AlterBrokerPriorityStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "BROKER PRIORITY");
        Imm<DropBrokerPriorityStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects, "BROKER PRIORITY");
        Imm<SendStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);
        Imm<ReceiveStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);
        Imm<BeginDialogStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);
        Imm<EndConversationStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);
        Imm<MoveConversationStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);
        Imm<BeginConversationTimerStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);
        Imm<GetConversationGroupStatement>(StatementCategory.ExternalAccess, PolicyRules.ExternalServerObjects);

        Fixed = f.ToFrozenDictionary();
        Computed = c.ToFrozenDictionary();
    }

    /// <summary>Clasifica la sentencia por su tipo exacto. Tipos no catalogados → Unknown (fail-closed).</summary>
    public static StatementClass Classify(TSqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var type = statement.GetType();
        if (Computed.TryGetValue(type, out var compute))
        {
            return compute(statement);
        }

        return Fixed.TryGetValue(type, out var cls) ? cls : StatementClass.Unknown;
    }

    /// <summary>Indica si un tipo de sentencia está catalogado (para pruebas de cobertura del mapa).</summary>
    public static bool IsCatalogued(Type statementType) => Fixed.ContainsKey(statementType) || Computed.ContainsKey(statementType);

    /// <summary>Categoría que corresponde a una regla inmutable de procedimiento.</summary>
    public static StatementCategory CategoryOf(PolicyRule rule) => rule.Id switch
    {
        var id when id.StartsWith("POL-DCL", StringComparison.Ordinal) => StatementCategory.Dcl,
        var id when id.StartsWith("POL-IMP", StringComparison.Ordinal) => StatementCategory.Impersonation,
        var id when id.StartsWith("POL-ADM", StringComparison.Ordinal) => StatementCategory.Administration,
        var id when id.StartsWith("POL-EXT", StringComparison.Ordinal) => StatementCategory.ExternalAccess,
        var id when id.StartsWith("POL-DYN", StringComparison.Ordinal) => StatementCategory.DynamicSql,
        _ => StatementCategory.ExecuteProcedure,
    };

    private static StatementClass ClassifyTrigger(TriggerScope scope, string operation) => scope == TriggerScope.Normal
        ? new StatementClass(StatementCategory.DdlTrigger, operation, "TRIGGER")
        : new StatementClass(StatementCategory.ExternalAccess, operation, "TRIGGER", PolicyRules.ExternalServerObjects);

    private static StatementClass ClassifyExecute(ExecuteStatement statement)
    {
        var entity = statement.ExecuteSpecification?.ExecutableEntity;
        if (entity is ExecutableStringList)
        {
            return new StatementClass(StatementCategory.DynamicSql, "EXECUTE", null);
        }

        if (entity is not ExecutableProcedureReference reference)
        {
            return StatementClass.Unknown;
        }

        if (reference.ProcedureReference?.ProcedureVariable is not null)
        {
            return new StatementClass(StatementCategory.DynamicSql, "EXECUTE", null);
        }

        if (reference.AdHocDataSource is not null || statement.ExecuteSpecification?.LinkedServer is not null)
        {
            return new StatementClass(StatementCategory.ExternalAccess, "EXECUTE", "PROCEDURE");
        }

        if (reference.ProcedureReference?.ProcedureReference?.Name is not { } schemaObject)
        {
            return StatementClass.Unknown;
        }

        var name = PolicyNames.From(schemaObject);
        if (name.Server is not null)
        {
            return new StatementClass(StatementCategory.ExternalAccess, "EXECUTE", "PROCEDURE");
        }

        if (Denylist.IsExecuteSql(name.Name))
        {
            return new StatementClass(StatementCategory.DynamicSql, "EXECUTE", null, Special: SpecialProcedure.ExecuteSql);
        }

        if (Denylist.ProcedureRule(name.Name) is { } rule)
        {
            return new StatementClass(CategoryOf(rule), "EXECUTE", "PROCEDURE");
        }

        if (Denylist.IsMsdb(name.Database))
        {
            return new StatementClass(StatementCategory.ExternalAccess, "EXECUTE", "PROCEDURE");
        }

        var systemSchema = string.IsNullOrEmpty(name.Schema)
            || PolicyNames.EqualsIgnoreCase(name.Schema, "sys")
            || PolicyNames.EqualsIgnoreCase(name.Schema, PolicyNames.DefaultSchema);
        if (systemSchema && Denylist.IsRenameProcedure(name.Name))
        {
            return new StatementClass(StatementCategory.DdlDestructiveAlter, "RENAME", null, Special: SpecialProcedure.Rename);
        }

        if (systemSchema && Denylist.IsExtendedPropertyProcedure(name.Name))
        {
            return new StatementClass(StatementCategory.ExtendedProperty, "EXECUTE", "EXTENDED PROPERTY", Special: SpecialProcedure.ExtendedProperty);
        }

        return new StatementClass(StatementCategory.ExecuteProcedure, "EXECUTE", "PROCEDURE");
    }

    /// <summary>Operaciones cuyo nombre de tipo no coincide con la palabra clave T-SQL.</summary>
    private static readonly FrozenDictionary<string, string> OperationNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ExecuteAs"] = "EXECUTE AS",
        ["BulkInsert"] = "BULK INSERT",
        ["InsertBulk"] = "INSERT BULK",
        ["SetUser"] = "SETUSER",
        ["WaitFor"] = "WAITFOR",
        ["Dbcc"] = "DBCC",
        ["KillQueryNotificationSubscription"] = "KILL QUERY NOTIFICATION SUBSCRIPTION",
        ["KillStatsJob"] = "KILL STATS JOB",
        ["Copy"] = "COPY INTO",
        ["Send"] = "SEND",
        ["Receive"] = "RECEIVE",
        ["Disk"] = "DISK",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static string OperationOf(Type type)
    {
        var name = type.Name;
        if (name.EndsWith("Statement", StringComparison.Ordinal))
        {
            name = name[..^"Statement".Length];
        }

        if (OperationNames.TryGetValue(name, out var known))
        {
            return known;
        }

        string[] verbs = ["CreateOrAlter", "Create", "Alter", "Drop", "Backup", "Restore", "Grant", "Deny", "Revoke", "Open", "Close", "Add", "Kill", "Begin", "End", "Move", "Get"];
        foreach (var verb in verbs)
        {
            if (name.StartsWith(verb, StringComparison.Ordinal))
            {
                return verb == "CreateOrAlter" ? "CREATE OR ALTER" : verb.ToUpperInvariant();
            }
        }

        return name.ToUpperInvariant();
    }
}
