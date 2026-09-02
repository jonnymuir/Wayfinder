# Demo recordings

Narrated, single-take screen recordings of Wayfinder features working end to end against the
real stack. These are **deliverables, not tests**: but every claim a caption makes is backed by
a real assertion in the spec that produced it, so a recording can never narrate something the app
didn't actually do.

| Recording | What it shows | Produced by |
|---|---|---|
| `wayfinder-overview.webm` | What a service blueprint is (grounded in Nielsen Norman Group's definition, with a QR code to the source article) and every major thing declaring one buys you: real GOV.UK screens, a declarative cross-field validation rule enforced live, the GDS "Change" pattern, conditional routing, and a genuinely separate third-party system (NN/g's "support processes" lane) resolving a caseworker's wait by webhook, plus the visual editor authoring the same rules. | `Wayfinder.ReferenceApp.Tests/tests/demo/wayfinder-overview-demo.spec.ts` |
| `bulk-data-review.webm` | An NJF operations user submitting a monthly contributions CSV to SafetyNet Underwriting, landing directly on the wait screen, reviewing only the rows that need attention (never the whole file), an autosaved correction that can't be silently lost, a genuine resubmit loop back to the same external system, an explicit confirmation stage before finishing with a non-blocking warning still on record, and (closing out) the editor's own canvas and properties panel showing the whole thing declared, not hand-coded. Written companion: [bulk-data-review-walkthrough.md](./bulk-data-review-walkthrough.md). | `Wayfinder.ReferenceApp.Tests/tests/demo/bulk-data-review-demo.spec.ts` |

## Regenerating

```
cd Wayfinder.ReferenceApp.Tests && npm run demo:record:overview
cd Wayfinder.ReferenceApp.Tests && npm run demo:record:bulk-data-review
```

The spec owns the whole `Wayfinder.AppHost` lifecycle (see `tests/support/live-app-host.ts`), so
nothing may already be listening on the reference app's or SafetyNet Underwriting's ports when it
starts. It runs headless, so recording doesn't take over the operator's screen, headless Chromium
is known to throttle rendering on a backgrounded tab and can silently freeze a recording on one
frame while the automation underneath keeps working, so every take is verified afterwards
(extract frames at several timestamps, confirm they're pixel-distinct) rather than assumed safe.

`npm run demo:record` prints a **narration timeline**: every caption with its video-relative
timestamp, which is the source data for adding a voiced-over track later without re-timing
anything by hand.

### Walkthrough screenshots

[`bulk-data-review-walkthrough.md`](./bulk-data-review-walkthrough.md) embeds still screenshots
under `screenshots/bulk-data-review/`. They are **not** frames cut from the video (those carry the
narration bar) but clean `page.screenshot()` captures, each taken as the side effect of a real
assertion on the same screen, by `Wayfinder.ReferenceApp.Tests/tests/bulk-data-review-screenshots-live.spec.ts`.
Regenerate them after a real UI change with:

```
cd Wayfinder.ReferenceApp.Tests && npm run docs:screenshots:bulk-data-review
```

That spec is skipped unless `CAPTURE_DOC_SCREENSHOTS` is set (the npm script sets it), so the
normal live-suite run never boots a second AppHost or rewrites the committed images. Act 5's
editor screens also need `Wayfinder.Editor.Client`'s compiled bundle on disk
(`npm run build` in `Wayfinder.Editor.Client`).

## Why these exist

A recording is the cheapest way to find the things assertions don't think to ask about. This
suite's first take surfaced three real defects that every existing test had passed straight over:

- an application sent to a support system **vanished from the caseworker's own queue**, reachable
  only by a remembered URL;
- the **webhook had never actually worked** (a missing Aspire service-discovery reference back to
  the reference app), with the poll fallback quietly covering for it, including inside a test
  whose own comment claimed to be proving push-delivery;
- a boolean shown read-only on a later stage was **silently reset to false** when that stage was
  submitted, so a caseworker reviewing a fire act read "Fire, knives or other dangerous props: No".

All three are fixed, and each now has a real regression test. See
[docs/guides/support-systems.md](../guides/support-systems.md).

## Conventions

Ported from Umbraco.Prism's `narrated-single-take-demo-recording` skill, one shared `Page` across
every act (Playwright records one video per page, so this is what makes it a single continuous
take with no stitching), reading-paced caption holds computed from word count, and a visible
cursor with character-by-character typing so a viewer can follow what's happening.
