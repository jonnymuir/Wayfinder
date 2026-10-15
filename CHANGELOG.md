# Changelog

Notable changes to the Wayfinder packages. The four lockstep packages (`Wayfinder`,
`Wayfinder.Engine`, `Wayfinder.Engine.Api`, `Wayfinder.Engine.Mcp`) share the version in each
heading; a change to an independently versioned package names its own version. This project
follows [semantic versioning](https://semver.org/); before 1.0, a breaking change bumps the minor
version.

Releases up to and including v0.19.0 are listed on
[GitHub Releases](https://github.com/jonnymuir/Wayfinder/releases).

## [Unreleased]

### Bug fixes

- `AddProcessManager` now registers the logger `ProcessManagerEngine` needs, so a host that calls
  only `AddProcessManager` (and registers a sanitizer) no longer fails with "Unable to resolve
  service for type ILogger".

### Documentation

- The README quickstart runs as written, and a test keeps it that way.
- A guides index, a glossary, and validated example blueprints in `docs/examples/`.
