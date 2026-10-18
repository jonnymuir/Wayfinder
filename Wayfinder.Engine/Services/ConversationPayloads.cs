using Wayfinder.Models.ServiceDesign;
using static Wayfinder.Engine.Services.BlueprintLookup;

namespace Wayfinder.Engine.Services;

/// <summary>
/// Turns a conversation field's stored thread into messages labelled for whoever the stage is being
/// rendered for, and leaves its box empty for the next message. A field elsewhere that only echoes a
/// conversation (a summary row, say) shows its latest message rather than the stored thread.
/// </summary>
internal static class ConversationPayloads
{
    private const string FieldType = "conversation";
    private const string OwnSideLabel = "You";

    /// <param name="components">The stage's rendered components.</param>
    /// <param name="definition">The blueprint, for the queues' display names.</param>
    /// <param name="stage">The stage being rendered: its queue is the viewer's side of the conversation.</param>
    public static ComponentRenderPayload[] Prepare(
        ComponentRenderPayload[] components, ServiceBlueprint definition, StageDefinition stage)
    {
        var viewerQueue = GetQueueKey(stage) ?? "";
        return components
            .Select(component => component with
            {
                Fields = PrepareFields(component.Fields, definition, stage, viewerQueue),
                AccordionSections = component.AccordionSections?
                    .Select(section => section with { Fields = PrepareFields(section.Fields, definition, stage, viewerQueue) })
                    .ToArray()
            })
            .ToArray();
    }

    private static FieldRenderPayload[] PrepareFields(
        IReadOnlyList<FieldRenderPayload> fields, ServiceBlueprint definition, StageDefinition stage, string viewerQueue) =>
        fields.Select(field => PrepareField(field, definition, stage, viewerQueue)).ToArray();

    private static FieldRenderPayload PrepareField(
        FieldRenderPayload field, ServiceBlueprint definition, StageDefinition stage, string viewerQueue)
    {
        var thread = ConversationEntry.ReadThread(field.Value);
        if (field.FieldType == FieldType)
        {
            return field with
            {
                Value = field.Value is string draft ? draft : "",
                MaxLength = ConversationThreads.MaxLength(stage, field.FieldKey),
                Conversation = thread.Select(entry => Label(entry, definition, viewerQueue)).ToArray()
            };
        }

        return thread.Count > 0 ? field with { Value = thread[^1].Text } : field;
    }

    private static ConversationMessagePayload Label(ConversationEntry entry, ServiceBlueprint definition, string viewerQueue)
    {
        var isOwnSide = string.Equals(entry.QueueKey, viewerQueue, StringComparison.Ordinal);
        var queueName = GetQueues(definition)
            .FirstOrDefault(q => string.Equals(q.Key, entry.QueueKey, StringComparison.Ordinal))?.DisplayName;
        return new ConversationMessagePayload
        {
            Text = entry.Text,
            At = entry.At,
            IsOwnSide = isOwnSide,
            AuthorLabel = isOwnSide ? OwnSideLabel : FirstNonEmpty(queueName, entry.QueueKey) ?? ""
        };
    }
}
