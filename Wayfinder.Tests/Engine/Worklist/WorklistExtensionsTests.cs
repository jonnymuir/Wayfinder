using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfinder.Engine.Worklist;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Worklist;

/// <summary>
/// <see cref="WorklistExtensions.AddWorklist"/>'s <c>ValidateOnStart()</c> guard — a host that
/// forgets one of the three options with no sane default (<see cref="WorklistOptions.ResolveTenantId"/>,
/// <see cref="WorklistOptions.ResolveAccessProfile"/>, <see cref="WorklistOptions.RenderPage"/>)
/// gets a clear failure naming the missing option, not a null-reference deep inside a route handler.
/// </summary>
public class WorklistExtensionsTests
{
    [Fact]
    public void Missing_required_option_fails_validation_with_a_clear_message()
    {
        var services = new ServiceCollection();
        services.AddWorklist(options =>
        {
            options.ResolveAccessProfile = _ => new ActorProfile();
            options.RenderPage = (title, body, _) => $"{title}:{body}";
            // ResolveTenantId deliberately left unset.
        });
        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<WorklistOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{nameof(WorklistOptions.ResolveTenantId)}*");
    }

    [Fact]
    public void All_required_options_set_passes_validation()
    {
        var services = new ServiceCollection();
        services.AddWorklist(options =>
        {
            options.ResolveTenantId = _ => "tenant-1";
            options.ResolveAccessProfile = _ => new ActorProfile();
            options.RenderPage = (title, body, _) => $"{title}:{body}";
        });
        var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IOptions<WorklistOptions>>().Value;

        resolved.ResolveTenantId.Should().NotBeNull();
    }
}
