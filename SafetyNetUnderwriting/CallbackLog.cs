namespace SafetyNetUnderwriting;

internal static partial class CallbackLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Callback to {CallbackUrl} for submission {SubmissionId} succeeded ({Status}).")]
    public static partial void CallbackSucceeded(this ILogger logger, string callbackUrl, string submissionId, int status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Callback to {CallbackUrl} for submission {SubmissionId} returned {Status}. Wayfinder's poll fallback will have to cover this.")]
    public static partial void CallbackRejected(this ILogger logger, string callbackUrl, string submissionId, int status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Callback to {CallbackUrl} for submission {SubmissionId} failed outright. Wayfinder's poll fallback will have to cover this.")]
    public static partial void CallbackFailed(this ILogger logger, Exception exception, string callbackUrl, string submissionId);
}
