#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

UNIT_OUTPUT_DIR="${REPO_ROOT}/coverage-unit"
INTEGRATION_OUTPUT_DIR="${REPO_ROOT}/coverage-integration"

# Clean previous runs
rm -rf "$UNIT_OUTPUT_DIR" "$INTEGRATION_OUTPUT_DIR"
mkdir -p "$UNIT_OUTPUT_DIR" "$INTEGRATION_OUTPUT_DIR"

# Run unit tests with coverage (exclude integration tests)
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
reportgenerator \
  -reports:"$UNIT_OUTPUT_DIR/**/coverage.cobertura.xml" \
  -targetdir:"$UNIT_OUTPUT_DIR/report" \
  -reporttypes:"HtmlSummary;HtmlChart;XmlSummary" \
  -historydir:"$UNIT_OUTPUT_DIR/history" \
  -verbosity:off

# Extract coverage metrics from XML summary
if [ -f "$UNIT_OUTPUT_DIR/report/Summary.xml" ]; then
  grep -E 'LineCoverage|BranchCoverage' "$UNIT_OUTPUT_DIR/report/Summary.xml" || echo "No summary XML found"
else
  echo "Unit coverage report generated at $UNIT_OUTPUT_DIR/report/index.html"
fi

# Run integration tests separately (no thresholds applied)
echo "Running integration tests with coverage..."
dotnet test \
  MessageBridge.sln \
  --configuration Release \
  --settings tests/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$INTEGRATION_OUTPUT_DIR" \
  --logger "console;verbosity=minimal" \
  --filter "FullyQualifiedName~IntegrationTests"

echo "Generating integration coverage report..."
if ls "$INTEGRATION_OUTPUT_DIR"/**/coverage.cobertura.xml 1>/dev/null 2>&1; then
  reportgenerator \
    -reports:"$INTEGRATION_OUTPUT_DIR/**/coverage.cobertura.xml" \
    -targetdir:"$INTEGRATION_OUTPUT_DIR/report" \
    -reporttypes:"HtmlSummary;HtmlChart" \
    -historydir:"$INTEGRATION_OUTPUT_DIR/history" \
    -verbosity:off
  echo "Integration coverage report generated at $INTEGRATION_OUTPUT_DIR/report/index.html"
else
  echo "No integration coverage data found"
fi

echo ""
echo "Coverage verification complete."
echo "Reports available at:"
echo "  Unit:        $UNIT_OUTPUT_DIR/report/index.html"
echo "  Integration: $INTEGRATION_OUTPUT_DIR/report/index.html"
