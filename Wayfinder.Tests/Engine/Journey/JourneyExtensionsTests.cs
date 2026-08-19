using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfinder.Engine.Journey;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Journey;

/// <summary>
/// <see cref="JourneyExtensions.AddJourney"/>'s <c>ValidateOnStart()</c> guard — mirrors
/// Wayfinder.Tests/Engine/Worklist/WorklistExtensionsTests.cs's own coverage of the identical
/// pattern.
/// </summary>
public class JourneyExtensionsTests
{
    [Fact]
    public void Missing_required_option_fails_validation_with_a_clear_message()
    {
        var services = new ServiceCollection();
        services.AddJourney(options =>
        {
            options.ResolveAccessProfile = _ => new ActorProfile();
            options.RenderPage = (title, body, _) => $"{title}:{body}";
            // ResolveTenantId deliberately left unset.
        });
        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<JourneyOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{nameof(JourneyOptions.ResolveTenantId)}*");
    }

    [Fact]
    public void All_required_options_set_passes_validation()
    {
        var services = new ServiceCollection();
        services.AddJourney(options =>
        {
            options.ResolveTenantId = _ => "tenant-1";
            options.ResolveAccessProfile = _ => new ActorProfile();
            options.RenderPage = (title, body, _) => $"{title}:{body}";
        });
        var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IOptions<JourneyOptions>>().Value;

        resolved.ResolveTenantId.Should().NotBeNull();
    }
}
