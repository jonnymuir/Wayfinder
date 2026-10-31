using Wayfinder.Engine.Models;

namespace Wayfinder.Engine.Services;

internal static class CursorMoves
{
    public static IReadOnlyList<RequestCursor> MoveCursor(
        IReadOnlyList<RequestCursor> cursors,
        string? cursorId,
        string newNodeKey,
        bool isAtGateway,
        string? arrivedViaAction = null,
        string? newQueueKey = null)
    {
        if (cursorId == null)
            return cursors;
        return cursors
            .Select(c => c.CursorId == cursorId
                ? c with
                {
                    CurrentNodeKey = newNodeKey,
                    IsAtGateway = isAtGateway,
                    ArrivedViaAction = isAtGateway ? arrivedViaAction : null,
                    ArrivedAt = DateTimeOffset.UtcNow,
                    QueueKey = newQueueKey ?? c.QueueKey,
                    AssignedTo = newQueueKey is null || newQueueKey == c.QueueKey ? c.AssignedTo : null
                }
                : c)
            .ToArray();
    }
}
