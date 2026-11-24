using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Execution;

/// <summary>
/// Enlaza la lista completa de parámetros de una invocación (RF-QRY-02): máximo <see cref="ParameterBinder.MaxParameters"/>
/// y sin nombres repetidos. Cada valor se valida con <see cref="IParameterBinder"/>; nunca se concatena al SQL.
/// </summary>
public static class QueryParameterBinding
{
    /// <summary>Devuelve parámetros nuevos (un SqlParameter solo puede pertenecer a un comando).</summary>
    public static SqlParameter[] BindAll(IParameterBinder binder, IReadOnlyList<QueryParameter>? parameters)
    {
        ArgumentNullException.ThrowIfNull(binder);
        if (parameters is null || parameters.Count == 0)
        {
            return [];
        }

        if (parameters.Count > ParameterBinder.MaxParameters)
        {
            throw new ToolFailureException(
                ErrorCodes.InvalidArgument,
                $"Se recibieron {parameters.Count.ToString(CultureInfo.InvariantCulture)} parámetros; el máximo por invocación es {ParameterBinder.MaxParameters.ToString(CultureInfo.InvariantCulture)}.",
                "Reduzca el número de parámetros o divida la operación.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bound = new SqlParameter[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i] ?? throw new ToolFailureException(ErrorCodes.InvalidArgument, "La lista de parámetros contiene un elemento nulo.");
            var sqlParameter = binder.Bind(parameter);
            if (!names.Add(sqlParameter.ParameterName))
            {
                throw new ToolFailureException(
                    ErrorCodes.InvalidArgument,
                    $"El parámetro '{sqlParameter.ParameterName}' está repetido.",
                    "Cada parámetro debe aparecer una sola vez.");
            }

            bound[i] = sqlParameter;
        }

        return bound;
    }
}
