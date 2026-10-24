using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Extensions;

namespace Wayfinder.Tests.ServiceDesign.Components;

public class ComponentTypeRegistryTests
{
    /// <summary>
    /// The registry is the only list of component types, so a concrete <see cref="Component"/>
    /// without a built-in descriptor could never be read or written: fail here instead.
    /// </summary>
    [Fact]
    public void EveryConcreteComponentType_HasABuiltInDescriptor()
    {
        var componentTypes = typeof(Component).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(Component)) && !type.IsAbstract)
            .OrderBy(type => type.Name, StringComparer.Ordinal);

        BuiltInComponentDescriptors.All.Select(d => d.ClrType).OrderBy(type => type.Name, StringComparer.Ordinal)
            .Should().Equal(componentTypes);
    }

    [Theory]
    [InlineData(typeof(FieldsetComponent), ContainmentKind.ChildList)]
    [InlineData(typeof(SummaryListComponent), ContainmentKind.ChildList)]
    [InlineData(typeof(AccordionComponent), ContainmentKind.NamedSections)]
    [InlineData(typeof(RadiosComponent), ContainmentKind.KeyedChildren)]
    [InlineData(typeof(CheckboxesComponent), ContainmentKind.KeyedChildren)]
    [InlineData(typeof(TextInputComponent), ContainmentKind.None)]
    public void Containment_MatchesExpectedShape(Type clrType, ContainmentKind expectedKind)
    {
        var descriptor = ComponentTypeRegistry.All.Single(d => d.ClrType == clrType);
        descriptor.Containment.Kind.Should().Be(expectedKind);
    }

    [Fact]
    public void Flatten_DescendsIntoSummaryListChildren()
    {
        // The bug this whole redesign fixes as a side effect: the old hand-written switch in
        // ComponentExtensions.Flatten never had a case for SummaryListComponent.Children, despite
        // it being structurally identical to FieldsetComponent.Children.
        var summaryList = new SummaryListComponent
        {
            Children =
            [
                new TextInputComponent { FieldKey = "name", Label = "Name" },
                new EmailComponent { FieldKey = "email", Label = "Email" },
            ],
        };

        var flattened = new[] { summaryList }.Flatten().ToList();

        flattened.Should().HaveCount(3);
        flattened.OfType<TextInputComponent>().Should().ContainSingle(c => c.FieldKey == "name");
        flattened.OfType<EmailComponent>().Should().ContainSingle(c => c.FieldKey == "email");
    }

    [Fact]
    public void FlattenWithPaths_BuildsCorrectPathsAcrossAllThreeContainmentShapes()
    {
        var components = new Component[]
        {
            new FieldsetComponent
            {
                Children = [new TextInputComponent { FieldKey = "a", Label = "A" }],
            },
            new AccordionComponent
            {
                Sections =
                [
                    new AccordionSection { Heading = "Section 1", Children = [new BooleanComponent { FieldKey = "b", Label = "B" }] },
                ],
            },
            new RadiosComponent
            {
                FieldKey = "choice", Label = "Choice", Options = ["Yes", "No"],
                ConditionalChildren = new Dictionary<string, IReadOnlyList<Component>>
                {
                    ["Yes"] = [new TextInputComponent { FieldKey = "why", Label = "Why?" }],
                },
            },
        };

        var paths = components.FlattenWithPaths("stages.test.components").Select(e => e.Path).ToList();

        paths.Should().Contain("stages.test.components[0]");
        paths.Should().Contain("stages.test.components[0].children[0]");
        paths.Should().Contain("stages.test.components[1].sections[0].children[0]");
        paths.Should().Contain("stages.test.components[2].conditionalChildren.Yes[0]");
    }

    [Fact]
    public void RoundTrip_EveryBuiltInDiscriminator_DeserializesBackToTheSameClrType()
    {
        foreach (var descriptor in ComponentTypeRegistry.All)
        {
            var component = ServiceBlueprintYaml.Deserialize<Component>($"type: {descriptor.Discriminator}");

            component.Should().NotBeNull(because: $"'{descriptor.Discriminator}' should deserialize");
            component!.GetType().Should().Be(descriptor.ClrType, because: $"'{descriptor.Discriminator}' should map back to {descriptor.ClrType.Name}");
        }
    }

    // A test-only fixture type — never registered by Wayfinder itself, standing in for a
    // toolkit user's own custom component.
    private sealed record FixtureWidgetComponent : Component
    {
        public string? Message { get; init; }
    }

    [Fact]
    public void CustomComponentType_RegistersAndRoundTripsThroughYaml()
    {
        ComponentTypeRegistry.ResetForTests();
        try
        {
            ComponentTypeRegistry.Register<FixtureWidgetComponent>(new ComponentDescriptor
            {
                Discriminator = "fixture-widget",
                DisplayName = "Fixture widget",
                Category = ComponentCategory.Content,
                ClrType = typeof(FixtureWidgetComponent),
                Properties = [],
            });

            var original = new FixtureWidgetComponent { Message = "hello from a toolkit extension" };
            var yaml = ServiceBlueprintYaml.Serialize<Component>(original);

            yaml.Should().StartWith("type: fixture-widget\n");

            var roundTripped = ServiceBlueprintYaml.Deserialize<Component>(yaml);

            roundTripped.Should().BeOfType<FixtureWidgetComponent>();
            ((FixtureWidgetComponent)roundTripped!).Message.Should().Be("hello from a toolkit extension");
        }
        finally
        {
            // Don't leak the fixture type into the shared, process-wide registry for whatever
            // test runs next.
            ComponentTypeRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_AfterFreeze_Throws()
    {
        ComponentTypeRegistry.ResetForTests();
        try
        {
            _ = ComponentTypeRegistry.All; // freezes the registry
            var act = () => ComponentTypeRegistry.Register<FixtureWidgetComponent>(new ComponentDescriptor
            {
                Discriminator = "fixture-widget-late",
                DisplayName = "Too late",
                Category = ComponentCategory.Content,
                ClrType = typeof(FixtureWidgetComponent),
            });

            act.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
        }
        finally
        {
            ComponentTypeRegistry.ResetForTests();
        }
    }

    /// <summary>
    /// Lets a call site declaring a per-queue/per-host component-type allow-list (e.g.
    /// <see cref="Wayfinder.ReferenceApp.Services.ReferenceActors"/>'s own capability
    /// declarations) reference a real registered CLR type instead of a bare string literal —
    /// a typo or a stale entry after a rename breaks the build instead of silently drifting.
    /// </summary>
    [Fact]
    public void DiscriminatorFor_GenericOverload_ReturnsTheRegisteredDiscriminator_WithNoInstanceNeeded()
    {
        ComponentTypeRegistry.DiscriminatorFor<TextInputComponent>().Should().Be("text");
        ComponentTypeRegistry.DiscriminatorFor<EmailComponent>().Should().Be("email");
    }

    [Fact]
    public void DiscriminatorFor_GenericOverload_UnregisteredType_Throws()
    {
        var act = () => ComponentTypeRegistry.DiscriminatorFor<UnregisteredFixtureComponent>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*UnregisteredFixtureComponent*");
    }

    private sealed record UnregisteredFixtureComponent : Component;
}
