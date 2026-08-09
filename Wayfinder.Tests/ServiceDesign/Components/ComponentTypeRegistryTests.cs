using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Extensions;

namespace Wayfinder.Tests.ServiceDesign.Components;

public class ComponentTypeRegistryTests
{
    /// <summary>
    /// The safety net this whole exercise exists to add: if someone adds a
    /// <c>[JsonDerivedType]</c> entry to <see cref="Component"/> without a matching
    /// <see cref="BuiltInComponentDescriptors"/> entry (or vice versa), this fails immediately
    /// instead of drifting silently — the exact failure mode that let
    /// <c>SummaryListComponent.Children</c> and the old <c>TelComponent</c> go unnoticed.
    /// </summary>
    [Fact]
    public void BuiltInDescriptors_ExactlyMatchComponentJsonDerivedTypeAttributes()
    {
        var attributeDiscriminators = typeof(Component)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => (string)a.TypeDiscriminator!)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        var descriptorDiscriminators = BuiltInComponentDescriptors.All
            .Select(d => d.Discriminator)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        descriptorDiscriminators.Should().Equal(attributeDiscriminators);
    }

    [Fact]
    public void AllBuiltInDescriptors_ClrTypeMatchesItsOwnDiscriminatorAttribute()
    {
        foreach (var descriptor in BuiltInComponentDescriptors.All)
        {
            var attribute = typeof(Component)
                .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
                .Cast<JsonDerivedTypeAttribute>()
                .SingleOrDefault(a => (string)a.TypeDiscriminator! == descriptor.Discriminator);

            attribute.Should().NotBeNull(because: $"'{descriptor.Discriminator}' should have a matching [JsonDerivedType] entry");
            attribute!.DerivedType.Should().Be(descriptor.ClrType);
        }
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
            var json = $$"""{ "type": "{{descriptor.Discriminator}}" }""";
            var component = JsonSerializer.Deserialize<Component>(json, ServiceBlueprintJson.ReadOptions);

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
    public void CustomComponentType_RegistersAndRoundTripsThroughJson()
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

            // Not ServiceBlueprintJson.ReadOptions/WriteOptions — those are static singletons
            // that may already have been used (and therefore cached their JsonTypeInfo<Component>)
            // by another test earlier in this same process. A fresh options instance, with its
            // own fresh resolver, is what actually proves registering a new type after the
            // registry already had built-ins in it works.
            var options = new JsonSerializerOptions
            {
                TypeInfoResolver = ComponentTypeRegistry.CreateJsonTypeInfoResolver(),
            };

            var original = new FixtureWidgetComponent { Message = "hello from a toolkit extension" };
            var json = JsonSerializer.Serialize<Component>(original, options);

            json.Should().Contain("\"fixture-widget\"");

            var roundTripped = JsonSerializer.Deserialize<Component>(json, options);

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
