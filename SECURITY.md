# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through
[GitHub's private vulnerability reporting](https://github.com/jonnymuir/Wayfinder/security/advisories/new),
not in a public issue or pull request. Include the package and version, what an attacker can do,
and the steps to reproduce it.

This is a solo-maintained project, so reports are answered as soon as possible rather than
against a fixed timescale. Fixes ship in a new release of the affected packages, credited in the
advisory unless you'd rather not be named.

## Supported versions

Only the latest release of each package receives security fixes. The packages are pre-1.0, so
upgrade to the newest version rather than expecting a patch to an older line.

## Scope

In scope: every package in this repo (`Wayfinder`, `Wayfinder.Engine`, `.Api`, `.Mcp`, `.Http`,
`.Journey`, `.Worklist`, `Wayfinder.Rendering.GovUk`, `Wayfinder.Editor`, `Wayfinder.Editor.Http`).
Of particular interest: anything that lets blueprint input forge state, skip a gateway, read
another actor's or tenant's request, inject HTML, or force unbounded work in the calculation
language.

Out of scope: `Wayfinder.ReferenceApp` and `Wayfinder.AppHost`. They are local demo hosts that
deliberately run their authoring endpoints without authentication and never ship.
