using System.Net;
using Microsoft.Extensions.Logging;

namespace Wayfinder.Engine.Http;

internal static partial class SupportSystemCallbackLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Support-system callback route {Path}/{{invocationId}} is mapped with NO shared secret — Development only: restricting it to loopback callers. Set a real secret before deploying.")]
    public static partial void UnauthenticatedRouteMapped(this ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected support-system callback for invocation {InvocationId}: missing/invalid secret.")]
    public static partial void RejectedBadSecret(this ILogger logger, string invocationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected support-system callback for invocation {InvocationId}: no shared secret is configured and the caller ({RemoteIp}) is not loopback.")]
    public static partial void RejectedNonLoopback(this ILogger logger, string invocationId, IPAddress? remoteIp);

    [LoggerMessage(Level = LogLevel.Information, Message = "Support-system callback for invocation {InvocationId} was a no-op (unknown or already resolved).")]
    public static partial void CallbackWasNoOp(this ILogger logger, string invocationId);
}
