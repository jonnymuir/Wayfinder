using FluentAssertions;
using Wayfinder.Engine.Worklist;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Tests.Engine.Worklist;

/// <summary>
/// <see cref="WorklistRenderer"/> is the one GOV.UK-correct worklist renderer every host calls —
/// <see cref="WorklistExtensions.MapWorklist"/> for a plain ASP.NET Core host, Wayfinder.Umbraco's
/// own worklist Block Grid component for an Umbraco one. These tests are the regression guard for
/// the exact bug class found live in Wayfinder.Umbraco's own hand-rolled implementation before it
/// adopted this renderer: govuk-frontend never sets typography via a global html/body reset, only
/// via its own classes, so any element with none of them silently falls back to the browser's
/// serif default.
/// </summary>
public class WorklistRendererTests
{
    private static readonly QueueWorkItem ActionableItem = new()
    {
        InstanceId = "11111111-1111-1111-1111-111111111111",
        BlueprintKey = "licence",
        BlueprintDisplayName = "Juggling licence",
        StateDisplayName = "Review application",
        CursorId = "cursor-1",
        Status = QueueWorkItemStatus.Actionable,
        PickupState = QueueWorkItemPickupState.NotPickedUp,
    };

    [Fact]
    public void Every_status_checkbox_carries_its_govuk_classes_and_reflects_the_selection()
    {
        var envelope = new QueueWorkListEnvelope { Items = [], TotalMatchingCount = 0 };
        var selected = new[] { QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Done };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work", envelope, selected, QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20);

        html.Should().Contain("""<input class="govuk-checkboxes__input" id="status-Actionable" name="status" type="checkbox" value="Actionable" checked>""");
        html.Should().Contain("""<input class="govuk-checkboxes__input" id="status-Waiting" name="status" type="checkbox" value="Waiting" >""");
        html.Should().Contain("""<input class="govuk-checkboxes__input" id="status-Done" name="status" type="checkbox" value="Done" checked>""");
        html.Should().Contain("""<label class="govuk-label govuk-checkboxes__label" for="status-Actionable">""");
    }

    [Fact]
    public void Pagination_and_its_page_links_carry_govuk_body_and_govuk_link()
    {
        var envelope = new QueueWorkListEnvelope { Items = [ActionableItem], TotalMatchingCount = 45 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work", envelope,
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 1, size: 20);

        // The exact regression this guards: a bare <span>/<a> with no govuk-* class falls back to
        // the browser's serif default — see PR fixing Wayfinder.Umbraco's own worklist block.
        html.Should().Contain("""<span class="govuk-body">Page 2 — showing 1 of 45</span>""");
        html.Should().MatchRegex("""<a class="govuk-link" href="[^"]*">Previous</a>""");
        html.Should().MatchRegex("""<a class="govuk-link" href="[^"]*">Next</a>""");
    }

    [Fact]
    public void No_pagination_is_rendered_when_nothing_matches()
    {
        var envelope = new QueueWorkListEnvelope { Items = [], TotalMatchingCount = 0 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work", envelope,
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20);

        html.Should().NotContain("<nav");
        html.Should().Contain("No applications match the current filters");
    }

    [Fact]
    public void Status_tags_use_the_real_govuk_tag_classes()
    {
        var waiting = ActionableItem with { Status = QueueWorkItemStatus.Waiting, PickupState = null };
        var envelope = new QueueWorkListEnvelope { Items = [waiting], TotalMatchingCount = 1 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work", envelope,
            [QueueWorkItemStatus.Waiting], QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20);

        html.Should().Contain("""<strong class="govuk-tag govuk-tag--yellow">Waiting</strong>""");
    }

    [Fact]
    public void Pickup_control_carries_govuk_button_and_the_antiforgery_token_when_supplied()
    {
        var html = WorklistRenderer.RenderPickupPutbackControl(
            ActionableItem, "/worklist", returnTo: "/worklist?page=2", antiforgeryToken: "token-value");

        html.Should().Contain("""<button class="govuk-button govuk-button--secondary govuk-!-margin-0" data-module="govuk-button">Pick up</button>""");
        html.Should().Contain("""<input type="hidden" name="__RequestVerificationToken" value="token-value">""");
    }

    [Fact]
    public void Pickup_control_omits_the_antiforgery_field_when_no_token_is_supplied()
    {
        var html = WorklistRenderer.RenderPickupPutbackControl(
            ActionableItem, "/worklist", returnTo: "/worklist", antiforgeryToken: null);

        html.Should().NotContain("__RequestVerificationToken");
    }

    [Fact]
    public void Putback_control_shows_the_with_you_tag_and_a_put_back_button()
    {
        var pickedUp = ActionableItem with { PickupState = QueueWorkItemPickupState.PickedUpByMe };

        var html = WorklistRenderer.RenderPickupPutbackControl(pickedUp, "/worklist", "/worklist", null);

        html.Should().Contain("""<strong class="govuk-tag">With you</strong>""");
        html.Should().Contain("Put back");
    }

    [Fact]
    public void ParseWorklistQuery_defaults_to_actionable_waiting_unassigned_on_a_bare_initial_load()
    {
        var (statuses, selected, sort, pageIndex, size) =
            WorklistRenderer.ParseWorklistQuery(status: null, sort: null, page: null, pageSize: null, statusFilterApplied: null, defaultPageSize: 20);

        statuses.Should().BeNull("a bare load carries no explicit filter — the engine applies its own default");
        selected.Should().BeEquivalentTo([QueueWorkItemStatus.Actionable, QueueWorkItemStatus.Waiting, QueueWorkItemStatus.Unassigned]);
        sort.Should().Be(QueueWorkListSort.Default);
        pageIndex.Should().Be(0);
        size.Should().Be(20);
    }

    [Fact]
    public void ParseWorklistQuery_takes_an_explicitly_empty_selection_literally_when_the_filter_was_applied()
    {
        var (statuses, selected, _, _, _) =
            WorklistRenderer.ParseWorklistQuery(status: [], sort: null, page: null, pageSize: null, statusFilterApplied: "1", defaultPageSize: 20);

        statuses.Should().BeEmpty("every status box was unchecked and the form was genuinely submitted");
        selected.Should().BeEmpty();
    }

    [Fact]
    public void ParseWorklistQuery_clamps_page_size_to_a_sane_range()
    {
        var (_, _, _, _, size) = WorklistRenderer.ParseWorklistQuery(null, null, null, pageSize: 500, statusFilterApplied: null, defaultPageSize: 20);
        size.Should().Be(100);
    }
}
