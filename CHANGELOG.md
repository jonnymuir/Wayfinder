# Changelog

Notable changes to the Wayfinder packages. The four lockstep packages (`Wayfinder`,
`Wayfinder.Engine`, `Wayfinder.Engine.Api`, `Wayfinder.Engine.Mcp`) share the version in each
heading; a change to an independently versioned package names its own version. This project
follows [semantic versioning](https://semver.org/); before 1.0, a breaking change bumps the minor
version.

Releases up to and including v0.19.0 are listed on
[GitHub Releases](https://github.com/jonnymuir/Wayfinder/releases).

## [Unreleased]

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
