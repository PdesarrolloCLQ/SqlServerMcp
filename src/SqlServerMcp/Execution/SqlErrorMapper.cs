using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>Datos de un error de SQL Server, independientes del driver (para mapeo y pruebas).</summary>
/// <param name="Number">Número de error (-2 = tiempo de espera del cliente).</param>
/// <param name="Class">Severidad.</param>
/// <param name="State">Estado.</param>
/// <param name="LineNumber">Línea del lote o procedimiento.</param>
/// <param name="Procedure">Procedimiento o desencadenador donde ocurrió, si aplica.</param>
/// <param name="Message">Mensaje original del servidor.</param>
public sealed record SqlErrorInfo(int Number, byte Class, byte State, int LineNumber, string? Procedure, string Message);

/// <summary>
/// Convierte errores de SQL Server en <see cref="ToolFailureException"/> con número, severidad, estado, línea,
/// procedimiento y mensaje (RF-OUT-02). Códigos: TIMEOUT (-2, RS-LIM-02), CONNECTION_ERROR (red, inicio de sesión,
/// errores fatales) o SQL_ERROR. Los mensajes se sanean: nunca contienen contraseñas, cadenas de conexión ni rutas
/// (RF-OUT-04, RS-SEC-03).
/// </summary>
public sealed partial class SqlErrorMapper
{
    /// <summary>Número que el driver asigna al tiempo de espera de comando agotado.</summary>
    public const int TimeoutNumber = -2;

    private const int MaxMessageLength = 2000;
    private const int MaxReportedErrors = 10;

    /// <summary>Errores de red, de inicio de sesión o de disponibilidad de la base de datos.</summary>
    private static readonly HashSet<int> ConnectionErrorNumbers =
    [
        -1, 2, 20, 26, 35, 40, 53, 64, 121, 233, 258, 1231, 1232, 4060, 4064, 10053, 10054, 10060, 10061, 10065, 11001, 11004,
        18452, 18456, 18470, 18486, 18487, 18488, 40143, 40197, 40501, 40613, 42108, 42109, 49918, 49919, 49920,
    ];

    /// <summary>Errores de autenticación: el mensaje original se sustituye para no revelar detalles de seguridad.</summary>
    private static readonly HashSet<int> LoginErrorNumbers = [18452, 18456, 18470, 18486, 18487, 18488];

    private SqlErrorMapper()
    {
    }

    /// <summary>Convierte una <see cref="SqlException"/> en un error de herramienta.</summary>
    /// <param name="exception">Excepción del driver.</param>
    /// <param name="commandTimeoutSeconds">Tiempo máximo aplicado, para el mensaje de TIMEOUT.</param>
    public static ToolFailureException Map(SqlException exception, int? commandTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var errors = new List<SqlErrorInfo>();
        foreach (SqlError error in exception.Errors)
        {
            errors.Add(new SqlErrorInfo(error.Number, error.Class, error.State, error.LineNumber, error.Procedure, error.Message ?? string.Empty));
        }

        if (errors.Count == 0)
        {
            errors.Add(new SqlErrorInfo(exception.Number, exception.Class, exception.State, exception.LineNumber, exception.Procedure, exception.Message));
        }

        return Map(errors, commandTimeoutSeconds);
    }

    /// <summary>Mapeo a partir de los errores ya extraídos (el primero es el principal).</summary>
    public static ToolFailureException Map(IReadOnlyList<SqlErrorInfo> errors, int? commandTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            return new ToolFailureException(ErrorCodes.SqlError, "SQL Server devolvió un error sin detalles.");
        }

        var primary = SelectPrimary(errors);
        ToolFailureException failure;
        if (errors.Any(e => e.Number == TimeoutNumber))
        {
            var seconds = commandTimeoutSeconds is > 0
                ? $" ({commandTimeoutSeconds.Value.ToString(CultureInfo.InvariantCulture)} s)"
                : string.Empty;
            failure = new ToolFailureException(
                ErrorCodes.Timeout,
                $"La sentencia superó el tiempo máximo de ejecución{seconds} y se canceló en el servidor.",
                "Agregue filtros o TOP, revise el plan con get_execution_plan o divida la consulta.");
            primary = errors.First(e => e.Number == TimeoutNumber);
        }
        else if (IsConnectionError(primary))
        {
            var login = LoginErrorNumbers.Contains(primary.Number);
            failure = new ToolFailureException(
                ErrorCodes.ConnectionError,
                login
                    ? $"SQL Server rechazó el inicio de sesión del perfil (error {primary.Number.ToString(CultureInfo.InvariantCulture)})."
                    : $"No fue posible conectar con SQL Server o la conexión se interrumpió (error {primary.Number.ToString(CultureInfo.InvariantCulture)}).",
                login
                    ? "Verifique la credencial configurada para el perfil; el agente no puede corregir este error."
                    : "Verifique que el servidor esté disponible y reintente; si persiste, consulte al administrador.");
        }
        else
        {
            var message = Sanitize(primary.Message);
            failure = new ToolFailureException(
                ErrorCodes.SqlError,
                $"Error de SQL Server {primary.Number.ToString(CultureInfo.InvariantCulture)} (severidad {primary.Class.ToString(CultureInfo.InvariantCulture)}, " +
                $"estado {primary.State.ToString(CultureInfo.InvariantCulture)}, línea {primary.LineNumber.ToString(CultureInfo.InvariantCulture)}): {message}",
                SuggestionFor(primary.Number));
        }

        var safeMessage = LoginErrorNumbers.Contains(primary.Number) ? "Inicio de sesión rechazado." : Sanitize(primary.Message);
        failure.Details["number"] = primary.Number;
        failure.Details["severity"] = (int)primary.Class;
        failure.Details["state"] = (int)primary.State;
        failure.Details["line"] = primary.LineNumber;
        failure.Details["procedure"] = string.IsNullOrWhiteSpace(primary.Procedure) ? null : primary.Procedure;
        failure.Details["message"] = safeMessage;
        if (errors.Count > 1)
        {
            failure.Details["errors"] = errors
                .Take(MaxReportedErrors)
                .Select(e => new Dictionary<string, object?>
                {
                    ["number"] = e.Number,
                    ["severity"] = (int)e.Class,
                    ["state"] = (int)e.State,
                    ["line"] = e.LineNumber,
                    ["message"] = LoginErrorNumbers.Contains(e.Number) ? "Inicio de sesión rechazado." : Sanitize(e.Message),
                })
                .ToList();
        }

        return failure;
    }

    /// <summary>True si el error corresponde a un tiempo de espera de comando agotado (RS-LIM-02).</summary>
    public static bool IsTimeout(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number == TimeoutNumber)
            {
                return true;
            }
        }

        return exception.Number == TimeoutNumber;
    }

    /// <summary>True si el error impide usar la conexión (red, inicio de sesión, base no disponible o severidad ≥ 20).</summary>
    public static bool IsConnectionError(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsConnectionError(new SqlErrorInfo(exception.Number, exception.Class, exception.State, exception.LineNumber, exception.Procedure, exception.Message));
    }

    /// <summary>
    /// Sanea un mensaje del servidor o del driver: oculta valores de Password/Pwd (y otras claves de secreto),
    /// rutas de archivos locales o UNC, y lo acota en longitud (RF-OUT-04).
    /// </summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var result = SecretPairs().Replace(message, m => m.Groups["key"].Value + "=***");
        result = UncPaths().Replace(result, "<ruta>");
        result = DrivePaths().Replace(result, "<ruta>");
        if (result.Length > MaxMessageLength)
        {
            result = result[..MaxMessageLength] + "…";
        }

        return result;
    }

    private static bool IsConnectionError(SqlErrorInfo error) =>
        ConnectionErrorNumbers.Contains(error.Number) || error.Class >= 20;

    /// <summary>El primer error con severidad ≥ 11 (los de menor severidad son informativos).</summary>
    private static SqlErrorInfo SelectPrimary(IReadOnlyList<SqlErrorInfo> errors)
    {
        foreach (var error in errors)
        {
            if (error.Class >= 11)
            {
                return error;
            }
        }

        return errors[0];
    }

    private static string? SuggestionFor(int number) => number switch
    {
        102 or 105 or 156 or 170 or 319 or 1038 => "Revise la sintaxis de la sentencia; puede validarla antes con validate_sql.",
        207 => "Verifique los nombres de columna con describe_table.",
        208 => "Verifique el nombre del objeto (esquema.objeto) con list_tables o search_objects.",
        4104 => "Revise los alias y los nombres de columnas calificados.",
        209 => "Califique la columna ambigua con el alias de su tabla.",
        229 or 230 or 297 or 300 => "El login del perfil no tiene permiso sobre el objeto; solicite al DBA el permiso necesario.",
        262 => "El login de lectura necesita el permiso SHOWPLAN; solicite al DBA: GRANT SHOWPLAN TO <rol de lectura del MCP>.",
        8134 => "Evite la división por cero, por ejemplo con NULLIF(divisor, 0).",
        241 or 242 or 245 or 8114 or 8115 or 220 or 232 or 517 or 9809 => "Revise los tipos de datos y conversiones (TRY_CAST / TRY_CONVERT).",
        1205 => "La sentencia fue elegida como víctima de un interbloqueo; reintente.",
        1222 => "Se agotó el tiempo de espera de bloqueos (LOCK_TIMEOUT); reintente más tarde o reduzca el alcance de la consulta.",
        3951 or 3952 => "La base de datos no permite aislamiento SNAPSHOT; el administrador debe habilitarlo o cambiar el aislamiento del perfil.",
        201 or 8144 or 8145 or 8146 => "Revise los parámetros con describe_procedure.",
        515 => "Una columna NOT NULL recibió NULL; proporcione un valor.",
        547 => "La sentencia viola una restricción FOREIGN KEY o CHECK.",
        2601 or 2627 => "La sentencia viola una restricción de unicidad (clave duplicada).",
        8152 or 2628 => "Un valor excede la longitud de la columna de destino.",
        _ => null,
    };

    [GeneratedRegex(@"(?i)\b(?<key>password|pwd|secret|token|access\s*token|accountkey|sharedaccesssignature)\s*=\s*(""[^""]*""|'[^']*'|\{[^}]*\}|[^;'""\s,)]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPairs();

    [GeneratedRegex(@"\\\\[^\s'""<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPaths();

    [GeneratedRegex(@"\b[A-Za-z]:\\[^\s'""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePaths();
}
