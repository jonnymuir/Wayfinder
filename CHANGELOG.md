# Changelog

Notable changes to the Wayfinder packages. The four lockstep packages (`Wayfinder`,
`Wayfinder.Engine`, `Wayfinder.Engine.Api`, `Wayfinder.Engine.Mcp`) share the version in each
heading; a change to an independently versioned package names its own version. This project
follows [semantic versioning](https://semver.org/); before 1.0, a breaking change bumps the minor
version.

Releases up to and including v0.19.0 are listed on
[GitHub Releases](https://github.com/jonnymuir/Wayfinder/releases).

## [Unreleased]

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
