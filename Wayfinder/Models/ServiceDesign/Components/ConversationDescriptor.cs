namespace Wayfinder.Models.ServiceDesign.Components;

internal static class ConversationDescriptor
{
    internal static ComponentDescriptor Build() => new()
    {
        Discriminator = "conversation", DisplayName = "Conversation", Category = ComponentCategory.Input,
        Description = "A running conversation: shows every earlier message with who wrote it and when, and a box " +
            "for the next one, which is added to the end on submit. Use the same field key on a stage in each " +
            "queue that takes part.",
        ClrType = typeof(ConversationComponent), IsInput = true,
        Properties =
        [
            .. BuiltInComponentDescriptors.InputBaseProperties(),
            BuiltInComponentDescriptors.Prop(nameof(ConversationComponent.MaxLength), "Maximum message length",
                ComponentPropertyValueKind.Number, $"Characters per message. Defaults to {ConversationComponent.DefaultMaxLength}."),
        ],
    };
}
