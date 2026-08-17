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

    public const string ValidateContributionsFileCapability = "validate-contributions-file";
    public const string ProcessedOutcome = "processed";
    public const string ContributionsResponseFileOutputKey = "contributionsResponseFile";

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
                            Key = "file", Title = "Risk assessment file",
                            Description = "The file-upload field carrying the applicant's risk assessment.",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref", Required = true,
                        },
                        new()
                        {
                            Key = "applicantName", Title = "Applicant name",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref",
                        },
                        new()
                        {
                            Key = "eventName", Title = "Event name",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref",
                        },
                        new()
                        {
                            Key = "notes", Title = "Risk mitigation notes",
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
                new SupportSystemCapabilityDescriptor
                {
                    Key = ValidateContributionsFileCapability,
                    DisplayName = "Validate a contributions file",
                    Description = "Uploads a CSV of member contributions; SafetyNet Underwriting returns the " +
                                  "same file annotated with a matched member ID and per-row error/warning " +
                                  "status — see docs/guides/bulk-data-review.md.",
                    Inputs =
                    [
                        new()
                        {
                            Key = "file", Title = "Contributions file",
                            Description = "The file-upload field carrying the NJF's contributions CSV.",
                            ValueKind = ComponentPropertyValueKind.String, Format = "field-ref", Required = true,
                        },
                    ],
                    Outputs =
                    [
                        new()
                        {
                            Key = ContributionsResponseFileOutputKey, Title = "Annotated response file",
                            Description = "SafetyNet's own response — the same CSV with a matched member ID " +
                                          "and per-row error/warning columns appended.",
                            ValueKind = ComponentPropertyValueKind.String,
                        },
                    ],
                    // Automatic rules, not a human decision — no staff queue involved, so no webhook
                    // callback to register either; poll is the only completion mode that makes sense.
                    SupportedCompletionModes = [SupportSystemCompletionMode.Poll],
                    Outcomes = [new() { Key = ProcessedOutcome, DisplayName = "Processed" }],
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
    // CheckStatusAsync only ever gets a capabilityKey + receipt, no instanceId — but the
    // contributions capability needs one to save the response file via IServiceRequestFileStorage
    // (SaveAsync partitions by instance). Captured here at InvokeAsync time instead, the same
    // "no server-side session, just correlate by the one token we're given" shape
    // SupportSystemInvocationContext.InvocationId already uses elsewhere. This client is a
    // singleton shared across concurrent requests (registered once in Program.cs), so this must
    // be concurrency-safe — same reasoning SafetyNetUnderwriting/Program.cs's own submissions map
    // is a ConcurrentDictionary.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _instanceIdByExternalReference = new();

    public string SupportSystemKey => SafetyNetUnderwriting.SupportSystemKey;

    public Task<SupportSystemInvocationReceipt> InvokeAsync(
        string capabilityKey,
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct = default) =>
        capabilityKey == SafetyNetUnderwriting.ValidateContributionsFileCapability
            ? InvokeContributionsAsync(inputs, context, ct)
            : InvokeRiskAssessmentAsync(inputs, context, ct);

    public Task<SupportSystemOutcome?> CheckStatusAsync(
        string capabilityKey,
        SupportSystemInvocationReceipt receipt,
        CancellationToken ct = default) =>
        capabilityKey == SafetyNetUnderwriting.ValidateContributionsFileCapability
            ? CheckContributionsStatusAsync(receipt, ct)
            : CheckRiskAssessmentStatusAsync(receipt, ct);

    private async Task<SupportSystemInvocationReceipt> InvokeRiskAssessmentAsync(
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();

        if (inputs.GetValueOrDefault("applicantName")?.RawValue is string applicantName)
        {
            form.Add(new StringContent(applicantName), "applicantName");
        }

        if (inputs.GetValueOrDefault("eventName")?.RawValue is string eventName)
        {
            form.Add(new StringContent(eventName), "eventName");
        }

        if (inputs.GetValueOrDefault("notes")?.RawValue is string notes)
        {
            form.Add(new StringContent(notes), "notes");
        }

        if (context.WebhookExpected)
        {
            form.Add(new StringContent($"{callbackBaseUrl}/wayfinder/support-systems/callbacks/{context.InvocationId}"), "callbackUrl");
        }

        await AddFilePartAsync(form, inputs, ct);

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

    private async Task<SupportSystemOutcome?> CheckRiskAssessmentStatusAsync(SupportSystemInvocationReceipt receipt, CancellationToken ct)
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

    private async Task<SupportSystemInvocationReceipt> InvokeContributionsAsync(
        IReadOnlyDictionary<string, SupportSystemInputValue> inputs,
        SupportSystemInvocationContext context,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await AddFilePartAsync(form, inputs, ct);

        var client = httpClientFactory.CreateClient(SafetyNetUnderwriting.HttpClientName);
        var response = await client.PostAsync("/contributions/submissions", form, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                   ?? throw new InvalidOperationException("SafetyNet Underwriting returned an empty submission response.");
        var submissionId = body["submissionId"]?.GetValue<string>()
                            ?? throw new InvalidOperationException("SafetyNet Underwriting response had no submissionId.");

        _instanceIdByExternalReference[submissionId] = context.InstanceId;
        return new SupportSystemInvocationReceipt { ExternalReference = submissionId };
    }

    private async Task<SupportSystemOutcome?> CheckContributionsStatusAsync(SupportSystemInvocationReceipt receipt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(SafetyNetUnderwriting.HttpClientName);
        var statusResponse = await client.GetAsync($"/contributions/submissions/{receipt.ExternalReference}", ct);
        statusResponse.EnsureSuccessStatusCode();

        var statusBody = await statusResponse.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (statusBody?["status"]?.GetValue<string>() != "processed")
        {
            return null;
        }

        var fileResponse = await client.GetAsync($"/contributions/submissions/{receipt.ExternalReference}/file", ct);
        fileResponse.EnsureSuccessStatusCode();
        var csvBytes = await fileResponse.Content.ReadAsByteArrayAsync(ct);

        if (!_instanceIdByExternalReference.TryGetValue(receipt.ExternalReference, out var instanceId))
        {
            throw new InvalidOperationException(
                $"No instance id captured for submission '{receipt.ExternalReference}' — InvokeAsync must run before CheckStatusAsync.");
        }

        await using var contentStream = new MemoryStream(csvBytes);
        var storageKey = await fileStorage.SaveAsync(
            instanceId, SafetyNetUnderwriting.ContributionsResponseFileOutputKey, contentStream, "contributions-response.csv", ct);

        var fileReference = new ServiceRequestFileReference
        {
            StorageKey = storageKey,
            OriginalFileName = "contributions-response.csv",
            ContentType = "text/csv",
            SizeBytes = csvBytes.LongLength,
        };

        return new SupportSystemOutcome
        {
            OutcomeKey = SafetyNetUnderwriting.ProcessedOutcome,
            ResultPayload = new JsonObject
            {
                [SafetyNetUnderwriting.ContributionsResponseFileOutputKey] = System.Text.Json.JsonSerializer.SerializeToNode(fileReference)
            }
        };
    }

    private async Task AddFilePartAsync(
        MultipartFormDataContent form, IReadOnlyDictionary<string, SupportSystemInputValue> inputs, CancellationToken ct)
    {
        if (inputs.GetValueOrDefault("file")?.FileReference is not { } fileReference)
        {
            return;
        }

        await using var fileStream = await fileStorage.OpenReadAsync(fileReference.StorageKey, ct);
        if (fileStream is null)
        {
            return;
        }

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
