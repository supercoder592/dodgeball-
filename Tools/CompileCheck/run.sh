#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Compile-checks every Dodgeball Ultra assembly against Unity reference assemblies.
#   Tools/CompileCheck/run.sh            -> all assemblies
#   Tools/CompileCheck/run.sh Runtime    -> only the named project(s)
# Exit code 0 = everything compiles.
# Configurations checked:
#   Runtime                   Editor build, Input System + HDRP defines
#   RuntimePlayerInputSystem  Player build (no UNITY_EDITOR), Input System + HDRP
#   RuntimePlayerLegacy       Player build, legacy Input Manager, no HDRP package
#   RenderingHDRP / Editor / EditorHDRP / TestsEditMode / Core
# -----------------------------------------------------------------------------
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
"$HERE/setup.sh" >/dev/null || { echo "setup failed"; exit 2; }

ALL=(Core Runtime RuntimePlayerInputSystem RuntimePlayerLegacy RenderingHDRP Editor EditorHDRP TestsEditMode)
if [ "$#" -gt 0 ]; then PROJECTS=("$@"); else PROJECTS=("${ALL[@]}"); fi

status=0
for p in "${PROJECTS[@]}"; do
  out="$(dotnet build "$HERE/Projects/$p.csproj" -nologo -v q -clp:NoSummary -p:CcTag="${CC_TAG:-local}" 2>&1)"
  errs="$(printf '%s\n' "$out" | grep -E ': error ' | sed -E "s#\[$HERE/Projects/[^]]*\]##; s#$REPO/##g" | sort -u)"
  warns="$(printf '%s\n' "$out" | grep -E ': warning CS' | sed -E "s#\[$HERE/Projects/[^]]*\]##; s#$REPO/##g" | sort -u | wc -l | tr -d ' ')"
  if [ -n "$errs" ]; then
    n=$(printf '%s\n' "$errs" | wc -l | tr -d ' ')
    echo "=== $p: FAILED ($n errors, $warns warnings)"
    printf '%s\n' "$errs"
    status=1
  else
    echo "=== $p: OK ($warns warnings)"
  fi
done
exit $status
