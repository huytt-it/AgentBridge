namespace AgentBridge.Contracts;

/// <summary>Values of <see cref="ErrorInfo.Code"/>. Part of the contract with the cloud: add, never rename.</summary>
public static class ErrorCodes
{
    // rejected: the request was not run.
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string UnknownCapability = "unknown_capability";
    public const string TargetRequired = "target_required";
    public const string UnknownTarget = "unknown_target";
    public const string InvalidArgs = "invalid_args";
    public const string Expired = "expired";
    public const string DuplicateConflict = "duplicate_conflict";

    // timedOut / failed / interrupted: the request was run, at least partly.
    public const string Timeout = "timeout";
    public const string ExecutionFailed = "execution_failed";
    public const string ArtifactTooLarge = "artifact_too_large";
    public const string Interrupted = "interrupted";
    public const string InternalError = "internal_error";
}
