// Wayfinder's reference-host orchestrator. Completely transient by design: the only resource
// is Wayfinder.ReferenceApp itself — no database, no containers, no external identity
// provider — matching the reference app's own in-memory, boot-fast architecture. See
// Wayfinder.ReferenceApp/Services/DemoUsers.cs for why auth is a hand-rolled in-memory login
// rather than something Aspire needs to orchestrate a Keycloak/Entra dependency for.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Wayfinder_ReferenceApp>("referenceapp", launchProfileName: "https")
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
