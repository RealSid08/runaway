#!/usr/bin/env bash
# Builds the macOS player in batch mode. Usage: tools/build.sh [setup|build|all]
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity}"
METHOD="Runaway.EditorTools.RunawayBuild.All"
[ "${1:-all}" = "setup" ] && METHOD="Runaway.EditorTools.RunawayBuild.SetupOnly"
[ "${1:-all}" = "build" ] && METHOD="Runaway.EditorTools.RunawayBuild.BuildOnly"
"$EDITOR" -batchmode -nographics -projectPath "$REPO/Runaway" -executeMethod "$METHOD" -logFile "$REPO/Runaway/Logs/build.log" < /dev/null
status=$?
grep -E "error CS|RunawayBuild" "$REPO/Runaway/Logs/build.log" | sed 's/.*Assets\//Assets\//' | sort -u | grep -v "^Runaway.EditorTools" || true
exit $status
