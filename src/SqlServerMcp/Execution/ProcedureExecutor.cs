using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Metadata;

namespace SqlServerMcp.Execution;

/// <summary>
/// Ejecuta procedimientos almacenados autorizados por nombre (RF-PRC-01..04): resuelve el procedimiento y sus parámetros
/// en el catálogo, enlaza argumentos tipados (entrada y salida), ejecuta con <see cref="CommandType.StoredProcedure"/> y
/// devuelve valor de retorno, parámetros de salida y hasta cinco conjuntos de resultados limitados.
/// </summary>
/// <remarks>
/// Rol de lectura: conexión del lector y transacción siempre revertida. Rol de escritura: transacción gestionada que se
/// confirma al final (o punto de guardado de la transacción explícita activa) con el máximo de filas afectadas del perfil.
/// Las excepciones de solo lectura del sistema (sp_help, sp_helptext, sp_describe_first_result_set) se ejecutan siempre con
/// el lector y con tipos de parámetro inferidos.
/// </remarks>
public sealed class ProcedureExecutor : IProcedureExecutor
{
    /// <summary>Regla de política que autoriza las excepciones de solo lectura del sistema (RF-PRC-02).</summary>
    public const string SystemReadOnlyRuleId = "POL-PRC-SYS-RO";

    /// <summary>Máximo de conjuntos de resultados devueltos.</summary>
    public const int MaxResultSets = 5;

    private const string SavepointPrefix = "mcp_prc_";
    private const string ReturnValueParameterName = "@mcp_return_value";

    private static readonly JsonElement NullElement = JsonSerializer.SerializeToElement<object?>(null);

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IParameterBinder _parameterBinder;
    private readonly IResultReader _resultReader;
    private readonly ICatalogLookup _catalogLookup;
    private readonly ITransactionManager _transactions;

    public ProcedureExecutor(
        ISqlConnectionFactory connectionFactory,
        IParameterBinder parameterBinder,
        IResultReader resultReader,
        ICatalogLookup catalogLookup,
        ITransactionManager transactions)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(parameterBinder);
        ArgumentNullException.ThrowIfNull(resultReader);
        ArgumentNullException.ThrowIfNull(catalogLookup);
        ArgumentNullException.ThrowIfNull(transactions);
        _connectionFactory = connectionFactory;
        _parameterBinder = parameterBinder;
        _resultReader = resultReader;
        _catalogLookup = catalogLookup;
        _transactions = transactions;
    }

    /// <inheritdoc />
    public async Task<ProcedureExecutionResult> ExecuteAsync(ProcedureExecutionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var profile = request.Profile;
        var isSystem = IsSystemReadOnly(request);
        var role = isSystem ? ConnectionRole.Read : request.Role;

        if (role == ConnectionRole.Read)
        {
            // Solo lectura: lector y reversión siempre, aunque haya una transacción explícita (mínimo privilegio, RF-CON-07).
            return await TransactionExecution.RunManagedAsync(
                _connectionFactory,
                profile,
                ConnectionRole.Read,
                request.CorrelationId,
                IsolationLevel.Unspecified,
                async (connection, transaction) =>
                {
                    var result = await ExecuteCoreAsync(connection, transaction, request, isSystem, committed: false, cancellationToken).ConfigureAwait(false);
                    return new ManagedOutcome<ProcedureExecutionResult>(result, Commit: false);
                },
                cancellationToken).ConfigureAwait(false);
        }

        var active = _transactions.GetActive(profile);
        if (active is not null)
        {
            return await TransactionExecution.RunExplicitAsync(
                _transactions,
                active,
                profile,
                request.CorrelationId,
                SavepointPrefix,
                countsAsStatement: true,
                async (connection, transaction, _) =>
                {
                    var result = await ExecuteCoreAsync(connection, transaction, request, isSystem: false, committed: false, cancellationToken).ConfigureAwait(false);
                    result.Warnings.Add($"Los cambios forman parte de la transacción explícita {active.TransactionId}: se aplicarán solo al confirmarla.");
                    return result;
                },
                NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);
        }

        return await TransactionExecution.RunManagedAsync(
            _connectionFactory,
            profile,
            ConnectionRole.Write,
            request.CorrelationId,
            IsolationLevel.ReadCommitted,
            async (connection, transaction) =>
            {
                var result = await ExecuteCoreAsync(connection, transaction, request, isSystem: false, committed: true, cancellationToken).ConfigureAwait(false);
                return new ManagedOutcome<ProcedureExecutionResult>(result, Commit: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRequest(ProcedureExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Evaluation);
        if (request.Evaluation.IsBlocked)
        {
            throw new ToolFailureException(
                request.Evaluation.ErrorCode ?? ErrorCodes.PolicyBlocked,
                "La ejecución del procedimiento fue bloqueada por la política.",
                request.Evaluation.Suggestion,
                request.Evaluation.RuleId);
        }

        var name = request.Procedure;
        if (name.Server is not null
            || (name.Database is not null && !string.Equals(name.Database, request.Profile.InitialDatabase, StringComparison.OrdinalIgnoreCase))
            || !SqlNames.IsValidIdentifier(name.Name)
            || (name.Schema is not null && !SqlNames.IsValidIdentifier(name.Schema)))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                "El nombre del procedimiento no es válido: indique 'esquema.procedimiento' de la base de datos del perfil.",
                "Use list_routines para ver los procedimientos disponibles.");
        }
    }

    private static bool IsSystemReadOnly(ProcedureExecutionRequest request) =>
        string.Equals(request.Evaluation.RuleId, SystemReadOnlyRuleId, StringComparison.Ordinal)
        && (request.Procedure.Schema is null || string.Equals(request.Procedure.Schema, "sys", StringComparison.OrdinalIgnoreCase))
        && request.Procedure.Name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase);

    private async Task<ProcedureExecutionResult> ExecuteCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProcedureExecutionRequest request,
        bool isSystem,
        bool committed,
        CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = profile.Limits.CommandTimeoutSeconds;

            List<BoundOutput> outputs;
            if (isSystem)
            {
                command.CommandText = SqlNames.Quote("sys") + "." + SqlNames.Quote(request.Procedure.Name);
                outputs = BindSystemArguments(command, request);
            }
            else
            {
                var procedure = await ResolveProcedureAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
                command.CommandText = SqlNames.Quote(procedure.Schema) + "." + SqlNames.Quote(procedure.Name);
                var parameters = await _catalogLookup.GetParametersAsync(profile, procedure, cancellationToken, connection, transaction).ConfigureAwait(false);
                outputs = BindCatalogArguments(command, request, procedure, parameters);
            }

            var returnValue = new SqlParameter(ReturnValueParameterName, SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            command.Parameters.Add(returnValue);

            var warnings = new List<string>();
            var resultSets = new List<TabularResult>();
            long rowsAffected;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await ReadResultSetsAsync(reader, profile, resultSets, warnings, cancellationToken).ConfigureAwait(false);
                await reader.CloseAsync().ConfigureAwait(false);
                rowsAffected = Math.Max(0, reader.RecordsAffected);
            }

            if (!isSystem && request.Role == ConnectionRole.Write && rowsAffected > profile.Limits.MaxAffectedRows)
            {
                // RF-PRC-04: mismos límites de filas que el DML. La excepción provoca la reversión (transacción o punto de guardado).
                var failure = new ToolFailureException(
                    ErrorCodes.LimitExceeded,
                    $"El procedimiento afectó {rowsAffected.ToString(CultureInfo.InvariantCulture)} filas y el máximo del perfil es " +
                    $"{profile.Limits.MaxAffectedRows.ToString(CultureInfo.InvariantCulture)}: los cambios se revirtieron.",
                    "Ejecute el procedimiento con parámetros que acoten las filas afectadas.");
                failure.Details["rowsAffected"] = rowsAffected;
                throw failure;
            }

            var outputValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in outputs)
            {
                var bare = output.Parameter.ParameterName.TrimStart('@');
                object? value;
                if (profile.IsColumnSensitive(null, null, bare) || profile.IsColumnPiiMasked(null, null, bare))
                {
                    value = output.Parameter.Value is null or DBNull ? null : profile.Config.Sensitive.MaskText;
                }
                else
                {
                    value = ProcedureValueSerializer.Serialize(output.Parameter.Value, output.SqlTypeName, profile.Limits.MaxCellChars, out var truncated);
                    if (truncated)
                    {
                        warnings.Add($"El parámetro de salida {output.Parameter.ParameterName} se truncó a {profile.Limits.MaxCellChars.ToString(CultureInfo.InvariantCulture)} caracteres.");
                    }
                }

                outputValues[output.Parameter.ParameterName] = value;
            }

            if (!isSystem && request.Role == ConnectionRole.Read)
            {
                warnings.Add("Procedimiento de solo lectura: se ejecutó en una transacción que siempre se revierte.");
            }

            return new ProcedureExecutionResult
            {
                ReturnValue = returnValue.Value is int code ? code : null,
                OutputParameters = outputValues,
                ResultSets = resultSets,
                RowsAffected = rowsAffected,
                Committed = committed,
                Warnings = warnings,
            };
        }
    }

    private async Task<CatalogObject> ResolveProcedureAsync(SqlConnection connection, SqlTransaction transaction, ProcedureExecutionRequest request, CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var procedure = await _catalogLookup.FindObjectAsync(profile, request.Procedure, cancellationToken, connection, transaction).ConfigureAwait(false);
        if (procedure is null || !profile.IsSchemaAllowed(procedure.Schema))
        {
            throw new ToolFailureException(
                ErrorCodes.NotFound,
                $"El procedimiento '{request.Procedure.SchemaQualified()}' no existe o no está disponible en este perfil.",
                "Use list_routines para ver los procedimientos disponibles.");
        }

        if (procedure.TypeCode == "PC")
        {
            throw new ToolFailureException(
                ErrorCodes.PolicyBlocked,
                $"'{procedure.QualifiedName}' es un procedimiento CLR; su ejecución no está permitida.",
                null,
                "POL-PRC-CLR");
        }

        if (procedure.TypeCode != "P")
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"'{procedure.QualifiedName}' no es un procedimiento almacenado ({procedure.TypeDescription}).",
                "Para funciones o vistas use run_query.");
        }

        return procedure;
    }

    /// <summary>Excepciones del sistema: tipos inferidos con <see cref="IParameterBinder.Bind"/>; sin parámetros de salida.</summary>
    private List<BoundOutput> BindSystemArguments(SqlCommand command, ProcedureExecutionRequest request)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, argument) in request.Arguments)
        {
            var bare = NormalizeArgumentName(name);
            if (!seen.Add(bare))
            {
                throw DuplicateArgument(bare);
            }

            if (argument.Output)
            {
                throw new ToolFailureException(
                    ErrorCodes.InvalidArgument,
                    $"El parámetro '@{bare}' de {request.Procedure.Name} no admite OUTPUT.",
                    "Quite la marca de salida.");
            }

            var parameter = _parameterBinder.Bind(new QueryParameter("@" + bare, null, argument.Value, argument.Sensitive));
            parameter.Direction = ParameterDirection.Input;
            command.Parameters.Add(parameter);
        }

        return [];
    }

    /// <summary>Valida los argumentos contra sys.parameters y los enlaza con el tipo del catálogo (RF-PRC-01, RF-DML-08).</summary>
    private List<BoundOutput> BindCatalogArguments(SqlCommand command, ProcedureExecutionRequest request, CatalogObject procedure, IReadOnlyList<CatalogParameter> parameters)
    {
        var byName = new Dictionary<string, CatalogParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var bareName = parameter.Name.TrimStart('@');
            if (bareName.Length > 0)
            {
                byName.TryAdd(bareName, parameter);
            }
        }

        var outputs = new List<BoundOutput>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, argument) in request.Arguments)
        {
            var bare = NormalizeArgumentName(name);
            if (!byName.TryGetValue(bare, out var catalogParameter))
            {
                throw new ToolFailureException(
                    ErrorCodes.InvalidArgument,
                    $"El procedimiento {procedure.QualifiedName} no tiene un parámetro '@{bare}'.",
                    "Consulte los parámetros con describe_routine.");
            }

            if (!seen.Add(bare))
            {
                throw DuplicateArgument(bare);
            }

            if (catalogParameter.IsReadOnly || (catalogParameter.Type.IsUserDefined && catalogParameter.Type.BaseTypeName is null))
            {
                throw new ToolFailureException(
                    ErrorCodes.InvalidArgument,
                    $"El parámetro '{catalogParameter.Name}' es de tipo tabla o de un tipo definido por el usuario no escalar; no está soportado.",
                    "Use un procedimiento con parámetros escalares.");
            }

            if (argument.Output && !catalogParameter.IsOutput)
            {
                throw new ToolFailureException(
                    ErrorCodes.InvalidArgument,
                    $"El parámetro '{catalogParameter.Name}' no está declarado como OUTPUT en {procedure.QualifiedName}.",
                    "Quite la marca de salida o consulte describe_routine.");
            }

            var value = argument.Output && argument.Value.ValueKind == JsonValueKind.Undefined ? NullElement : argument.Value;
            var sqlParameter = _parameterBinder.BindForCatalogType(catalogParameter.Name, catalogParameter.Type, value);
            sqlParameter.ParameterName = catalogParameter.Name.StartsWith('@') ? catalogParameter.Name : "@" + catalogParameter.Name;
            if (argument.Output)
            {
                sqlParameter.Direction = ParameterDirection.InputOutput;
                ApplyOutputSize(sqlParameter, catalogParameter.Type);
                outputs.Add(new BoundOutput(sqlParameter, (catalogParameter.Type.BaseTypeName ?? catalogParameter.Type.TypeName).ToLowerInvariant()));
            }
            else
            {
                sqlParameter.Direction = ParameterDirection.Input;
            }

            command.Parameters.Add(sqlParameter);
        }

        return outputs;
    }

    /// <summary>Tamaño, precisión y escala de los parámetros de salida según el catálogo.</summary>
    private static void ApplyOutputSize(SqlParameter parameter, CatalogType type)
    {
        switch ((type.BaseTypeName ?? type.TypeName).ToLowerInvariant())
        {
            case "varchar" or "char" or "varbinary" or "binary":
                parameter.Size = type.MaxLength == -1 ? -1 : type.MaxLength;
                break;
            case "nvarchar" or "nchar":
                parameter.Size = type.MaxLength == -1 ? -1 : type.MaxLength / 2;
                break;
            case "xml" or "text" or "ntext" or "image":
                parameter.Size = -1;
                break;
            case "decimal" or "numeric":
                parameter.Precision = type.Precision;
                parameter.Scale = type.Scale;
                break;
            case "datetime2" or "datetimeoffset" or "time":
                parameter.Scale = type.Scale;
                break;
        }
    }

    /// <summary>
    /// Lee hasta <see cref="MaxResultSets"/> conjuntos, cada uno con <see cref="LimitsConfig.MaxRows"/>, sin superar en total
    /// <see cref="LimitsConfig.MaxResponseBytes"/>; el resto se descarta sin cargarlo (advirtiendo cuántos se omitieron).
    /// </summary>
    private async Task ReadResultSetsAsync(SqlDataReader reader, ProfileRuntime profile, List<TabularResult> resultSets, List<string> warnings, CancellationToken cancellationToken)
    {
        long budget = profile.Limits.MaxResponseBytes;
        long usedBytes = 0;
        var dropped = 0;
        do
        {
            if (reader.FieldCount == 0)
            {
                continue;
            }

            if (resultSets.Count >= MaxResultSets || usedBytes >= budget)
            {
                dropped++;
                continue;
            }

            var set = await _resultReader.ReadAsync(reader, profile, profile.Limits.MaxRows, 0, [], cancellationToken).ConfigureAwait(false);
            TrimToBudget(set, budget - usedBytes);
            usedBytes += set.ApproximateBytes;
            resultSets.Add(set);
            if (set.Truncated)
            {
                warnings.Add($"El conjunto de resultados {resultSets.Count.ToString(CultureInfo.InvariantCulture)} se truncó ({set.TruncationReason ?? "límite"}).");
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        if (dropped > 0)
        {
            warnings.Add(
                $"Se omitieron {dropped.ToString(CultureInfo.InvariantCulture)} conjuntos de resultados adicionales " +
                $"(máximo {MaxResultSets.ToString(CultureInfo.InvariantCulture)} conjuntos y {profile.Limits.MaxResponseBytes.ToString(CultureInfo.InvariantCulture)} bytes por respuesta).");
        }
    }

    /// <summary>Recorta filas del final hasta que el conjunto quepa en el presupuesto de bytes restante.</summary>
    internal static void TrimToBudget(TabularResult set, long remainingBytes)
    {
        if (set.ApproximateBytes <= remainingBytes)
        {
            return;
        }

        long used = 0;
        var keep = 0;
        foreach (var row in set.Rows)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(row).Length + 1;
            if (used + size > remainingBytes)
            {
                break;
            }

            used += size;
            keep++;
        }

        set.Rows.RemoveRange(keep, set.Rows.Count - keep);
        set.ApproximateBytes = used;
        set.Truncated = true;
        set.TruncationReason = "maxResponseBytes";
    }

    private static string NormalizeArgumentName(string name)
    {
        var bare = (name ?? string.Empty).Trim().TrimStart('@');
        if (!SqlNames.IsValidIdentifier(bare))
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                "Hay un nombre de parámetro no válido.",
                "Use los nombres que devuelve describe_routine, con o sin '@'.");
        }

        return bare;
    }

    private static ToolFailureException DuplicateArgument(string bare) => new(
        ErrorCodes.InvalidArgument,
        $"El parámetro '@{bare}' se indicó más de una vez.",
        "Indique cada parámetro una sola vez.");

    private sealed record BoundOutput(SqlParameter Parameter, string SqlTypeName);
}
