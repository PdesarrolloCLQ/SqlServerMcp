using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Plan de ejecución estimado sin ejecutar la sentencia (RF-QRY-09): SET SHOWPLAN_XML ON en su propio lote, la
/// sentencia, lectura del XML y SET SHOWPLAN_XML OFF siempre al final. Usa el login de lectura (requiere el
/// permiso SHOWPLAN).
/// <para>
/// Limitación documentada: SQL Server no devuelve el plan de un comando parametrizado enviado por RPC
/// (sp_executesql) bajo SHOWPLAN_XML. Por eso, si hay parámetros, se antepone en la misma línea un
/// <c>DECLARE @p tipo, …;</c> construido solo con nombres y tipos ya validados (nunca valores, RS-INJ-01); el plan se
/// estima sin conocer los valores (como variables locales).
/// </para>
/// </summary>
public sealed partial class ExecutionPlanService : IExecutionPlanService
{
    /// <summary>Caracteres máximos del XML que se leen y analizan (protege la memoria del proceso).</summary>
    internal const int MaxPlanChars = 16 * 1024 * 1024;

    private const int CharChunk = 8192;

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IParameterBinder _parameterBinder;

    public ExecutionPlanService(ISqlConnectionFactory connectionFactory, IParameterBinder parameterBinder)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _parameterBinder = parameterBinder ?? throw new ArgumentNullException(nameof(parameterBinder));
    }

    /// <inheritdoc />
    public async Task<ExecutionPlanResult> GetEstimatedPlanAsync(
        ProfileRuntime profile,
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        string correlationId,
        bool includeXml,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "La sentencia está vacía.");
        }

        var bound = QueryParameterBinding.BindAll(_parameterBinder, parameters);
        var batch = BuildBatch(sql, bound);

        string? planXml;
        bool tooLarge;
        try
        {
            var connection = await _connectionFactory.OpenAsync(profile, ConnectionRole.Read, correlationId, cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                (planXml, tooLarge) = await CaptureShowplanAsync(connection, batch, profile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw SqlErrorMapper.Map(ex, profile.Limits.CommandTimeoutSeconds);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is SqlException or InvalidOperationException)
        {
            throw new OperationCanceledException("La obtención del plan se canceló.", ex, cancellationToken);
        }

        if (planXml is null)
        {
            throw new ToolFailureException(
                ErrorCodes.SqlError,
                "SQL Server no devolvió un plan de ejecución para la sentencia.",
                "Verifique que se trate de una única sentencia SELECT.");
        }

        var analysis = tooLarge
            ? new ExecutionPlanResult { Warnings = ["El plan es demasiado grande para resumirlo; revise el XML (recortado) o simplifique la consulta."] }
            : PlanXmlAnalyzer.Analyze(planXml);

        var warnings = new List<string>(analysis.Warnings);
        if (bound.Length > 0)
        {
            warnings.Add("Los parámetros se declararon como variables locales sin valor: las filas estimadas usan densidades promedio y pueden diferir de la ejecución real.");
        }

        string? xml = null;
        var xmlTruncated = false;
        if (includeXml)
        {
            (xml, xmlTruncated) = TruncateUtf8(planXml, Math.Max(1, profile.Limits.MaxResponseBytes));
            xmlTruncated |= tooLarge;
            if (xmlTruncated)
            {
                warnings.Add("El XML del plan se recortó al tamaño máximo de respuesta.");
            }
        }

        return new ExecutionPlanResult
        {
            EstimatedCost = analysis.EstimatedCost,
            EstimatedRows = analysis.EstimatedRows,
            CostlyOperators = analysis.CostlyOperators,
            Warnings = warnings,
            MissingIndexes = analysis.MissingIndexes,
            PlanXml = xml,
            PlanXmlTruncated = xmlTruncated,
        };
    }

    /// <summary>
    /// Lote para SHOWPLAN: la sentencia tal cual o precedida por <c>DECLARE</c> de los parámetros en la misma línea
    /// (conserva los números de línea de los errores). Solo nombres validados y tipos, nunca valores.
    /// </summary>
    internal static string BuildBatch(string sql, IReadOnlyList<SqlParameter> parameters)
    {
        if (parameters.Count == 0)
        {
            return sql;
        }

        var builder = new StringBuilder("DECLARE ");
        for (var i = 0; i < parameters.Count; i++)
        {
            var name = parameters[i].ParameterName;
            if (!SafeParameterName().IsMatch(name))
            {
                throw new ToolFailureException(ErrorCodes.InvalidArgument, "Nombre de parámetro no válido para el plan de ejecución.");
            }

            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(name).Append(' ').Append(ParameterBinder.GetDeclaration(parameters[i]));
        }

        return builder.Append("; ").Append(sql).ToString();
    }

    /// <summary>Recorta un texto para que su codificación UTF-8 no supere <paramref name="maxBytes"/> bytes.</summary>
    internal static (string Text, bool Truncated) TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return (text, false);
        }

        var bytes = 0;
        var i = 0;
        while (i < text.Length)
        {
            int length;
            int step;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                length = 4;
                step = 2;
            }
            else
            {
                length = text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3;
                step = 1;
            }

            if (bytes + length > maxBytes)
            {
                break;
            }

            bytes += length;
            i += step;
        }

        return (text[..i], true);
    }

    private static async Task<(string? Xml, bool TooLarge)> CaptureShowplanAsync(SqlConnection connection, string batch, ProfileRuntime profile, CancellationToken cancellationToken)
    {
        try
        {
            // SET SHOWPLAN_XML debe ser la única instrucción de su lote.
            await ExecuteSetAsync(connection, "SET SHOWPLAN_XML ON", profile, cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = batch;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = Math.Max(1, profile.Limits.CommandTimeoutSeconds);
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    string? xml = null;
                    var tooLarge = false;
                    do
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            if (xml is not null || reader.FieldCount == 0 || await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                            {
                                continue;
                            }

                            (xml, tooLarge) = ReadPlanText(reader);
                        }
                    }
                    while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                    return (xml, tooLarge);
                }
            }
        }
        finally
        {
            try
            {
                await ExecuteSetAsync(connection, "SET SHOWPLAN_XML OFF", profile, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                // Si no se pudo desactivar, la conexión no debe volver a usarse desde el pool.
                SqlConnection.ClearPool(connection);
            }
        }
    }

    private static (string Xml, bool TooLarge) ReadPlanText(SqlDataReader reader)
    {
        var buffer = new char[CharChunk];
        var builder = new StringBuilder();
        long offset = 0;
        while (builder.Length <= MaxPlanChars)
        {
            var wanted = Math.Min(buffer.Length, MaxPlanChars + 1 - builder.Length);
            var read = reader.GetChars(0, offset, buffer, 0, wanted);
            if (read <= 0)
            {
                break;
            }

            builder.Append(buffer, 0, (int)read);
            offset += read;
        }

        return builder.Length > MaxPlanChars ? (builder.ToString(0, MaxPlanChars), true) : (builder.ToString(), false);
    }

    private static async Task ExecuteSetAsync(SqlConnection connection, string statement, ProfileRuntime profile, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = statement;
            command.CommandTimeout = Math.Max(5, profile.Limits.CommandTimeoutSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [GeneratedRegex(@"^@[\p{L}_#][\p{L}\p{Nd}_@#$]{0,126}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeParameterName();
}
