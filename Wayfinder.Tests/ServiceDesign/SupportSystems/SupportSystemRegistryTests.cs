using FluentAssertions;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;

namespace Wayfinder.Tests.ServiceDesign.SupportSystems;

public class SupportSystemRegistryTests
{
    private static SupportSystemDescriptor MakeFixtureDescriptor(
        string key = "safetynet-underwriting",
        string capabilityKey = "validate-risk-assessment") =>
        new()
        {
            Key = key,
            DisplayName = "SafetyNet Underwriting",
            Capabilities =
            [
                new SupportSystemCapabilityDescriptor
                {
                    Key = capabilityKey,
                    DisplayName = "Validate a risk assessment",
                    Inputs =
                    [
                        new ComponentPropertyDescriptor
                        {
                            Key = "file",
                            Title = "Risk assessment file",
                            ValueKind = ComponentPropertyValueKind.String,
                            Format = "field-ref",
                            Required = true,
                        },
                    ],
                    SupportedCompletionModes = [SupportSystemCompletionMode.Poll, SupportSystemCompletionMode.Webhook],
                    Outcomes =
                    [
                        new SupportSystemOutcomeDescriptor { Key = "approved", DisplayName = "Approved" },
                        new SupportSystemOutcomeDescriptor { Key = "rejected", DisplayName = "Rejected" },
                    ],
                },
            ],
        };

    [Fact]
    public void RegisteredSupportSystem_IsFindableByKey_AndAppearsInAll()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(MakeFixtureDescriptor());

            SupportSystemRegistry.Find("safetynet-underwriting").Should().NotBeNull();
            SupportSystemRegistry.All.Should().ContainSingle(d => d.Key == "safetynet-underwriting");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Find_UnregisteredKey_ReturnsNull()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Find("does-not-exist").Should().BeNull();
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void FindCapability_ResolvesTheExactCapabilityAnActionWouldReferenceByKeyPair()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(MakeFixtureDescriptor());

            var capability = SupportSystemRegistry.FindCapability("safetynet-underwriting", "validate-risk-assessment");

            capability.Should().NotBeNull();
            capability!.Outcomes.Select(o => o.Key).Should().BeEquivalentTo("approved", "rejected");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Theory]
    [InlineData("unknown-support-system", "validate-risk-assessment")]
    [InlineData("safetynet-underwriting", "unknown-capability")]
    public void FindCapability_UnknownSupportSystemOrCapability_ReturnsNull(string supportSystemKey, string capabilityKey)
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(MakeFixtureDescriptor());

            SupportSystemRegistry.FindCapability(supportSystemKey, capabilityKey).Should().BeNull();
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_DuplicateKey_Throws()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            SupportSystemRegistry.Register(MakeFixtureDescriptor());

            var act = () => SupportSystemRegistry.Register(MakeFixtureDescriptor());

            act.Should().Throw<InvalidOperationException>().WithMessage("*safetynet-underwriting*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_AfterFreeze_Throws()
    {
        SupportSystemRegistry.ResetForTests();
        try
        {
            _ = SupportSystemRegistry.All; // freezes the registry

            var act = () => SupportSystemRegistry.Register(MakeFixtureDescriptor());

            act.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_CapabilityWithNoCompletionMode_Throws()
    {
        // A capability whose outcome can never be delivered back (no poll, no webhook) would
        // leave any invocation permanently stuck — catch that at registration, not at runtime
        // when the first caseworker is left waiting forever.
        var descriptor = MakeFixtureDescriptor() with
        {
            Capabilities =
            [
                MakeFixtureDescriptor().Capabilities[0] with { SupportedCompletionModes = [] },
            ],
        };

        SupportSystemRegistry.ResetForTests();
        try
        {
            var act = () => SupportSystemRegistry.Register(descriptor);

            act.Should().Throw<ArgumentException>().WithMessage("*completion*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_CapabilityWithNoOutcomes_Throws()
    {
        var descriptor = MakeFixtureDescriptor() with
        {
            Capabilities =
            [
                MakeFixtureDescriptor().Capabilities[0] with { Outcomes = [] },
            ],
        };

        SupportSystemRegistry.ResetForTests();
        try
        {
            var act = () => SupportSystemRegistry.Register(descriptor);

            act.Should().Throw<ArgumentException>().WithMessage("*outcome*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Theory]
    [InlineData(false)] // Inputs
    [InlineData(true)] // Outputs
    public void Register_CapabilityInputOrOutputKeyStartingUppercase_Throws(bool isOutput)
    {
        // Found live: a PascalCase key here (the natural instinct — nameof-style PascalCase is
        // the convention everywhere else in this toolkit) silently becomes a different string
        // over the wire, since Inputs/Outputs reuse ComponentPropertyDescriptor.Key's
        // PropertyNameJsonConverter — designed for a real CLR property name, which a capability's
        // own input/output key never is. The editor's live-fetched catalog and a blueprint's own
        // params.inputs/params.outputs mapping keys then silently stop agreeing, and every
        // reference fails validation with no clue why (exactly what happened before this guard
        // existed). Catch it loudly at registration instead.
        var badProperty = new ComponentPropertyDescriptor
        {
            Key = "File", // deliberately uppercase-first
            Title = "File",
            ValueKind = ComponentPropertyValueKind.String,
        };
        var capability = MakeFixtureDescriptor().Capabilities[0] with
        {
            Inputs = isOutput ? MakeFixtureDescriptor().Capabilities[0].Inputs : [badProperty],
            Outputs = isOutput ? [badProperty] : [],
        };
        var descriptor = MakeFixtureDescriptor() with { Capabilities = [capability] };

        SupportSystemRegistry.ResetForTests();
        try
        {
            var act = () => SupportSystemRegistry.Register(descriptor);

            act.Should().Throw<ArgumentException>().WithMessage("*'File'*uppercase*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_DuplicateCapabilityKeysOnOneSupportSystem_Throws()
    {
        var capability = MakeFixtureDescriptor().Capabilities[0];
        var descriptor = MakeFixtureDescriptor() with { Capabilities = [capability, capability] };

        SupportSystemRegistry.ResetForTests();
        try
        {
            var act = () => SupportSystemRegistry.Register(descriptor);

            act.Should().Throw<ArgumentException>().WithMessage("*validate-risk-assessment*more than once*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Register_DuplicateOutcomeKeysOnOneCapability_Throws()
    {
        var descriptor = MakeFixtureDescriptor() with
        {
            Capabilities =
            [
                MakeFixtureDescriptor().Capabilities[0] with
                {
                    Outcomes =
                    [
                        new SupportSystemOutcomeDescriptor { Key = "approved", DisplayName = "Approved" },
                        new SupportSystemOutcomeDescriptor { Key = "approved", DisplayName = "Approved again" },
                    ],
                },
            ],
        };

        SupportSystemRegistry.ResetForTests();
        try
        {
            var act = () => SupportSystemRegistry.Register(descriptor);

            act.Should().Throw<ArgumentException>().WithMessage("*approved*more than once*");
        }
        finally
        {
            SupportSystemRegistry.ResetForTests();
        }
    }
}
