#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

VALIDATOR="${REPO_ROOT}/eng/coverage/validator.py"
TOOL_MANIFEST="${REPO_ROOT}/.config/dotnet-tools.json"
ARTIFACT_ROOT="${REPO_ROOT}/artifacts"
UNIT_OUTPUT_DIR="${ARTIFACT_ROOT}/coverage-unit"
INTEGRATION_OUTPUT_DIR="${ARTIFACT_ROOT}/coverage-integration"

run_reportgenerator() {
  dotnet tool run reportgenerator "$@"
}

if [[ "${1:-}" == "--self-test" ]]; then
  exec python3 "${REPO_ROOT}/eng/coverage/test_validator.py"
fi

if [[ "${1:-}" == "--validate-unit" ]]; then
  [[ $# == 2 ]] || { echo "Usage: $0 --validate-unit <coverage-directory-or-summary>" >&2; exit 2; }
  exec python3 "$VALIDATOR" "$2"
fi

dotnet tool restore --tool-manifest "$TOOL_MANIFEST" >/dev/null
python3 "${REPO_ROOT}/eng/coverage/test_validator.py"

rm -rf "$UNIT_OUTPUT_DIR" "$INTEGRATION_OUTPUT_DIR"
mkdir -p "$UNIT_OUTPUT_DIR" "$INTEGRATION_OUTPUT_DIR"

echo "Running unit tests with coverage..."
dotnet test \
  MessageBridge.sln \
  --configuration Release \
  --settings tests/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$UNIT_OUTPUT_DIR" \
  --logger "console;verbosity=minimal" \
  --filter "FullyQualifiedName!~IntegrationTests"

echo "Generating unit coverage report..."
run_reportgenerator \
  -reports:"$UNIT_OUTPUT_DIR/**/coverage.cobertura.xml" \
  -targetdir:"$UNIT_OUTPUT_DIR/report" \
  -reporttypes:"HtmlSummary;HtmlChart;XmlSummary" \
  -historydir:"$UNIT_OUTPUT_DIR/history" \
  -verbosity:off

echo "Validating unit coverage thresholds..."
if python3 "$VALIDATOR" "$UNIT_OUTPUT_DIR/report"; then
  UNIT_VALIDATION_RC=0
else
  UNIT_VALIDATION_RC=$?
  echo "Unit coverage threshold validation failed (status $UNIT_VALIDATION_RC); integration reporting continues."
fi

echo "Running integration tests with coverage (unthresholded)..."
dotnet test \
  MessageBridge.sln \
  --configuration Release \
  --settings tests/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$INTEGRATION_OUTPUT_DIR" \
  --logger "console;verbosity=minimal" \
  --filter "FullyQualifiedName~IntegrationTests"

echo "Generating integration coverage report (unthresholded)..."
if find "$INTEGRATION_OUTPUT_DIR" -name coverage.cobertura.xml -print -quit | grep -q .; then
  run_reportgenerator \
    -reports:"$INTEGRATION_OUTPUT_DIR/**/coverage.cobertura.xml" \
    -targetdir:"$INTEGRATION_OUTPUT_DIR/report" \
    -reporttypes:"HtmlSummary;HtmlChart" \
    -historydir:"$INTEGRATION_OUTPUT_DIR/history" \
    -verbosity:off
  echo "Integration coverage report generated at $INTEGRATION_OUTPUT_DIR/report/summary.html"
else
  echo "No integration coverage data found"
fi

echo "Coverage verification complete."
echo "Reports available at:"
echo "  Unit:        $UNIT_OUTPUT_DIR/report/summary.html"
echo "  Integration: $INTEGRATION_OUTPUT_DIR/report/summary.html"

if (( UNIT_VALIDATION_RC != 0 )); then
  exit "$UNIT_VALIDATION_RC"
fi
