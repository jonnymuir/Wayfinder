using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.ServiceDesign.Components;

/// <summary>
/// Locks docs/guides/reference-service-blueprint-contract.md's component catalog table to the
/// live <see cref="ComponentTypeRegistry"/> — otherwise this doc becomes a 9th (10th, 11th...)
/// hand-maintained enumeration of the same catalog, exactly the drift problem this whole registry
/// exists to eliminate everywhere else.
/// </summary>
public class ComponentCatalogDocsTests
{
    private static readonly Regex CatalogRowDiscriminator = new(@"^\|\s*`([a-z][a-z0-9-]*)`", RegexOptions.Multiline);

    private static string ContractDocPath([CallerFilePath] string testFilePath = "") =>
        Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "..", "..",
            "docs", "guides", "reference-service-blueprint-contract.md");

    [Fact]
    public void CatalogTable_ExactlyMatchesComponentTypeRegistry()
    {
        var doc = File.ReadAllText(ContractDocPath());

        var startIndex = doc.IndexOf("<!-- component-catalog:start -->", StringComparison.Ordinal);
        var endIndex = doc.IndexOf("<!-- component-catalog:end -->", StringComparison.Ordinal);
        startIndex.Should().BeGreaterThan(-1, "the doc should have a component-catalog:start marker");
        endIndex.Should().BeGreaterThan(startIndex, "the doc should have a component-catalog:end marker after the start marker");

        var tableSection = doc[startIndex..endIndex];
        var docDiscriminators = CatalogRowDiscriminator.Matches(tableSection)
            .Select(m => m.Groups[1].Value)
            // The header row itself ("| `type` | Category | Description |") matches the same
            // pattern as a real data row — "type" is not, and can never be, a real discriminator.
            .Where(d => d != "type")
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        var registryDiscriminators = ComponentTypeRegistry.AllDiscriminators
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        docDiscriminators.Should().Equal(
            registryDiscriminators,
            because: "reference-service-blueprint-contract.md's component-catalog table must list " +
                "exactly the discriminators ComponentTypeRegistry actually has — add/remove a row " +
                "there whenever a built-in component type is added/removed");
    }
}
