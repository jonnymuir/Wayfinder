using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Wayfinder.Editor;

/// <summary>
/// Serves this package's compiled service-blueprint-editor.html + JS/CSS bundle — see this
/// package's own <c>.csproj</c> for why <c>Sdk.Web</c> already gives it the framework reference
/// this needs for free, despite having zero Razor/MVC C# code otherwise.
/// </summary>
public static class WayfinderEditorAssetsExtensions
{
    /// <summary>
    /// Serves this package's compiled assets at web root instead of the default
    /// "_content/Wayfinder.Editor/dist/" static-web-asset prefix — the build emits root-relative
    /// asset references, so it must be served from "/". Strips caching on ".html" responses so a
    /// deployed editor page update isn't stuck behind a stale browser cache.
    /// </summary>
    public static IApplicationBuilder UseWayfinderEditorAssets(this IApplicationBuilder app)
    {
        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new SubPathFileProvider(env.WebRootFileProvider, "_content/Wayfinder.Editor/dist"),
            RequestPath = "",
            OnPrepareResponse = ctx =>
            {
                if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
                    ctx.Context.Response.Headers.Pragma = "no-cache";
                    ctx.Context.Response.Headers.Expires = "0";
                }
            }
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
