namespace SqlServerMcp.Infrastructure;

/// <summary>Códigos de error estables (en inglés) devueltos al agente (RF-OUT-03, RF-OUT-05).</summary>
public static class ErrorCodes
{
    public const string PolicyBlocked = "POLICY_BLOCKED";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string ConfirmationDenied = "CONFIRMATION_DENIED";
    public const string ConfirmationUnavailable = "CONFIRMATION_UNAVAILABLE";
    public const string ConfirmationExpired = "CONFIRMATION_EXPIRED";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string ProfileUnavailable = "PROFILE_UNAVAILABLE";
    public const string ModeNotAllowed = "MODE_NOT_ALLOWED";
    public const string RateLimited = "RATE_LIMITED";
    public const string SessionSuspended = "SESSION_SUSPENDED";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
    public const string NotFound = "NOT_FOUND";
    public const string SqlError = "SQL_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string ConnectionError = "CONNECTION_ERROR";
    public const string TransactionError = "TRANSACTION_ERROR";
    public const string InternalError = "INTERNAL_ERROR";
}
