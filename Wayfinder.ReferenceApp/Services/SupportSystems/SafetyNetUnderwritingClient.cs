using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.ReferenceApp.Services.SupportSystems;

/// <summary>
/// Registers the <see cref="SupportSystemDescriptor"/> for SafetyNet Underwriting — a fictional
/// insurer, standing in for NN/g's "support processes" layer
/// (https://www.nngroup.com/articles/service-blueprints-definition/), the third lane alongside
/// the citizen and caseworker queues. See docs/guides/support-systems.md and the real running
/// app at <c>SafetyNetUnderwriting/Program.cs</c> — a genuinely separate ASP.NET Core project,
/// not a library inside this host.
/// </summary>
public static class SafetyNetUnderwriting
{
    public const string SupportSystemKey = "safetynet-underwriting";
    public const string ValidateRiskAssessmentCapability = "validate-risk-assessment";
    public const string ApprovedOutcome = "approved";
    public const string RejectedOutcome = "rejected";

    /// <summary>Named <see cref="IHttpClientFactory"/> client key for <see cref="SafetyNetUnderwritingClient"/>'s own HttpClient.</summary>
    public const string HttpClientName = "safetynet-underwriting";

    public static void Register() =>
        SupportSystemRegistry.Register(new SupportSystemDescriptor
        {
            Key = SupportSystemKey,
            DisplayName = "SafetyNet Underwriting",
            Description = "A fictional insurer that validates a juggling licence applicant's risk assessment.",
            Capabilities =
            [
                new SupportSystemCapabilityDescriptor
                {
                    Key = ValidateRiskAssessmentCapability,
                    DisplayName = "Validate a risk assessment",
                    Description = "Sends the applicant's risk assessment (and event context) to SafetyNet " +
                                  "Underwriting's own staff queue for a human approve/reject decision.",
                    Inputs =
                    [
                        new()
                        {
                            Key = "File", Title = "Risk assessment file",
                            Description = "The file-upload field carrying the applicant's risk assessment.",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref", Required = true,
                        },
                        new()
                        {
                            Key = "ApplicantName", Title = "Applicant name",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref",
                        },
                        new()
                        {
                            Key = "EventName", Title = "Event name",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref",
                        },
                        new()
                        {
                            Key = "Notes", Title = "Risk mitigation notes",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref",
                        },
                    ],
                    Outputs =
                    [
                        new()
                        {
                            Key = "insurerDecision", Title = "Insurer decision",
                            Description = "Mirrors the resolved outcome key (\"approved\"/\"rejected\") as a displayable field.",
                            ValueKind = ComponentPropertyValueKind.String,
                        },
                        new()
                        {
                            Key = "insurerDecisionNotes", Title = "Insurer decision notes",
                            ValueKind = ComponentPropertyValueKind.String,
                        },
                    ],
                    SupportedCompletionModes = [SupportSystemCompletionMode.Poll, SupportSystemCompletionMode.Webhook],
                    Outcomes =
                    [
                        new() { Key = ApprovedOutcome, DisplayName = "Approved" },
                        new() { Key = RejectedOutcome, DisplayName = "Rejected" },
                    ],
                },
            ],
        });
}

/// <summary>
/// Talks to the real, separately-running SafetyNet Underwriting app over HTTP — the host-side
/// half of the registration in <see cref="SafetyNetUnderwriting"/>. Reads file bytes itself via
/// <see cref="IServiceRequestFileStorage"/> when a capability input resolves to a
/// <see cref="ServiceRequestFileReference"/>, exactly the way any other host code reads an
/// uploaded file — the engine that invoked this client never touched the bytes.
/// </summary>
public sealed class SafetyNetUnderwritingClient(
    IHttpClientFactory httpClientFactory,
    IServiceRequestFileStorage fileStorage,
    string callbackBaseUrl) : ISupportSystemClient
{
    public string SupportSystemKey => SafetyNetUnderwriting.SupportSystemKey;

    public async Task<SupportSystemInvocationReceipt> InvokeAsync(
        string capabilityKey,
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();

        if (inputs.GetValueOrDefault("ApplicantName")?.RawValue is string applicantName)
        {
            form.Add(new StringContent(applicantName), "applicantName");
        }

        if (inputs.GetValueOrDefault("EventName")?.RawValue is string eventName)
        {
            form.Add(new StringContent(eventName), "eventName");
        }

        if (inputs.GetValueOrDefault("Notes")?.RawValue is string notes)
        {
            form.Add(new StringContent(notes), "notes");
        }

        if (context.WebhookExpected)
        {
            form.Add(new StringContent($"{callbackBaseUrl}/wayfinder/support-systems/callbacks/{context.InvocationId}"), "callbackUrl");
        }

        if (inputs.GetValueOrDefault("File")?.FileReference is { } fileReference)
        {
            await using var fileStream = await fileStorage.OpenReadAsync(fileReference.StorageKey, ct);
            if (fileStream is not null)
            {
                using var memory = new MemoryStream();
                await fileStream.CopyToAsync(memory, ct);
                var fileContent = new ByteArrayContent(memory.ToArray());
                if (!string.IsNullOrWhiteSpace(fileReference.ContentType))
                {
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileReference.ContentType);
                }

                form.Add(fileContent, "file", fileReference.OriginalFileName);
            }
        }

        var client = httpClientFactory.CreateClient(SafetyNetUnderwriting.HttpClientName);
        var response = await client.PostAsync("/submissions", form, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                   ?? throw new InvalidOperationException("SafetyNet Underwriting returned an empty submission response.");

        return new SupportSystemInvocationReceipt
        {
            ExternalReference = body["submissionId"]?.GetValue<string>()
                                 ?? throw new InvalidOperationException("SafetyNet Underwriting response had no submissionId.")
        };
    }

    public async Task<SupportSystemOutcome?> CheckStatusAsync(
        string capabilityKey,
        SupportSystemInvocationReceipt receipt,
        CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(SafetyNetUnderwriting.HttpClientName);
        var response = await client.GetAsync($"/submissions/{receipt.ExternalReference}", ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var status = body?["status"]?.GetValue<string>();

        if (status is not (SafetyNetUnderwriting.ApprovedOutcome or SafetyNetUnderwriting.RejectedOutcome))
        {
            return null;
        }

        var decisionNotes = body?["decisionNotes"]?.GetValue<string>();
        return new SupportSystemOutcome
        {
            OutcomeKey = status,
            ResultPayload = new JsonObject
            {
                ["insurerDecision"] = status,
                ["insurerDecisionNotes"] = decisionNotes ?? ""
            }
        };
    }
}
