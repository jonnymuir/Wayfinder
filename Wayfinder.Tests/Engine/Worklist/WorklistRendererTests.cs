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
    public void Pagination_is_the_real_govuk_pagination_block_component_with_a_separate_page_status_paragraph()
    {
        var envelope = new QueueWorkListEnvelope { Items = [ActionableItem], TotalMatchingCount = 45 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work", envelope,
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 1, size: 20);

        // The real GOV.UK Pagination component (block variant), not a hand-rolled prev/next
        // string — the exact regression this guards: the previous hand-rolled version squashed
        // "Previous"/the page-status text/"Next" onto one line with no separating markup at all.
        html.Should().Contain("""<nav class="govuk-pagination govuk-pagination--block govuk-!-margin-top-4" aria-label="Worklist pages">""");
        html.Should().Contain("""<div class="govuk-pagination__prev">""");
        html.Should().Contain("""<div class="govuk-pagination__next">""");
        html.Should().MatchRegex("""<a class="govuk-link govuk-pagination__link" href="[^"]*" rel="prev">""");
        html.Should().MatchRegex("""<a class="govuk-link govuk-pagination__link" href="[^"]*" rel="next">""");
        html.Should().Contain("""<p class="govuk-body">Page 2 — showing 1 of 45</p>""");
    }

    [Fact]
    public void Pagination_omits_the_prev_link_entirely_on_the_first_page_and_the_next_link_on_the_last()
    {
        // Matches govuk-frontend's own template.njk: a direction with no href is omitted whole,
        // not rendered as a disabled-looking link — see that macro's `{%- if previous and previous.href %}`.
        var firstPage = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work",
            new QueueWorkListEnvelope { Items = [ActionableItem], TotalMatchingCount = 45 },
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20);
        firstPage.Should().NotContain("govuk-pagination__prev");
        firstPage.Should().Contain("govuk-pagination__next");

        var lastPage = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work",
            new QueueWorkListEnvelope { Items = [ActionableItem], TotalMatchingCount = 45 },
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 2, size: 20);
        lastPage.Should().Contain("govuk-pagination__prev");
        lastPage.Should().NotContain("govuk-pagination__next");
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
    public void A_host_can_override_the_empty_state_message_with_its_own_vocabulary()
    {
        // Regression: adopting this renderer silently changed Wayfinder.Umbraco's displayed
        // "no results" copy from its own "service requests" wording to this package's default
        // "applications" wording — caught by a downstream consumer's Playwright suite, not here.
        var envelope = new QueueWorkListEnvelope { Items = [], TotalMatchingCount = 0 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", "My work", envelope,
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20,
            emptyStateMessage: "No service requests match the current filters");

        html.Should().Contain("No service requests match the current filters");
        html.Should().NotContain("No applications match the current filters");
    }

    [Fact]
    public void A_null_page_title_omits_the_heading_entirely()
    {
        // For a host embedding this in a page that already has its own heading — an Umbraco Block
        // Grid component, say — RenderWorklistBody must not impose a second, duplicate <h1>.
        var envelope = new QueueWorkListEnvelope { Items = [], TotalMatchingCount = 0 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/worklist", "/worklist", pageTitle: null, envelope,
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20);

        html.Should().NotContain("<h1");
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
    public void The_review_link_carries_listUrl_as_a_returnTo_query_parameter()
    {
        // A host whose item-detail route can't render inline (it 302s back into a CMS page
        // render instead — see Wayfinder.Umbraco's WayfinderWorklistSurfaceController) needs to
        // know where "back to worklist" goes; this is a real bug found live, not a hypothetical.
        var envelope = new QueueWorkListEnvelope { Items = [ActionableItem], TotalMatchingCount = 1 };

        var html = WorklistRenderer.RenderWorklistBody(
            "/caseworker-queue", "/worklist", "My work", envelope,
            [QueueWorkItemStatus.Actionable], QueueWorkListSort.Default,
            q: null, pageIndex: 0, size: 20);

        html.Should().Contain($"""href="/worklist/licence/{ActionableItem.InstanceId}?returnTo=%2Fcaseworker-queue">Review</a>""");
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
