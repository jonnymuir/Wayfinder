# Demo recordings

Narrated, single-take screen recordings of Wayfinder features working end to end against the
real stack. These are **deliverables, not tests** — but every claim a caption makes is backed by
a real assertion in the spec that produced it, so a recording can never narrate something the app
didn't actually do.

| Recording | What it shows | Produced by |
|---|---|---|
| `support-systems-end-to-end.webm` | NN/g's third service-blueprint lane: a citizen applies and uploads a risk assessment, a caseworker sends it to SafetyNet Underwriting, that genuinely separate app approves it, a real webhook resolves the caseworker's wait, and the licence is granted. | `Wayfinder.ReferenceApp.Tests/tests/demo/support-systems-demo.spec.ts` |

## Regenerating

```
cd Wayfinder.ReferenceApp.Tests && npm run demo:record
```

The spec owns the whole `Wayfinder.AppHost` lifecycle (see `tests/support/live-app-host.ts`), so
nothing may already be listening on the reference app's or SafetyNet Underwriting's ports when it
starts. It runs headed on purpose — headless Chromium throttles rendering on a backgrounded tab
and can silently freeze a recording on one frame while the automation underneath keeps working.

`npm run demo:record` prints a **narration timeline** — every caption with its video-relative
timestamp — which is the source data for adding a voiced-over track later without re-timing
anything by hand.

## Why these exist

A recording is the cheapest way to find the things assertions don't think to ask about. This
suite's first take surfaced three real defects that every existing test had passed straight over:

- an application sent to a support system **vanished from the caseworker's own queue**, reachable
  only by a remembered URL;
- the **webhook had never actually worked** (a missing Aspire service-discovery reference back to
  the reference app), with the poll fallback quietly covering for it — including inside a test
  whose own comment claimed to be proving push-delivery;
- a boolean shown read-only on a later stage was **silently reset to false** when that stage was
  submitted, so a caseworker reviewing a fire act read "Fire, knives or other dangerous props: No".

All three are fixed, and each now has a real regression test. See
[docs/guides/support-systems.md](../guides/support-systems.md).

## Conventions

Ported from Umbraco.Prism's `narrated-single-take-demo-recording` skill — one shared `Page` across
every act (Playwright records one video per page, so this is what makes it a single continuous
take with no stitching), reading-paced caption holds computed from word count, and a visible
cursor with character-by-character typing so a viewer can follow what's happening.
