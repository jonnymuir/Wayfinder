// Wayfinder's reference-host orchestrator. Deliberately minimal: no database, no external
// identity provider — matching Wayfinder.ReferenceApp's own in-memory, boot-fast architecture.
// See Wayfinder.ReferenceApp/Services/DemoUsers.cs for why auth is a hand-rolled in-memory login
// rather than something Aspire needs to orchestrate a Keycloak/Entra dependency for. The one
// exception to "just the reference app" is SafetyNetUnderwriting — a genuinely separate second
// service, standing in for NN/g's third "support systems" service-blueprint lane (see
// docs/guides/support-systems.md) — proving a real caseworker journey can call out to, and wait
// on, an actual external system rather than something baked into this same process.
var builder = DistributedApplication.CreateBuilder(args);

var safetyNetUnderwriting = builder.AddProject<Projects.SafetyNetUnderwriting>(
        "safetynet-underwriting", launchProfileName: "https")
    .WithUrls(ctx =>
    {
        var httpsBaseUrl = ctx.Urls
            .Where(u => u.Url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
            .Select(u => new Uri(u.Url!))
            .Select(uri => $"{uri.Scheme}://{uri.Authority}")
            .FirstOrDefault();

        if (httpsBaseUrl != null)
        {
            ctx.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{httpsBaseUrl}/queue",
                DisplayText = "Staff queue",
                DisplayOrder = 1
            });
        }
    });

builder.AddProject<Projects.Wayfinder_ReferenceApp>("referenceapp", launchProfileName: "https")
    .WithReference(safetyNetUnderwriting)
    .WaitFor(safetyNetUnderwriting)
    .WithUrls(ctx =>
    {
        // Aspire already lists the resource's own endpoint URLs; these are extra named
        // shortcuts straight into the reference app's own screens/tools, shown as additional
        // links on the resource's row in the dashboard.
        var httpsBaseUrl = ctx.Urls
            .Where(u => u.Url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
            .Select(u => new Uri(u.Url!))
            .Select(uri => $"{uri.Scheme}://{uri.Authority}")
            .FirstOrDefault();

        if (httpsBaseUrl != null)
        {
            ctx.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{httpsBaseUrl}/",
                DisplayText = "Wayfinder Reference App",
                DisplayOrder = 1
            });
        }

        // Plain HTTP, for pointing local MCP clients (`claude mcp add --transport http`) at —
        // most HTTP MCP clients reject the local ASP.NET Core dev cert on HTTPS. See
        // Wayfinder.Engine.Mcp/README.md.
        var httpBaseUrl = ctx.Urls
            .Where(u => u.Url?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true)
            .Select(u => new Uri(u.Url!))
            .Select(uri => $"{uri.Scheme}://{uri.Authority}")
            .FirstOrDefault();

        if (httpBaseUrl != null)
        {
            ctx.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{httpBaseUrl}/wayfinder/service-blueprint-authoring/mcp",
                DisplayText = "Service Blueprint Authoring MCP (HTTP)",
                DisplayOrder = 2
            });
        }
    });

builder.Build().Run();
