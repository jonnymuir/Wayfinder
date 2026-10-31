# Changelog

Notable changes to the Wayfinder packages. The four lockstep packages (`Wayfinder`,
`Wayfinder.Engine`, `Wayfinder.Engine.Api`, `Wayfinder.Engine.Mcp`) share the version in each
heading; a change to an independently versioned package names its own version. This project
follows [semantic versioning](https://semver.org/); before 1.0, a breaking change bumps the minor
version.

Releases up to and including v0.19.0 are listed on
[GitHub Releases](https://github.com/jonnymuir/Wayfinder/releases).

## [Unreleased]

### Added

- A Join gateway says when the answer it waits for usually comes back, with `waitingPace`:
  `WhileTheyWait` (seconds or minutes) or `Later` (hours or days). The waiting page then tells the
  person what to expect in plain words: "Still working on it" and how long it usually takes, then
  that it is taking longer than usual, for an answer that comes back while they wait; that they can
  leave and come back, with no time claim, for one that comes back later. It moves between those
  messages on its own as time passes, says when it cannot reach the service, and announces each
  change once to screen readers. The editor asks the question in the same words.
- An optional `waitingIcon` on a Join gateway (`Hourglass`, `Search`, `Document`, `Butterfly`,
  `Hedgehog`) shows a small line drawing beside "Still working on it". None by default.
- `wayfinder-waiting.js` in `Wayfinder.Rendering.GovUk` keeps the waiting page's message current.
  A host that renders the waiting component itself should load it alongside `wayfinder-poll.js`.

### Changed

- A Join gateway with no `waitingPace` shows a neutral waiting page with no time claim, where it
  used to say a default "usually takes about 30 seconds". Validation warns
  (`GATEWAY_WAITING_PACE_UNSET`) so authors choose a pace. `waitingExpectedSeconds` now only
  applies to `WhileTheyWait`.
- `wayfinder-poll.js` no longer announces "Checking for updates" to screen readers on every check,
  and prefers a host's `data-wayfinder-poll-url` element when a page also carries a waiting
  component's own interval.

## [0.25.0] - 2026-10-24

Also releases `Wayfinder.Editor` 0.11.0, `Wayfinder.Editor.Http` 0.3.0, `Wayfinder.Engine.Http`
0.5.0, `Wayfinder.Engine.Journey` 0.3.0, `Wayfinder.Engine.Worklist` 0.5.0 and
`Wayfinder.Rendering.GovUk` 0.12.0, all built against the changed model.

### Changed (breaking)

- A service blueprint is written in YAML everywhere it is stored or travels: seed files
  (`service-blueprints/*.yaml`), the filesystem stores, the REST authoring API and the editor's
  host routes (a whole blueprint is an `application/yaml` body, both ways), the MCP tools
  (`validate_service_blueprint`, `save_service_blueprint` and `simulate_service_blueprint` take
  `blueprintYaml`; `read_service_blueprint` returns YAML), and the editor's Definition tab.
  Catalogs, diagnostics and outcomes are still JSON. To upgrade, convert each JSON blueprint to
  YAML (JSON is valid YAML, so `ServiceBlueprintYaml.Deserialize` reads one, and
  `ServiceBlueprintYaml.Serialize` writes the YAML) and reseed.
- `ServiceBlueprintYaml` replaces `ServiceBlueprintJson` as the one blueprint serializer, and the
  model carries no `System.Text.Json` attributes. Reading is strict: an unknown key or component
  type is an error naming its line and column, not silently dropped. Anchors and aliases are
  refused.
- `AddServiceBlueprintAuthoringApi()` is removed; the REST API needs no registration of its own.
- The simulate route takes the blueprint as a YAML string in its JSON body.
- `ActionDefinition.Parameters` (`params`) is a plain `IReadOnlyDictionary<string, object?>`, read
  with `GetString`, `GetInt`, `GetBool`, `GetMap` and `GetList`.
- `ServiceBlueprintStageValidationRule` has init properties instead of a positional constructor.
- `Wayfinder.Editor`: the Definition tab edits YAML; "Copy stage JSON" is "Copy stage YAML"; the
  graph's declarative attribute is `service-blueprint-yaml`; `parseServiceBlueprintYaml` is exported
  for hosts reading a blueprint from their own routes.

## [0.24.2] - 2026-10-24

Releases `Wayfinder.Editor` 0.10.2. The four lockstep packages have no changes.

### Changed

- `Wayfinder.Editor`: the notes under the Properties panel fold away. They start closed, the heading
  says how many notes are open on what is selected, and once opened they stay open as the selection
  changes.

## [0.24.1] - 2026-10-24

Releases `Wayfinder.Editor` 0.10.1. The four lockstep packages have no changes.

### Fixed

- `Wayfinder.Editor`: the way to add a note to the selected stage, gateway or route is pinned under
  the Properties panel, which scrolls on its own. It was below the panel's visible area and could
  not be reached.

## [0.24.0] - 2026-10-22

Also releases `Wayfinder.Editor` 0.10.0 (design notes, and following changes saved elsewhere).

### Added

- Design notes: feedback pinned to a stage, gateway, route, field or the whole blueprint, kept
  beside the blueprint rather than in it. `Wayfinder.Engine` adds the `DesignNote` model, the
  `IDesignNoteStore` contract with `InMemoryDesignNoteStore` as the default, and `DesignNoteService`,
  registered by `AddServiceBlueprintAuthoring()`. A host keeps notes across restarts by registering
  its own `IDesignNoteStore`.
- `Wayfinder.Engine.Mcp`: `list_design_notes`, `add_design_note`, `resolve_design_note` and
  `reply_to_design_note`, so an AI design partner can work through the designer's notes and pin its
  own questions. The authoring guide resource teaches the cycle.
- `Wayfinder.Engine.Api`: design-note routes under `/blueprints/{definitionKey}/notes`, inside the
  authoring API's authorization requirement, with notes attributed to the signed-in user.
- `Wayfinder.Editor`: a Notes tab listing every note, with reply, resolve, reopen and delete; an
  **Add a note** form under the Properties panel for the selected stage, gateway or route; and a
  marker with the open-note count on each stage and gateway. Notes refresh every 5 seconds. A
  `ServiceBlueprintSource` offers notes through an optional `designNotes` (`DesignNoteSource`);
  `HttpDesignNoteSource` talks to the routes above and `InMemoryDesignNoteSource` keeps them in the
  page. `<wayfinder-service-blueprint-graph>` takes a `noteCounts` property.

### Changed

- `Wayfinder.Editor`: when someone else saves the open blueprint, such as an AI design partner over
  MCP, and the author has nothing unsaved, the editor moves to the new version in place instead of
  going read-only. It keeps the zoom, position and selection, marks new and changed stages and
  gateways on the canvas, draws changed routes in green, and shows a notice summarising what changed
  since the author last dismissed it. It checks every 3 seconds for ten minutes after an outside
  save, then every 15 again. With unsaved work, including Definition text not yet applied, the
  editor goes read-only as before.
- `ServiceBlueprintAuthoringService.DeleteAsync` also deletes the blueprint's design notes.
- `InMemoryServiceBlueprintSource` versions blueprints as a real store does: a save must be made at
  the stored version, or it fails with a conflict, and moves the version on by one. It also
  implements `checkVersion`.

## [0.23.0] - 2026-10-19

Also releases `Wayfinder.Rendering.GovUk` 0.11.0 (wait screens name who they wait for; conversation
times keep their offset).

### Changed

- An advance that is saved but takes the request out of everything the actor can see now answers
  `responseState` `moved_on`, with no problems, instead of an `ACCESS_DENIED` error. A team's reply
  that hands a conversation back to the person who wrote in is a success; reporting it as access
  denied made hosts treat a saved reply as a failed one. **Upgrade:** a host that treated
  `ACCESS_DENIED` after its own advance as "done" checks for `moved_on` instead. A refused advance
  (someone else's request, say) is still an `ACCESS_DENIED` error.
- A join's wait screen no longer appends "Waiting for: " and the internal keys of the queues it is
  waiting on to the authored `waitingContent`. The `waiting` component carries them separately, by
  display name, in `WaitingFor`, and `Wayfinder.Rendering.GovUk` shows them on their own line.
- `Wayfinder.Rendering.GovUk` shows a conversation message's time in the offset it carries, without
  converting it to UTC or adding "UTC", so a host shows times in its visitors' time zone by
  converting them before rendering.

## [0.22.0] - 2026-10-18

Also releases `Wayfinder.Rendering.GovUk` 0.10.0 (the conversation rendering) and `Wayfinder.Editor`
0.9.0 (the conversation component in the editor).

### Added

- A `conversation` component for a running conversation between the person a request belongs to and
  the team handling it. Each submitted message is added to the end of the thread with which queue
  wrote it and when, instead of replacing the last one, so a "reply, ask for more, reply again" loop
  keeps its whole history. Each side sees its own messages as "You". Messages are capped at
  `maxLength` (5,000 characters by default).

### Fixed

- A plain stage-to-stage hop into a different queue now clears the pickup, as a gateway crossing
  already did. Before, the work stayed assigned to whoever had picked it up in the queue it left, so
  nobody in the new queue could see it: a request a team sent back to the citizen without a gateway
  vanished from the citizen's view, and work handed to another team never reached its worklist. If
  your blueprint relied on the same person acting again in the new queue, they now pick it up there.

### Changed

- `ClaimInstances` claims every anonymous request of a blueprint that lets a person have several
  (`requestPolicy` `"multiple"` or `"prompt"`), even if the member already has some. Only a
  one-per-person (`"single"`) blueprint still leaves the anonymous request behind.

## [0.21.0] - 2026-10-18

### Added

- `ServiceRequest.HandedOverAt` records when a request first reached a queue other than its starting
  stage's, such as a submitted contact message landing in a team's worklist. It is set once and
  never cleared. A store that expires anonymous requests with the visitor's session must keep
  handed-over ones, or a submission is lost before anyone picks it up. Wayfinder.Umbraco's store
  uses it.

## [0.20.0] - 2026-10-16

### Changed

- A `source: "service"` calculation field the host doesn't supply is now read from the request's
  stored values, where the engine's own actions (such as a bulk-dataset ingest's counts) write
  theirs. A host's `serviceInputsResolver` or `ResolveServiceInputs` override only needs to supply
  what it looks up, and a pass-through resolver that copied stored values can be deleted. A value
  the host supplies still wins.

## [0.19.1] - 2026-10-16

### Bug fixes

- `AddProcessManager` now registers the logger `ProcessManagerEngine` needs, so a host that calls
  only `AddProcessManager` (and registers a sanitizer) no longer fails with "Unable to resolve
  service for type ILogger".

### Documentation

- The README quickstart runs as written, and a test keeps it that way.
- A guides index, a glossary, and validated example blueprints in `docs/examples/`.
