# Testing Guide

Complete reference for local unit tests, Docker-backed integration tests, coverage gates, CI behavior, and troubleshooting.

## Prerequisites

- **.NET 10 SDK or later** (check with `dotnet --version`)
- **Git** for repository access and branch management
- **Docker Desktop** (macOS/Windows) or **Docker daemon** (Linux) for integration tests only—unit tests run without containers

### SDK Setup

Verify .NET SDK version matches the project:

```bash
dotnet --version
```

Expected output: `10.0.xxx` or later.

Restore all project dependencies:

```bash
dotnet restore MessageBridge.sln
```

## Unit Tests (Docker-Free)

Unit tests validate business logic, domain models, and handlers in isolation without external dependencies.

### Run All Unit Tests

```bash
dotnet test MessageBridge.UnitTests.slnf -c Release
```

**Expected runtime**: ~5–8 seconds  
**Applicability**: All environments (CI, local, no Docker required)

### Run Specific Unit Test Class

```bash
dotnet test MessageBridge.UnitTests.slnf -c Release --filter "FullyQualifiedName=MessageBridge.Publisher.Tests.PublisherRegistrationTests"
```

### Run Specific Test Method

```bash
dotnet test MessageBridge.UnitTests.slnf -c Release --filter "Name=ShouldRegisterPublisherWithDirectMode"
```

### View Verbose Output

```bash
dotnet test MessageBridge.UnitTests.slnf -c Release --logger "console;verbosity=detailed"
```

## Integration Tests (Testcontainers)

Integration tests validate end-to-end behavior: message publishing, RabbitMQ routing, PostgreSQL persistence, and error handling. Testcontainers automatically provisions isolated PostgreSQL and RabbitMQ containers per test run.

### Prerequisites for Integration Tests

- **Docker daemon running** and accessible to your user (verify: `docker ps`)
- If on Linux, ensure Docker socket has correct permissions: `ls -l /var/run/docker.sock`
- Recommended: 2+ GB available disk space for container images and volumes

### Run All Integration Tests

```bash
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj -c Release
```

**Expected runtime**: ~35–50 seconds  
**Applicability**: Local development and CI (requires Docker)

### Run Specific Integration Test Class

```bash
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj -c Release --filter "FullyQualifiedName=MessageBridge.IntegrationTests.RabbitMqPublishConsumeTests"
```

### Run Specific Integration Test Method

```bash
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj -c Release --filter "Name=ShouldPublishAndConsumeViaRabbitMq"
```

### Run Single Test with Debug Output

```bash
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj -c Release --logger "console;verbosity=detailed" --filter "Name=ShouldPublishAndConsumeViaRabbitMq"
```

## Full Test Suite (Unit + Integration)

Run all tests in sequence—unit tests first, then integration tests:

```bash
dotnet test MessageBridge.sln -c Release
```

**Expected runtime**: ~45–65 seconds (varies by system and Docker performance)  
**Artifact location**: `artifacts/test-results/` (when run locally with coverage)

### Run Without Integration Tests

If Docker is unavailable or you want fast feedback:

```bash
dotnet test MessageBridge.sln -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

## Coverage Generation and Validation

### Unit Coverage Only

Generate coverage report for unit tests without CI-enforced thresholds:

```bash
dotnet test MessageBridge.UnitTests.slnf -c Release \
  --settings tests/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory artifacts/coverage-unit
```

View the report:

```bash
# macOS
open artifacts/coverage-unit/coverage.cobertura.xml

# or use any XML viewer; report files are at artifacts/coverage-unit/
```

### Generate Reports for Both Unit and Integration

The repository provides a coverage automation script. This runs unit tests with thresholds, integration tests without thresholds, and generates HTML reports:

```bash
bash eng/verify-coverage.sh
```

**Output locations**:
- Unit coverage (with threshold validation): `artifacts/coverage-unit/report/summary.html`
- Integration coverage (informational): `artifacts/coverage-integration/report/summary.html`

**Exit code**:
- `0` if unit coverage passes thresholds
- Non-zero if unit coverage falls below required thresholds

### Coverage Thresholds

Unit test coverage is validated against thresholds. Review the validator configuration:

```bash
cat eng/coverage/validator.py
```

If thresholds are too strict during development, document the reason in the PR description and request a review. Thresholds are not changed dynamically; they must be updated in `eng/coverage/validator.py`.

## Local Application Services (Docker Compose)

Docker Compose provides PostgreSQL and RabbitMQ for **local application development only**, not for integration tests. Testcontainers automatically provisions isolated containers for each integration test run.

Start the local stack:

```bash
docker-compose up -d
```

Services available at:
- **RabbitMQ** AMQP: `localhost:5672` (credentials: `guest/guest`)
- **RabbitMQ Management UI**: `http://localhost:15672` (credentials: `guest/guest`)
- **PostgreSQL**: `localhost:5432` (database: `messagebridge_dev`, credentials: `dev/dev`)

Stop services:

```bash
docker-compose down
```

Remove volumes and reset data:

```bash
docker-compose down -v
```

## Testcontainers Behavior

Integration tests use Testcontainers to provision ephemeral containers automatically—no manual Docker setup needed for test execution.

### Container Lifecycle

1. **Test start**: Testcontainers creates isolated PostgreSQL and RabbitMQ containers with unique dynamically allocated ports.
2. **Test run**: Each test gets an isolated database (via schema or separate tables) and unique RabbitMQ topology prefix to prevent collisions.
3. **Test end**: Containers are automatically stopped and removed; no manual cleanup required.

### Pinned Container Images

- **PostgreSQL**: `postgres:17-alpine` (pinned in `IntegrationEnvironmentFixture.cs`)
- **RabbitMQ**: `rabbitmq:4.0-management-alpine` with delayed-message-exchange plugin v4.0.7

Pinned versions ensure reproducible test behavior across runs and machines.

### Dynamic Ports

Containers are assigned ephemeral ports (e.g., `5432` → `32891`). Tests discover ports dynamically via Testcontainers API; hardcoding port assumptions will fail.

### Test Isolation

- **PostgreSQL**: Each test creates a fresh database schema; data does not persist between tests.
- **RabbitMQ**: Each test uses a unique topology prefix for exchanges and queues, preventing message cross-talk.

If tests are not properly isolated, verify:
1. No shared static state in test classes
2. Each test uses `CreateMiqueTopologyPrefix()` for queue names
3. Integration fixture is properly cleaned up after each test collection

### Cleanup and Disk Space

Containers are removed immediately after tests complete. However, pulled images persist locally:

```bash
# List downloaded images
docker images | grep -E "postgres|rabbitmq"

# Free up disk space (removes unused images)
docker image prune -a --force
```

## Docker Daemon Troubleshooting

### Docker not found

```
Error: Cannot connect to Docker daemon at unix:///var/run/docker.sock
```

**Solutions**:
1. Ensure Docker Desktop is running (macOS/Windows) or Docker daemon is active (Linux).
2. On Linux, verify socket permissions: `ls -l /var/run/docker.sock` should show your user in the docker group.
3. On macOS/Windows, restart Docker Desktop.

### Testcontainers hangs on startup

**Symptoms**: Test hangs for >2 minutes during container startup.

**Solutions**:
1. Check system resources: `docker stats` (running containers consume CPU/memory).
2. Verify disk space: `df -h` (need at least 500 MB free).
3. Restart Docker daemon: `docker restart` or restart Docker Desktop.
4. Check Docker logs: On macOS, `log stream --predicate 'process=="Docker"'`.

### Port already in use

If Docker Compose services are running on `5432` or `5672`, Testcontainers may fail to allocate a container port. Stop Docker Compose before running integration tests:

```bash
docker-compose down
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj -c Release
```

## CI Behavior

GitHub Actions pipelines validate all pull requests and merges to main. This section documents actual CI behavior, timeouts, job names, artifact locations, and retention.

### PR Validation (`pr-validation.yml`)

**Trigger**: Pull request opened, reopened, or synchronized against `main`.

**Concurrency**: Cancels in-progress runs for the same PR when a new commit is pushed.

**Jobs** (via `_validation.yml`):

| Job | Timeout | Purpose |
|-----|---------|---------|
| format | 10 min | Code formatting compliance (dotnet format) |
| build | 10 min | Release build without tests |
| unit-tests | 20 min | Unit tests with coverage validation |
| integration-tests | 45 min | Testcontainers-backed integration tests |
| buf-checks | 10 min | Protobuf schema linting and breaking-change detection |
| samples | 10 min | Build the sample client application |
| package-generation | 10 min | Generate NuGet packages |

**Runtime targets** (not enforced; informational):
- Unit tests: ~5–10 minutes
- Integration tests: ~30–40 minutes

**Artifacts**:
- `unit-test-results`: Test results (`*.trx`) and HTML coverage report
- `integration-test-results`: Test results and coverage report
- `integration-container-logs`: Sanitized container logs (only on failure)

**Artifact retention**: 14 days

**Failure handling**: If any job fails, the PR is marked red. Subsequent pushes trigger a new validation run; in-progress runs are cancelled.

### Main Branch Validation (`main-validation.yml`)

**Trigger**: Direct push to `main`.

**Concurrency**: Does NOT cancel in-progress runs (strict serialization to prevent race conditions).

**Jobs**: Same as PR validation (via `_validation.yml`).

**Artifact retention**: 14 days

**On failure**: PR merge to main failed. Investigate artifacts, fix, and push a new commit to main.

### Artifact Locations in CI

Artifacts are uploaded to GitHub Actions and retained for 14 days:

```
https://github.com/chanakya-net/whatsapp-messaging/actions/runs/<RUN_ID>/attempts/<ATTEMPT>
```

Inside each run:
- **unit-test-results**: Contains `artifacts/test-results/unit/*.trx` and `artifacts/coverage-unit/report/summary.html`
- **integration-test-results**: Contains `artifacts/test-results/integration/*.trx` and `artifacts/coverage-integration/report/summary.html`
- **integration-container-logs**: Contains `artifacts/container-logs/*.log` (PostgreSQL and RabbitMQ logs, sanitized for credentials)

### Failure Logs

Container diagnostics are uploaded when integration tests fail:

1. Navigate to the failed run on GitHub Actions.
2. Expand the **integration-container-logs** artifact.
3. Download `PostgreSQL.log` and `RabbitMQ.log`.
4. Search for error messages, deadlocks, or connection failures.

Logs are automatically sanitized to remove passwords and connection strings.

### Re-running CI

To re-run a failed CI job without pushing a new commit:

1. Navigate to the workflow run on GitHub Actions.
2. Click **Re-run all jobs** or **Re-run failed jobs**.
3. Monitor the new run until completion.

## Expected Local Runtimes

These are approximate runtimes on a modern development machine (4+ CPU cores, 8+ GB RAM, SSD):

| Command | Time | Notes |
|---------|------|-------|
| `dotnet test MessageBridge.UnitTests.slnf -c Release` | 5–8s | No external dependencies |
| `dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj -c Release` | 35–50s | Includes container startup |
| `bash eng/verify-coverage.sh` | 3–5min | Unit + integration + report generation |
| `dotnet build MessageBridge.sln -c Release` | 10–20s | Incremental builds are faster |

On first run, pulling container images (`postgres:17-alpine`, `rabbitmq:4.0-management-alpine`) adds 30–60 seconds. Subsequent runs use cached images.

## Continuous Integration Resource Targets

- **Unit tests CI timeout**: 20 minutes (target: 8–10 minutes)
- **Integration tests CI timeout**: 45 minutes (target: 30–40 minutes)
- **PR validation concurrency**: 7 jobs (format, build, unit, integration, buf, samples, packages)
- **Artifact retention**: 14 days

If CI jobs consistently exceed targets, investigate:
1. New test additions (slow test suite growth)
2. Testcontainers image pull times (large container images)
3. Coverage report generation time (increasing codebase)

## Tips and Patterns

### Skip Integration Tests During Development

If Docker is unavailable or you want instant feedback:

```bash
dotnet test MessageBridge.UnitTests.slnf -c Release --watch
```

This runs unit tests in watch mode—they re-run whenever you save a file.

### Watch Mode for Development

```bash
dotnet watch test MessageBridge.UnitTests.slnf
```

### Filter by Test Category or Behavior

```bash
# Run only subscription-related tests
dotnet test MessageBridge.sln -c Release --filter "Class~Subscription"

# Run only tests for error handling
dotnet test MessageBridge.sln -c Release --filter "Name~Error"
```

### Debugging a Single Test

```bash
# Enable debug output and run a specific test
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj \
  --filter "Name=ShouldPublishAndConsumeViaRabbitMq" \
  --logger "console;verbosity=detailed" \
  -c Release
```

### Check Test Count

```bash
# Count total tests in the solution
dotnet test MessageBridge.sln --collect:"XPlat Code Coverage" --no-build -c Release --filter "Name=NONEXISTENT_TEST" 2>&1 | grep "Total tests:"
```

## Related Documentation

- [Local Development](local-development.md) — Docker Compose setup for application infrastructure
- [Deployment](deployment.md) — Container image configuration and production decisions
- [Message Contracts](contracts.md) — Protobuf versioning and breaking-change validation
- [Operations](operations.md) — Health checks, retries, and error handling

## Next Steps for Contributors

1. **Run unit tests** to verify your changes don't break existing logic.
2. **Run integration tests** to validate end-to-end behavior (requires Docker).
3. **Check coverage** with `bash eng/verify-coverage.sh` before submitting a PR.
4. **Review CI logs** if a PR validation job fails—artifacts are linked in the GitHub Actions run.
