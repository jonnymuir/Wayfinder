using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Wayfinder.Rendering.GovUk;

/// <summary>
/// Serves this package's vendored govuk-frontend static assets — see this package's own
/// <c>.csproj</c> for why <c>Sdk.Web</c> already gives it the framework reference this needs for
/// free, despite having zero Razor/MVC C# code otherwise.
/// </summary>
public static class GovUkFrontendAssetsExtensions
{
    /// <summary>
    /// govuk-frontend.min.css's own @font-face rules request fonts at a hard-coded absolute
    /// "/assets/fonts/..." — baked into the pre-built CSS regardless of where the CSS file itself
    /// is served from, so the vendored font files (shipped alongside the CSS under this package's
    /// own static-web-asset prefix) need re-rooting onto that exact path. A host already using
    /// "/assets/..." for something else should pick a different <paramref name="requestPath"/> —
    /// static-files middleware calls just fall through to the next on a miss, so there's no
    /// collision either way.
    /// </summary>
    public static IApplicationBuilder UseGovUkFrontendAssets(this IApplicationBuilder app, string requestPath = "/assets")
    {
        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new SubPathFileProvider(env.WebRootFileProvider, "_content/Wayfinder.Rendering.GovUk/govuk-frontend/assets"),
            RequestPath = requestPath
        });
    }

    /// <summary>Re-roots an <see cref="IFileProvider"/> at a fixed subpath, without hardcoding any machine-specific NuGet/build-output path.</summary>
    private sealed class SubPathFileProvider(IFileProvider inner, string subpath) : IFileProvider
    {
        private string Rebase(string path) => $"{subpath}/{path.TrimStart('/')}";

        public IFileInfo GetFileInfo(string subpath_) => inner.GetFileInfo(Rebase(subpath_));

        public IDirectoryContents GetDirectoryContents(string subpath_) => inner.GetDirectoryContents(Rebase(subpath_));

        public IChangeToken Watch(string filter) => inner.Watch(Rebase(filter));
    }
}
