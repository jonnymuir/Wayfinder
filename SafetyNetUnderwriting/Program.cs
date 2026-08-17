using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

// SafetyNet Underwriting — a fictional insurer's own system, standing in for the "support
// system" a Wayfinder-hosted caseworker calls out to (see docs/guides/support-systems.md). This
// is deliberately a genuinely separate small web app, not a library inside the Wayfinder host:
// the whole point of the demo is proving a real external system, with its own staff worklist and
// its own decision, can drive a Wayfinder blueprint's caseworker experience via poll and/or
// webhook. It knows nothing about Wayfinder's domain model beyond the shape of the callback it
// optionally posts back to — see Wayfinder.ReferenceApp/Services/SupportSystems/
// SafetyNetUnderwritingClient.cs for the ISupportSystemClient that talks to this app.

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();
app.MapDefaultEndpoints();

var submissions = new ConcurrentDictionary<string, Submission>();

app.MapPost("/submissions", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart/form-data.");
    }

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];
    byte[]? fileBytes = null;
    if (file is not null)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        fileBytes = stream.ToArray();
    }

    var submission = new Submission
    {
        Id = Guid.NewGuid().ToString("N"),
        ApplicantName = form["applicantName"],
        EventName = form["eventName"],
        Notes = form["notes"],
        CallbackUrl = form["callbackUrl"],
        FileName = file?.FileName,
        ContentType = file?.ContentType,
        FileBytes = fileBytes,
        Status = "pending",
        SubmittedAt = DateTimeOffset.UtcNow
    };

    submissions[submission.Id] = submission;

    return Results.Accepted($"/submissions/{submission.Id}", new
    {
        submissionId = submission.Id,
        status = submission.Status
    });
});

app.MapGet("/submissions/{id}", (string id) =>
    submissions.TryGetValue(id, out var submission)
        ? Results.Ok(new { id = submission.Id, status = submission.Status, decisionNotes = submission.DecisionNotes })
        : Results.NotFound());

// An underwriter can't sensibly approve or reject a risk assessment they can't actually open —
// the file genuinely travelled here over HTTP (see SafetyNetUnderwritingClient), so serving it
// back is this app's own concern, nothing to do with Wayfinder. Inline rather than an attachment
// download, so a staff member reviewing a queue can just look at it.
app.MapGet("/submissions/{id}/file", (string id) =>
{
    if (!submissions.TryGetValue(id, out var submission) || submission.FileBytes is null)
    {
        return Results.NotFound();
    }

    return Results.File(
        submission.FileBytes,
        submission.ContentType ?? "application/octet-stream",
        submission.FileName,
        enableRangeProcessing: false);
});

app.MapGet("/queue", () => Results.Content(RenderQueue(submissions.Values), "text/html"));

app.MapPost("/queue/{id}/decide", async (
    string id, HttpRequest request, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("SafetyNetUnderwriting.Callback");
    if (!submissions.TryGetValue(id, out var submission))
    {
        return Results.NotFound();
    }

    var form = await request.ReadFormAsync();
    var decision = form["decision"];
    if (decision != "approve" && decision != "reject")
    {
        return Results.BadRequest("decision must be 'approve' or 'reject'.");
    }

    var decided = submission with
    {
        Status = decision == "approve" ? "approved" : "rejected",
        DecisionNotes = form["decisionNotes"]
    };
    submissions[id] = decided;

    if (!string.IsNullOrWhiteSpace(decided.CallbackUrl))
    {
        var payload = new JsonObject
        {
            ["outcomeKey"] = decided.Status,
            ["resultPayload"] = new JsonObject
            {
                ["insurerDecision"] = decided.Status,
                ["insurerDecisionNotes"] = decided.DecisionNotes ?? ""
            }
        };

        // Best-effort — a callback failing here doesn't undo the decision this staff member just
        // made; Wayfinder's own poll-check hook is the fallback path if this never arrives. But
        // "best-effort" must never mean "silent": an unresolvable callback URL swallowed here once
        // hid a genuinely broken webhook for as long as the poll fallback kept the journey
        // completing anyway. Log both a non-success status and a thrown failure, loudly enough to
        // spot in the Aspire dashboard.
        try
        {
            var client = httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync(decided.CallbackUrl, payload);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Callback to {CallbackUrl} for submission {SubmissionId} succeeded ({Status}).",
                    decided.CallbackUrl, id, (int)response.StatusCode);
            }
            else
            {
                logger.LogError(
                    "Callback to {CallbackUrl} for submission {SubmissionId} returned {Status}. " +
                    "Wayfinder's poll fallback will have to cover this.",
                    decided.CallbackUrl, id, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Callback to {CallbackUrl} for submission {SubmissionId} failed outright. " +
                "Wayfinder's poll fallback will have to cover this.",
                decided.CallbackUrl, id);
        }
    }

    // PRG: redirect back to the queue rather than rendering at this POST URL.
    return Results.Redirect("/queue");
});

// ── Contributions file validation (see docs/guides/bulk-data-review.md and
// Wayfinder.ReferenceApp/service-blueprints/njf-contributions.json) — a genuinely different
// interaction shape from the risk-assessment queue above: no staff member decides anything here,
// ContributionsValidation.cs applies deterministic rules automatically. Kept as its own
// submissions map/endpoints rather than reusing the one above — the two capabilities' request/
// response shapes (JSON status body vs a whole annotated file; human decision vs automatic rules)
// don't share enough to be worth forcing into one model.
var contributionsSubmissions = new ConcurrentDictionary<string, ContributionsSubmission>();

app.MapPost("/contributions/submissions", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart/form-data.");
    }

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];
    if (file is null)
    {
        return Results.BadRequest("Expected a 'file' part.");
    }

    using var stream = new MemoryStream();
    await file.CopyToAsync(stream);
    var resultCsv = ContributionsValidation.Validate(stream.ToArray());

    var id = Guid.NewGuid().ToString("N");
    contributionsSubmissions[id] = new ContributionsSubmission
    {
        Id = id,
        SubmittedAt = DateTimeOffset.UtcNow,
        // A short artificial delay so the demo genuinely shows a "please wait while we process
        // your file" screen instead of resolving on the very first poll — real batch processing
        // isn't instant either. Purely a demo touch, not a queueing mechanism: the actual
        // validation already ran above, this just holds back when it's revealed as done.
        ReadyAt = DateTimeOffset.UtcNow.AddSeconds(3),
        ResultCsvBytes = resultCsv,
    };

    return Results.Accepted($"/contributions/submissions/{id}", new { submissionId = id, status = "pending" });
});

app.MapGet("/contributions/submissions/{id}", (string id) =>
{
    if (!contributionsSubmissions.TryGetValue(id, out var submission))
    {
        return Results.NotFound();
    }

    var status = DateTimeOffset.UtcNow >= submission.ReadyAt ? "processed" : "pending";
    return Results.Ok(new { id = submission.Id, status });
});

app.MapGet("/contributions/submissions/{id}/file", (string id) =>
{
    if (!contributionsSubmissions.TryGetValue(id, out var submission) || DateTimeOffset.UtcNow < submission.ReadyAt)
    {
        return Results.NotFound();
    }

    return Results.File(submission.ResultCsvBytes, "text/csv", "contributions-response.csv", enableRangeProcessing: false);
});

// Development-only, same guard/reasoning as Wayfinder.ReferenceApp's own /api/test/reset —
// wipes every in-memory submission so a Playwright spec starts each test from a known-empty
// state instead of restarting the process.
app.MapDelete("/api/test/reset", (IHostEnvironment env) =>
{
    if (!env.IsDevelopment())
    {
        return Results.NotFound();
    }

    submissions.Clear();
    contributionsSubmissions.Clear();
    return Results.Ok(new { cleared = true });
});

app.Run();

const string QueueStyle = """
    body { font-family: -apple-system, sans-serif; margin: 2rem; background: #1a1a2e; color: #eaeaea; }
    h1 { color: #ff9f1c; }
    table { border-collapse: collapse; width: 100%; margin-top: 1rem; }
    th, td { border: 1px solid #444; padding: 0.5rem; text-align: left; vertical-align: top; }
    th { background: #16213e; }
    button { background: #ff9f1c; border: none; padding: 0.3rem 0.6rem; cursor: pointer; }
    input[type=text] { padding: 0.2rem; }
    a { color: #7dd3fc; }
    """;

static string RenderQueue(IEnumerable<Submission> all)
{
    var pending = all.Where(s => s.Status == "pending").OrderBy(s => s.SubmittedAt).ToList();
    var decided = all.Where(s => s.Status != "pending").OrderByDescending(s => s.SubmittedAt).Take(10).ToList();

    string Row(Submission s, bool showActions) => $"""
        <tr>
          <td>{System.Net.WebUtility.HtmlEncode(s.ApplicantName ?? "(unknown)")}</td>
          <td>{System.Net.WebUtility.HtmlEncode(s.EventName ?? "")}</td>
          <td>{System.Net.WebUtility.HtmlEncode(s.Notes ?? "")}</td>
          <td>{(s.FileName is null ? "&mdash;" : $"""<a href="/submissions/{s.Id}/file" target="_blank" rel="noopener">{System.Net.WebUtility.HtmlEncode(s.FileName)}</a>""")}</td>
          <td>{s.Status}</td>
          <td>
            {(showActions ? $"""
              <form method="post" action="/queue/{s.Id}/decide">
                <label>Decision notes <input type="text" name="decisionNotes" /></label>
                <button type="submit" name="decision" value="approve">Approve</button>
                <button type="submit" name="decision" value="reject">Reject</button>
              </form>
              """ : System.Net.WebUtility.HtmlEncode(s.DecisionNotes ?? ""))}
          </td>
        </tr>
        """;

    return $"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>SafetyNet Underwriting — Staff Queue</title>
          <style>{QueueStyle}</style>
        </head>
        <body>
          <h1>SafetyNet Underwriting</h1>
          <p>Not a GOV.UK service — this is the insurer's own system, entirely separate from Wayfinder.</p>
          <h2>Pending ({pending.Count})</h2>
          <table>
            <tr><th>Applicant</th><th>Event</th><th>Notes</th><th>File</th><th>Status</th><th>Decide</th></tr>
            {string.Join("", pending.Select(s => Row(s, showActions: true)))}
          </table>
          <h2>Recent decisions</h2>
          <table>
            <tr><th>Applicant</th><th>Event</th><th>Notes</th><th>File</th><th>Status</th><th>Decision notes</th></tr>
            {string.Join("", decided.Select(s => Row(s, showActions: false)))}
          </table>
        </body>
        </html>
        """;
}

sealed record Submission
{
    public required string Id { get; init; }
    public string? ApplicantName { get; init; }
    public string? EventName { get; init; }
    public string? Notes { get; init; }
    public string? CallbackUrl { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public byte[]? FileBytes { get; init; }
    public required string Status { get; init; }
    public string? DecisionNotes { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}

sealed record ContributionsSubmission
{
    public required string Id { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset ReadyAt { get; init; }
    public required byte[] ResultCsvBytes { get; init; }
}
