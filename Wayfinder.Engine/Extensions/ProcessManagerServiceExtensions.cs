using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;

namespace Wayfinder.Engine.Extensions;

public static class ProcessManagerServiceExtensions
{
    public static IServiceCollection AddProcessManager(
        this IServiceCollection services,
        string blueprintSeedPath) =>
        services.AddProcessManager<ProcessManagerEngine>(blueprintSeedPath);

    public static IServiceCollection AddProcessManager<TEngine>(
        this IServiceCollection services,
        string blueprintSeedPath)
        where TEngine : class, IProcessManager
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintSeedPath);

        // ProcessManagerEngine takes a non-generic ILogger, which no host registers by default.
        services.TryAddSingleton<ILogger>(sp =>
            sp.GetService<ILoggerFactory>()?.CreateLogger("Wayfinder.Engine") ?? NullLogger.Instance);
        services.AddSingleton<IServiceBlueprintStore>(
            _ => new FilesystemServiceBlueprintStore(blueprintSeedPath));
        services.AddSingleton<TEngine>();
        services.AddSingleton<IProcessManager>(sp => sp.GetRequiredService<TEngine>());

        return services;
    }

    /// <summary>
    /// Registers <see cref="ServiceBlueprintAuthoringService"/> and <see cref="DesignNoteService"/>. The host
    /// must already have an <see cref="Abstractions.IServiceBlueprintSourceStore"/> registered — this does not
    /// provide one. Design notes default to <see cref="InMemoryDesignNoteStore"/>, which forgets them on
    /// restart; register an <see cref="IDesignNoteStore"/> of your own to keep them.
    /// </summary>
    public static IServiceCollection AddServiceBlueprintAuthoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ServiceBlueprintAuthoringService>();
        services.TryAddSingleton<IDesignNoteStore, InMemoryDesignNoteStore>();
        services.AddSingleton<DesignNoteService>();

        return services;
    }
}
