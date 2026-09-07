#!/usr/bin/env bash
#
# Fails the build when `dotnet list package --vulnerable` reports a High or Critical
# advisory against any package in the given solution/project, UNLESS that advisory's
# id is recorded in .github/allowed-advisories.txt.
#
# Moderate/Low advisories, and anything on the allow-list, are reported in the job
# summary but never fail the build. This is the SAST "vulnerable dependency" gate
# described in the security testing plan; Dependabot alerts are advisory-only and do
# not gate a merge, so this is what actually holds the line on shipped dependencies.
#
# Usage: check-vulnerable-packages.sh <solution-or-project>   (run after `dotnet restore`)

set -euo pipefail

TARGET="${1:?usage: check-vulnerable-packages.sh <solution-or-project>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ALLOWLIST="${SCRIPT_DIR}/../allowed-advisories.txt"
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

# Accepted advisory ids: leading GHSA-… / CVE-… token on each non-comment line of the
# allow-list file. Everything after it (a '#' reason + revisit condition) is for humans.
allowed=""
if [[ -f "$ALLOWLIST" ]]; then
  allowed="$(grep -oE '^(GHSA-[[:alnum:]-]+|CVE-[0-9]{4}-[0-9]+)' "$ALLOWLIST" || true)"
fi

json="$(dotnet list "$TARGET" package --vulnerable --include-transitive --format json --output-version 1)"

# One row per (package, advisory): "<severity>\t<package>\t<resolved>\t<project>\t<url>"
rows="$(
  jq -r '
    .projects[]? as $p
    | ($p.path | sub(".*/"; "")) as $proj
    | $p.frameworks[]?
    | ((.topLevelPackages // []) + (.transitivePackages // []))[]
    | . as $pkg
    | (.vulnerabilities // [])[]
    | [ .severity, $pkg.id, $pkg.resolvedVersion, $proj, .advisoryurl ]
    | @tsv
  ' <<<"$json"
)"

blocking=""
info=""
fail=0

while IFS=$'\t' read -r severity pkg resolved project url; do
  [[ -z "${severity:-}" ]] && continue
  id="$(grep -oE '(GHSA-[[:alnum:]-]+|CVE-[0-9]{4}-[0-9]+)' <<<"$url" | head -n1 || true)"
  [[ -z "$id" ]] && id="$url"

  case "$(tr '[:upper:]' '[:lower:]' <<<"$severity")" in
    critical|high)
      if [[ -n "$allowed" ]] && grep -qxF "$id" <<<"$allowed"; then
        info+="- ✅ allow-listed — **${pkg}** ${resolved} · ${severity} · ${id} · _${project}_"$'\n'
      else
        blocking+="- ❌ **${pkg}** ${resolved} · ${severity} · [${id}](${url}) · _${project}_"$'\n'
        fail=1
      fi
      ;;
    *)
      info+="- ℹ️ **${pkg}** ${resolved} · ${severity} · ${id} · _${project}_"$'\n'
      ;;
  esac
done <<<"$rows"

{
  echo "## NuGet vulnerability scan — \`${TARGET}\`"
  if [[ -n "$blocking" ]]; then
    echo ""
    echo "### Blocking — High/Critical, not allow-listed"
    echo ""
    printf '%s' "$blocking"
  fi
  if [[ -n "$info" ]]; then
    echo ""
    echo "### Informational — Moderate/Low, or allow-listed"
    echo ""
    printf '%s' "$info"
  fi
  if [[ -z "$blocking$info" ]]; then
    echo ""
    echo "No known-vulnerable packages. 🎉"
  fi
} >>"$SUMMARY"

if [[ "$fail" -ne 0 ]]; then
  echo "::error::High/Critical vulnerable NuGet package(s) not listed in .github/allowed-advisories.txt"
  printf '%s' "$blocking"
  exit 1
fi

echo "No blocking NuGet vulnerabilities."
