using System.Collections.Frozen;

namespace SqlServerMcp.Policy;

/// <summary>
/// Listas de denegación inmutables (Anexo C y sección 5.4). Los nombres se comparan normalizados
/// (<see cref="PolicyNames.Key"/>): sin delimitadores, sin distinguir mayúsculas, sin espacios finales ni
/// diacríticos, y con independencia del prefijo de base de datos o esquema (RS-POL-06).
/// </summary>
internal static class Denylist
{
    /// <summary>Procedimientos del sistema de solo lectura permitidos (RF-PRC-02).</summary>
    public static readonly FrozenSet<string> ReadOnlySystemProcedures = Keys("sp_help", "sp_helptext", "sp_describe_first_result_set");

    /// <summary>Procedimientos que execute_ddl admite como DDL (RF-DDL: propiedades extendidas y sp_rename).</summary>
    public static readonly FrozenSet<string> ExtendedPropertyProcedures = Keys("sp_addextendedproperty", "sp_updateextendedproperty", "sp_dropextendedproperty");

    public const string RenameProcedure = "SP_RENAME";

    public const string ExecuteSqlProcedure = "SP_EXECUTESQL";

    private static readonly FrozenDictionary<string, PolicyRule> ProcedureRules = BuildProcedureRules();

    /// <summary>Vistas de catálogo sensibles (esquema sys, en cualquier base de datos).</summary>
    private static readonly FrozenSet<string> SensitiveSysViews = Keys(
        "sql_logins", "syslogins", "sysxlogins", "server_principals", "server_role_members", "server_permissions",
        "credentials", "database_scoped_credentials", "database_credentials", "symmetric_keys", "asymmetric_keys",
        "certificates", "master_key_passwords", "linked_logins", "remote_logins", "sysremotelogins", "sysoledbusers",
        "key_encryptions", "column_master_keys", "column_encryption_keys", "column_encryption_key_values",
        "server_audits", "server_file_audits", "server_audit_specifications", "server_audit_specification_details",
        "database_audit_specifications", "database_audit_specification_details", "dm_server_audit_status", "fn_get_audit_file",
        "fn_xe_file_target_read_file", "fn_dblog", "fn_dump_dblog", "fn_trace_gettable", "fn_get_sql");

    /// <summary>Vistas de compatibilidad sensibles que SQL Server resuelve aunque se escriban sin esquema o con dbo.</summary>
    private static readonly FrozenSet<string> SensitiveLegacyViews = Keys("syslogins", "sysxlogins", "sysremotelogins", "sysoledbusers");

    /// <summary>Funciones sensibles (Anexo C), con o sin prefijo sys.</summary>
    private static readonly FrozenSet<string> SensitiveFunctions = Keys(
        "PWDCOMPARE", "PWDENCRYPT", "LOGINPROPERTY", "fn_get_audit_file", "fn_xe_file_target_read_file", "fn_xe_target_read_file",
        "fn_dblog", "fn_dump_dblog", "fn_trace_gettable", "fn_get_sql", "CERTPRIVATEKEY");

    /// <summary>
    /// Vistas de compatibilidad (SQL Server 2000) que se resuelven al esquema sys aunque se escriban sin esquema.
    /// Se tratan como catálogo del sistema (RS-POL-08).
    /// </summary>
    private static readonly FrozenSet<string> LegacyCompatibilityViews = Keys(
        "sysobjects", "syscolumns", "sysindexes", "sysindexkeys", "sysusers", "sysmembers", "syspermissions", "sysprotects",
        "syscomments", "sysdepends", "sysfilegroups", "sysfiles", "sysforeignkeys", "sysfulltextcatalogs", "sysreferences",
        "systypes", "sysconstraints", "syssegments", "sysprocesses", "sysdatabases", "syslogins", "sysxlogins", "sysremotelogins",
        "sysoledbusers", "sysservers", "sysconfigures", "syscurconfigs", "syslanguages", "syscharsets", "sysaltfiles",
        "sysdevices", "sysperfinfo", "syscacheobjects", "syslocks", "syslockinfo", "sysmessages", "sysopentapes", "syscursors",
        "syscursorcolumns", "syscursorrefs", "syscursortables", "sysfiles1", "sysfulltextnotify", "syshobtcolumns", "sysrowsets");

    /// <summary>Funciones de tabla globales integradas que se permiten (FROM STRING_SPLIT(...), GENERATE_SERIES(...)).</summary>
    private static readonly FrozenSet<string> AllowedGlobalTableFunctions = Keys("STRING_SPLIT", "GENERATE_SERIES");

    public static bool IsReadOnlySystemProcedure(string name) => ReadOnlySystemProcedures.Contains(PolicyNames.Key(name));

    public static bool IsExtendedPropertyProcedure(string name) => ExtendedPropertyProcedures.Contains(PolicyNames.Key(name));

    public static bool IsRenameProcedure(string name) => PolicyNames.Key(name) == RenameProcedure;

    public static bool IsExecuteSql(string name) => PolicyNames.Key(name) == ExecuteSqlProcedure;

    /// <summary>Prefijo de procedimiento del sistema (sp_ o xp_), en cualquier esquema (RF-PRC-02).</summary>
    public static bool HasSystemProcedurePrefix(string name)
    {
        var key = PolicyNames.Key(name);
        return key.StartsWith("SP_", StringComparison.Ordinal) || key.StartsWith("XP_", StringComparison.Ordinal);
    }

    /// <summary>
    /// Regla inmutable para un procedimiento de la lista de denegación (DCL, administración, acceso externo,
    /// correo/trabajos), comparando solo el nombre base normalizado.
    /// </summary>
    public static PolicyRule? ProcedureRule(string name)
    {
        var key = PolicyNames.Key(name);
        if (ProcedureRules.TryGetValue(key, out var rule))
        {
            return rule;
        }

        if (key.StartsWith("XP_", StringComparison.Ordinal) || key.StartsWith("SP_OA", StringComparison.Ordinal))
        {
            return PolicyRules.ExternalOs;
        }

        return null;
    }

    /// <summary>Vista o función de catálogo sensible (CA-06, RS-POL-08).</summary>
    public static bool IsSensitiveCatalogObject(string? schema, string name)
    {
        var key = PolicyNames.Key(name);
        if (string.IsNullOrEmpty(schema) || PolicyNames.EqualsIgnoreCase(schema, PolicyNames.DefaultSchema))
        {
            return SensitiveLegacyViews.Contains(key) || (string.IsNullOrEmpty(schema) && SensitiveFunctions.Contains(key) && key.StartsWith("FN_", StringComparison.Ordinal));
        }

        return PolicyNames.EqualsIgnoreCase(PolicyNames.Clean(schema), "sys") && SensitiveSysViews.Contains(key);
    }

    public static bool IsSensitiveFunction(string name) => SensitiveFunctions.Contains(PolicyNames.Key(name));

    public static bool IsLegacyCompatibilityView(string name) => LegacyCompatibilityViews.Contains(PolicyNames.Key(name));

    public static bool IsAllowedGlobalTableFunction(string name) => AllowedGlobalTableFunctions.Contains(PolicyNames.Key(name));

    /// <summary>Base de datos msdb (Agente SQL, correo): siempre bloqueada como acceso externo (Anexo C).</summary>
    public static bool IsMsdb(string? database) => database is not null && PolicyNames.Key(database) == "MSDB";

    private static FrozenSet<string> Keys(params string[] names) =>
        names.Select(PolicyNames.Key).ToFrozenSet(StringComparer.Ordinal);

    private static FrozenDictionary<string, PolicyRule> BuildProcedureRules()
    {
        var map = new Dictionary<string, PolicyRule>(StringComparer.Ordinal);

        void Add(PolicyRule rule, params string[] names)
        {
            foreach (var n in names)
            {
                map[PolicyNames.Key(n)] = rule;
            }
        }

        // Sección 5.4: logins y usuarios.
        Add(PolicyRules.DclLoginsUsers,
            "sp_addlogin", "sp_droplogin", "sp_password", "sp_adduser", "sp_dropuser", "sp_grantdbaccess", "sp_revokedbaccess",
            "sp_change_users_login", "sp_defaultdb", "sp_defaultlanguage", "sp_grantlogin", "sp_denylogin", "sp_revokelogin",
            "sp_addalias", "sp_dropalias", "sp_migrate_user_to_contained", "sp_validatelogins");

        // Sección 5.4: roles.
        Add(PolicyRules.DclRoles,
            "sp_addrolemember", "sp_droprolemember", "sp_addsrvrolemember", "sp_dropsrvrolemember", "sp_addrole", "sp_droprole",
            "sp_setapprole", "sp_unsetapprole", "sp_addapprole", "sp_dropapprole", "sp_approlepassword");

        // Sección 5.4: propiedad.
        Add(PolicyRules.DclOwnership, "sp_changedbowner", "sp_changeobjectowner");

        // Criptografía.
        Add(PolicyRules.DclCrypto, "sp_control_dbmasterkey_password");

        // Administración de instancia y bases de datos (Anexo C).
        Add(PolicyRules.AdminInstance,
            "sp_configure", "sp_attach_db", "sp_attach_single_file_db", "sp_detach_db", "sp_dboption", "sp_renamedb",
            "sp_resetstatus", "sp_create_removable", "sp_certify_removable", "sp_addumpdevice", "sp_dropdevice", "sp_procoption",
            "sp_addmessage", "sp_altermessage", "sp_dropmessage", "sp_trace_create", "sp_trace_setevent", "sp_trace_setfilter",
            "sp_trace_setstatus", "sp_cycle_errorlog", "sp_readerrorlog");

        // Servidores vinculados.
        Add(PolicyRules.ExternalLinkedServer,
            "sp_addlinkedserver", "sp_addlinkedsrvlogin", "sp_droplinkedsrvlogin", "sp_dropserver", "sp_addserver", "sp_serveroption",
            "sp_setnetname", "sp_addremotelogin", "sp_dropremotelogin", "sp_remoteoption", "sp_linkedservers", "sp_catalogs",
            "sp_tables_ex", "sp_columns_ex", "sp_testlinkedserver");

        // Sistema operativo, OLE, CLR y scripts externos.
        Add(PolicyRules.ExternalOs,
            "sp_execute_external_script", "sp_addextendedproc", "sp_dropextendedproc", "sp_add_trusted_assembly",
            "sp_drop_trusted_assembly", "sp_xp_cmdshell_proxy_account");

        // Acceso a servicios externos.
        Add(PolicyRules.ExternalData, "sp_invoke_external_rest_endpoint");

        // Correo y trabajos (msdb).
        Add(PolicyRules.ExternalMailJobs,
            "sp_send_dbmail", "xp_sendmail", "sp_add_job", "sp_add_jobstep", "sp_start_job", "sp_update_job", "sp_update_jobstep",
            "sp_add_jobschedule", "sp_add_jobserver", "sp_delete_job", "sp_stop_job", "sp_add_schedule", "sp_attach_schedule",
            "sp_add_operator", "sp_add_alert", "sp_notify_operator", "sp_add_proxy", "sp_grant_proxy_to_subsystem");

        // SQL dinámico: sp_executesql se analiza aparte; como procedimiento estructurado se bloquea.
        Add(PolicyRules.DynamicSqlBlocked, "sp_executesql", "sp_sqlexec");

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
