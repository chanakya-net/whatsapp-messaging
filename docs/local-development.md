# Local Development Environment

This guide explains how to set up a local development environment using Docker Compose for RabbitMQ and PostgreSQL.

## Prerequisites

- Docker and Docker Compose installed
- .NET 10 SDK or later
- Visual Studio Code, Visual Studio, or your preferred .NET IDE

## Quick Start

### 1. Start Docker Compose Services

```bash
docker-compose up -d
```

This starts:
- **RabbitMQ** with management UI on `http://localhost:15672` (credentials: guest/guest)
- **PostgreSQL** on `localhost:5432` (credentials: dev/dev, database: messagebridge_dev)

### 2. Verify Services Are Ready

```bash
# Check RabbitMQ health
curl -u guest:guest http://localhost:15672/api/health/checks/local-alarms

# Check PostgreSQL connectivity
psql -h localhost -U dev -d messagebridge_dev -c "SELECT 1;"
```

Health checks are built into the compose config and will show `healthy` once ready:

```bash
docker-compose ps
```

### 3. Build and Run the Solution

```bash
dotnet build MessageBridge.sln
```

The Worker will automatically use the Development environment settings from `appsettings.Development.json`, which points to the local Docker services.

## Configuration

### Environment Variables

Local values are pre-configured in `docker-compose.yml` and `appsettings.Development.json`:

| Service | Host | Port | Username | Password |
|---------|------|------|----------|----------|
| RabbitMQ | localhost | 5672 | guest | guest |
| RabbitMQ Management | localhost | 15672 | guest | guest |
| PostgreSQL | localhost | 5432 | dev | dev |

Database: `messagebridge_dev`

### .env.local (Optional)

For local overrides, create `.env.local` (not committed):

```bash
cp .env.example .env.local
# Edit .env.local with local values if needed
```

## RabbitMQ Management UI

Access the RabbitMQ management interface at:

```
http://localhost:15672
```

Default credentials: `guest` / `guest`

Use this to:
- Monitor queues and message counts
- Create exchanges and bindings
- Debug message flow during development

## PostgreSQL

Connect to the local database using any PostgreSQL client:

```bash
# Command line
psql -h localhost -U dev -d messagebridge_dev

# Connection string for .NET
Host=localhost;Port=5432;Database=messagebridge_dev;Username=dev;Password=dev;
```

## Running Tests

Integration tests use the same Docker Compose services. Ensure compose is running before executing tests:

```bash
dotnet test MessageBridge.sln
```

## Stopping Services

```bash
docker-compose down
```

To also remove volumes and clean up all data:

```bash
docker-compose down -v
```

## Troubleshooting

**Services not starting**: Check Docker daemon is running and ports 5432, 5672, 15672 are not in use.

**PostgreSQL connection timeout**: Wait for health check to complete (10-15 seconds), then retry.

**RabbitMQ management UI unreachable**: Verify the rabbitmq container is running: `docker-compose ps`

## Next Steps

- Set up Entity Framework Core migrations targeting the local PostgreSQL instance
- Configure MassTransit with RabbitMQ endpoints for local message testing
- Integrate Testcontainers for isolated integration test environments
