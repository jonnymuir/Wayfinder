using FluentAssertions;
using Wayfinder.Engine.Abstractions;
using Wayfinder.Engine.Models;
using Wayfinder.Engine.Services;
using Wayfinder.Engine.Stores;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;

namespace Wayfinder.Tests.Engine.Services;

public class DesignNoteServiceTests
{
    private static readonly DesignNoteAuthor Designer = new("Sam Designer", DesignNoteAuthorKind.Person);
    private static readonly DesignNoteAuthor Partner = new("Design partner", DesignNoteAuthorKind.DesignPartner);

    internal sealed class BlueprintStore : IServiceBlueprintSourceStore
    {
        private readonly Dictionary<string, ServiceBlueprint> _blueprints = [];

        public void Put(ServiceBlueprint blueprint) => _blueprints[blueprint.DefinitionKey] = blueprint;

        public Task<IReadOnlyList<ServiceBlueprintSourceSummary>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceBlueprintSourceSummary>>(
                _blueprints.Values.Select(b => new ServiceBlueprintSourceSummary(b.DefinitionKey, b.DisplayName)).ToArray());

        public Task<ServiceBlueprint?> LoadAsync(string definitionKey, CancellationToken ct = default) =>
            Task.FromResult(_blueprints.GetValueOrDefault(definitionKey));

        public Task<ServiceBlueprintSaveResult> SaveAsync(ServiceBlueprint blueprint, int expectedVersion, CancellationToken ct = default)
        {
            _blueprints[blueprint.DefinitionKey] = blueprint with { Version = expectedVersion + 1 };
            return Task.FromResult(new ServiceBlueprintSaveResult(true, expectedVersion + 1, "memory://"));
        }

        public Task<bool> DeleteAsync(string definitionKey, CancellationToken ct = default) =>
            Task.FromResult(_blueprints.Remove(definitionKey));
    }

    internal static ServiceBlueprint Licence(int version = 3) => new()
    {
        DefinitionKey = "licence",
        DisplayName = "Apply for a licence",
        Version = version,
        InitialStage = "details",
        Stages =
        [
            new StageDefinition
            {
                StageKey = "details",
                DisplayName = "Your details",
                QueueKey = "citizen",
                Components = [new TextInputComponent { FieldKey = "email", Label = "Email address" }],
                Routes = [new ServiceBlueprintRouteDefinition { Id = "to-check", Trigger = "continue", Target = "decide" }],
            },
            new StageDefinition { StageKey = "check", DisplayName = "Check your answers", QueueKey = "citizen" },
        ],
        Gateways =
        [
            new ServiceBlueprintGatewayDefinition
            {
                Key = "decide",
                DisplayName = "Decide",
                GatewayType = GatewayKind.Split,
                Routes = [new ServiceBlueprintRouteDefinition { Id = "to-answers", Trigger = "continue", Target = "check" }],
            },
        ],
    };

    private readonly BlueprintStore _blueprints = new();
    private readonly InMemoryDesignNoteStore _notes = new();
    private readonly DesignNoteService _service;

    public DesignNoteServiceTests()
    {
        _blueprints.Put(Licence());
        _service = new DesignNoteService(_blueprints, _notes);
    }

    private async Task<DesignNote> Add(DesignNoteAnchor anchor, string text = "This question is confusing")
    {
        var outcome = await _service.AddAsync("licence", anchor, text, Designer);
        outcome.Status.Should().Be(DesignNoteOutcomeStatus.Ok, outcome.Error);
        return outcome.Note!;
    }

    [Fact]
    public async Task ANoteOnAStage_IsListedWithTheStagesNameAndPath()
    {
        await Add(new(DesignNoteAnchorKind.Stage, "check"));

        var listing = (await _service.ListWithAnchorsAsync("licence"))!.Single();

        listing.Note.Text.Should().Be("This question is confusing");
        listing.Note.Author.Should().Be(Designer);
        listing.Note.Status.Should().Be(DesignNoteStatus.Open);
        listing.Anchor.Should().Be(new ResolvedDesignNoteAnchor("Stage \"Check your answers\"", "stages.check", false));
    }

    [Theory]
    [InlineData(DesignNoteAnchorKind.Gateway, "decide", null, "Gateway \"Decide\"", "gateways.decide")]
    [InlineData(DesignNoteAnchorKind.Route, "to-answers", null, "Route \"continue\" from \"Decide\"", "gateways.decide.routes[0]")]
    [InlineData(DesignNoteAnchorKind.Field, "details", "email", "Field \"Email address\" on \"Your details\"", "stages.details.components[0]")]
    [InlineData(DesignNoteAnchorKind.Blueprint, null, null, "Whole blueprint", "")]
    public async Task EveryKindOfAnchor_ResolvesToWhereItIsInTheBlueprint(
        DesignNoteAnchorKind kind, string? key, string? fieldKey, string label, string path)
    {
        await Add(new(kind, key, fieldKey));

        var listing = (await _service.ListWithAnchorsAsync("licence"))!.Single();

        listing.Anchor.Should().Be(new ResolvedDesignNoteAnchor(label, path, false));
    }

    [Theory]
    [InlineData(DesignNoteAnchorKind.Stage, "nope", null)]
    [InlineData(DesignNoteAnchorKind.Route, "nope", null)]
    [InlineData(DesignNoteAnchorKind.Field, "details", "nope")]
    [InlineData(DesignNoteAnchorKind.Blueprint, "details", null)]
    [InlineData(DesignNoteAnchorKind.Stage, "details", "email")]
    public async Task ANoteCannotBePinnedToSomethingThatIsNotThere(DesignNoteAnchorKind kind, string? key, string? fieldKey)
    {
        var outcome = await _service.AddAsync("licence", new(kind, key, fieldKey), "Hello", Designer);

        outcome.Status.Should().Be(DesignNoteOutcomeStatus.Invalid);
        (await _notes.ListAsync("licence")).Should().BeEmpty();
    }

    [Fact]
    public async Task ANoteOnABlueprintThatDoesNotExist_IsNotFound()
    {
        var outcome = await _service.AddAsync("missing", new(DesignNoteAnchorKind.Blueprint), "Hello", Designer);

        outcome.Status.Should().Be(DesignNoteOutcomeStatus.NotFound);
        (await _service.ListAsync("missing")).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyNote_IsRejected(string text)
    {
        var outcome = await _service.AddAsync("licence", new(DesignNoteAnchorKind.Blueprint), text, Designer);

        outcome.Status.Should().Be(DesignNoteOutcomeStatus.Invalid);
    }

    [Fact]
    public async Task ANoteLongerThanTheLimit_IsRejected()
    {
        var outcome = await _service.AddAsync(
            "licence", new(DesignNoteAnchorKind.Blueprint), new string('a', DesignNoteService.MaxTextLength + 1), Designer);

        outcome.Status.Should().Be(DesignNoteOutcomeStatus.Invalid);
    }

    [Fact]
    public async Task WhenItsStageIsDeleted_TheNoteStaysAndStandsAtBlueprintLevel()
    {
        await Add(new(DesignNoteAnchorKind.Stage, "check"));
        _blueprints.Put(Licence() with { Stages = [Licence().Stages[0]] });

        var listing = (await _service.ListWithAnchorsAsync("licence"))!.Single();

        listing.Anchor.Should().Be(new ResolvedDesignNoteAnchor("Whole blueprint (stage \"check\" was removed)", "", true));
    }

    [Fact]
    public async Task WhenItsFieldIsDeleted_TheNoteStandsOnTheStage()
    {
        await Add(new(DesignNoteAnchorKind.Field, "details", "email"));
        _blueprints.Put(Licence() with { Stages = [Licence().Stages[0] with { Components = [] }, Licence().Stages[1]] });

        var listing = (await _service.ListWithAnchorsAsync("licence"))!.Single();

        listing.Anchor.Should().Be(new ResolvedDesignNoteAnchor("Stage \"Your details\" (field \"email\" was removed)", "stages.details", true));
    }

    [Fact]
    public async Task ResolvingANote_RecordsWhatWasDoneAndInWhichVersion_WithoutChangingTheBlueprint()
    {
        var note = await Add(new(DesignNoteAnchorKind.Stage, "check"));

        var outcome = await _service.ResolveAsync("licence", note.Id, "Reworded the question.", Partner, version: 2);

        outcome.Note!.Status.Should().Be(DesignNoteStatus.Resolved);
        outcome.Note.Resolution!.Summary.Should().Be("Reworded the question.");
        outcome.Note.Resolution.Version.Should().Be(2);
        outcome.Note.Resolution.Author.Should().Be(Partner);
        (await _blueprints.LoadAsync("licence"))!.Version.Should().Be(3);
        (await _service.ListAsync("licence", DesignNoteStatus.Open))!.Should().BeEmpty();
        (await _service.ListAsync("licence", DesignNoteStatus.Resolved))!.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolvingWithoutAVersion_UsesTheBlueprintsCurrentVersion()
    {
        var note = await Add(new(DesignNoteAnchorKind.Blueprint));

        var outcome = await _service.ResolveAsync("licence", note.Id, "Done.", Partner);

        outcome.Note!.Resolution!.Version.Should().Be(3);
    }

    [Fact]
    public async Task ANoteCannotBeResolvedTwice()
    {
        var note = await Add(new(DesignNoteAnchorKind.Blueprint));
        await _service.ResolveAsync("licence", note.Id, "Done.", Partner);

        var again = await _service.ResolveAsync("licence", note.Id, "Done again.", Partner);

        again.Status.Should().Be(DesignNoteOutcomeStatus.Invalid);
    }

    [Fact]
    public async Task RepliesAreKeptInOrder_AndTheNoteStaysOpen()
    {
        var note = await Add(new(DesignNoteAnchorKind.Blueprint));

        await _service.ReplyAsync("licence", note.Id, "Refuse it, or send it to a caseworker?", Partner);
        var outcome = await _service.ReplyAsync("licence", note.Id, "Send it to a caseworker.", Designer);

        outcome.Note!.Replies.Select(r => (r.Author.Kind, r.Text)).Should().Equal(
            (DesignNoteAuthorKind.DesignPartner, "Refuse it, or send it to a caseworker?"),
            (DesignNoteAuthorKind.Person, "Send it to a caseworker."));
        outcome.Note.Status.Should().Be(DesignNoteStatus.Open);
    }

    [Fact]
    public async Task ReopeningANote_KeepsWhatWasTriedInTheThread_ThenTheReason()
    {
        var note = await Add(new(DesignNoteAnchorKind.Blueprint));
        await _service.ResolveAsync("licence", note.Id, "Reworded the question.", Partner, version: 2);

        var outcome = await _service.ReopenAsync("licence", note.Id, Designer, "Still confusing.");

        outcome.Note!.Status.Should().Be(DesignNoteStatus.Open);
        outcome.Note.Resolution.Should().BeNull();
        outcome.Note.Replies.Select(r => r.Text).Should().Equal("Resolved in version 2: Reworded the question.", "Still confusing.");
    }

    [Fact]
    public async Task AReplyToANoteOnAnotherBlueprint_IsNotFound()
    {
        _blueprints.Put(Licence() with { DefinitionKey = "permit" });
        var note = await Add(new(DesignNoteAnchorKind.Blueprint));

        var outcome = await _service.ReplyAsync("permit", note.Id, "Hello", Designer);

        outcome.Status.Should().Be(DesignNoteOutcomeStatus.NotFound);
    }

    [Fact]
    public async Task AStaleWrite_IsRefusedByTheStore()
    {
        var note = await Add(new(DesignNoteAnchorKind.Blueprint));
        await _service.ReplyAsync("licence", note.Id, "First", Designer);

        var stale = await _notes.UpdateAsync(note with { Text = "Overwritten" }, note.Revision);

        stale.Should().BeFalse();
        (await _notes.GetAsync("licence", note.Id))!.Text.Should().Be("This question is confusing");
    }

    [Fact]
    public async Task DeletingTheBlueprint_DeletesItsNotes()
    {
        await Add(new(DesignNoteAnchorKind.Blueprint));
        var authoring = new ServiceBlueprintAuthoringService(_blueprints, designNotes: _notes);

        await authoring.DeleteAsync("licence");
        _blueprints.Put(Licence());

        (await _service.ListAsync("licence"))!.Should().BeEmpty();
    }
}
