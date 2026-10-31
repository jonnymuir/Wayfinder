using System.Globalization;
using System.Text.Json.Nodes;
using Wayfinder.Engine.Configuration;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Engine.SupportSystems;

/// <summary>
/// Turns a capability's configured <c>mock</c> block into the answers a
/// <see cref="MockSupportSystemClient"/> gives, failing at startup when an answer names an outcome,
/// output or input the capability does not declare.
/// </summary>
internal static class MockSupportSystemAnswers
{
    public static MockSupportSystemCapability Build(
        string supportSystemKey,
        WebhookSupportSystemCapabilityOptions options,
        SupportSystemCapabilityDescriptor capability)
    {
        var where = $"Mocked support system '{supportSystemKey}' capability '{options.Key}'";
        var mock = options.Mock
            ?? throw new InvalidOperationException($"{where} has no 'mock' answer.");

        return new MockSupportSystemCapability
        {
            Default = ToMockAnswer(where, mock, capability),
            Cases = mock.Cases.Select((c, i) =>
            {
                var caseWhere = $"{where}, case {i + 1},";
                var unknownInput = c.When.Keys.FirstOrDefault(k => capability.Inputs.All(input => input.Key != k));
                if (unknownInput is not null)
                {
                    throw new InvalidOperationException(
                        $"{caseWhere} matches on '{unknownInput}', which is not one of its declared inputs.");
                }

                return new MockSupportSystemCase { When = c.When, Answer = ToMockAnswer(caseWhere, c, capability) };
            }).ToArray(),
        };
    }

    private static MockSupportSystemAnswer ToMockAnswer(
        string where, MockSupportSystemAnswerOptions answer, SupportSystemCapabilityDescriptor capability)
    {
        if (capability.Outcomes.All(o => o.Key != answer.Outcome))
        {
            throw new InvalidOperationException(
                $"{where} answers with outcome '{answer.Outcome}', which is not one of its declared outcomes " +
                $"({string.Join(", ", capability.Outcomes.Select(o => o.Key))}).");
        }

        var outputs = new JsonObject();
        foreach (var (key, value) in answer.Outputs)
        {
            var output = capability.Outputs.FirstOrDefault(o => o.Key == key)
                ?? throw new InvalidOperationException(
                    $"{where} sets output '{key}', which is not one of its declared outputs.");
            outputs[key] = ToJsonValue(where, output, value);
        }

        return new MockSupportSystemAnswer { OutcomeKey = answer.Outcome, Outputs = outputs.Count > 0 ? outputs : null };
    }

    // Configuration binds every value as text; convert to what the output declares so a
    // calculation reading it sees a number or a boolean.
    private static JsonValue? ToJsonValue(string where, ComponentPropertyDescriptor output, string? value)
    {
        if (value is null)
        {
            return null;
        }

        return output.ValueKind switch
        {
            ComponentPropertyValueKind.Integer when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonValue.Create(integer),
            ComponentPropertyValueKind.Number when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
            ComponentPropertyValueKind.Boolean when bool.TryParse(value, out var boolean) => JsonValue.Create(boolean),
            ComponentPropertyValueKind.Integer or ComponentPropertyValueKind.Number or ComponentPropertyValueKind.Boolean =>
                throw new InvalidOperationException(
                    $"{where} sets output '{output.Key}' to '{value}', which is not a valid {output.ValueKind}."),
            _ => JsonValue.Create(value),
        };
    }
}
