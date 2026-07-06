# MessageBridge

WhatsApp and email confirmation messaging service built with .NET 10, RabbitMQ, and PostgreSQL.

## Quick Start

### Prerequisites

- .NET 10 SDK or later
- Docker & Docker Compose
- Git

### Local Setup (5 minutes)

```bash
# Clone the repository
git clone https://github.com/chanakya-net/whatsapp-messaging.git
cd whatsapp-messaging

# Start local services (RabbitMQ, PostgreSQL)
docker-compose up -d

# Build the solution
dotnet build MessageBridge.sln

# Run integration tests
dotnet test MessageBridge.sln
```

Services start automatically:

- **RabbitMQ**: `localhost:5672` (management UI: `http://localhost:15672`, credentials: `guest/guest`)
- **PostgreSQL**: `localhost:5432` (database: `messagebridge_dev`, credentials: `dev/dev`)

## Architecture Overview

```text
┌─────────────────────────────────────────────────────┐
│  Application Code                                   │
│  └─ IMessageBridgePublisher (DI)                    │
└─────────────────────────────────────────────────────┘
           │
    ┌──────▼──────┐
    │  Publisher  │  (DI registration, routing key setup)
    └──────┬──────┘
           │
    ┌──────▼──────────────────────────┐
    │  Transport Layer                │
    │  ├─ Direct Mode                 │
    │  │  └─ MassTransit/AMQP         │
    │  └─ Outbox Mode                 │
    │     ├─ Database Outbox Table    │
    │     └─ Async Dispatcher         │
    └──────┬──────────────────────────┘
           │
    ┌──────▼──────────────────────────┐
    │  RabbitMQ                       │
    │  ├─ Commands Exchange           │
    │  ├─ WhatsApp Queue              │
    │  └─ Email Confirmation Queue    │
    └──────────────────────────────────┘
           │
    ┌──────▼──────────────────────────┐
    │  Worker Service                 │
    │  ├─ Message Handlers            │
    │  ├─ Health Checks               │
    │  └─ Observability               │
    └──────────────────────────────────┘
```

## Documentation

- **[Local Development](docs/local-development.md)** — Docker Compose setup, service verification, database configuration
- **[Message Contracts](docs/contracts.md)** — Protobuf definitions, versioning, breaking-change checks
- **[Publisher Guide](docs/publisher.md)** — Direct & outbox modes, registration, usage examples
- **[Sample Client](samples/MessageBridge.SampleClient/README.md)** — Complete working example (direct & outbox)
- **[Operations](docs/operations.md)** — Health checks, retries, error queues, idempotency, cleanup
- **[Deployment](docs/deployment.md)** — Container configuration, CloudAMQP setup, secrets, production decisions

## Publishing Messages

### Direct Mode

Publishes directly to RabbitMQ (lowest latency):

```csharp
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "tenant-1";
    opts.ExchangeName = "messagebridge.commands";
    opts.WhatsAppRoutingKey = "whatsapp.send";
    opts.EmailRoutingKey = "email.confirmation";
});

var publisher = serviceProvider.GetRequiredService<IMessageBridgePublisher>();

await publisher.PublishWhatsAppMessageAsync(new SendWhatsAppMessageRequest
{
    TenantId = "tenant-1",
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    Body = "Hello!"
});
```

### Outbox Mode

Transactional outbox pattern (guarantees delivery):

```csharp
services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(...));
services.AddMessageBridgeOutboxPublisher<AppDbContext>(opts =>
{
    opts.BatchSize = 100;
    opts.Concurrency = 4;
    opts.PollIntervalMilliseconds = 500;
    opts.MaxRetryAttempts = 3;
    opts.RetryDelayMilliseconds = 50;
    opts.RetryBackoffMultiplier = 2.0;
    opts.CleanupEnabled = true;
    opts.CleanupRetentionHours = 24;
    opts.CleanupBatchSize = 500;
});
```

Messages are written to the outbox table atomically with your business transaction, then dispatched asynchronously by a background worker.

## Key Features

- **Typed API** — request DTOs validated before publish
- **Multi-tenant** — tenant ID required on every publish call
- **Transport-agnostic** — direct or outbox modes, all via same interface
- **Protobuf contracts** — versioned, breaking-change detected at build time
- **Built-in retries** — exponential backoff with configurable limits
- **Idempotent** — message IDs and correlation IDs generated automatically
- **Observable** — structured logging, distributed tracing, health endpoints

## Important Security Notes

⚠️ **Credentials & Secrets**

- **Never commit credentials** to version control (`.gitignore` protects `.env` files)
- **Local development** uses placeholder credentials (`guest/guest` for RabbitMQ, `dev/dev` for PostgreSQL)
- **CloudAMQP credentials** → use a secrets provider (e.g. Azure Key Vault, Kubernetes Secrets, environment variables)
- **Credential rotation** → update secrets in your provider; running instances must restart to pick up new values
- **No credential logging** — the framework never logs connection strings, tokens, or sensitive payload data

See [Deployment](docs/deployment.md) for secrets configuration in production.

## Project Structure

```text
├── src/
│   ├── MessageBridge.Contracts/          # Protobuf definitions
│   ├── MessageBridge.Domain/             # Business logic (dependency-free)
│   ├── MessageBridge.Application/        # Use cases & handlers
│   ├── MessageBridge.Infrastructure/     # RabbitMQ, PostgreSQL, outbox
│   ├── MessageBridge.Publisher/          # Public NuGet package
│   └── MessageBridge.Worker/             # Hosted Worker Service
├── samples/
│   └── MessageBridge.SampleClient/       # Example usage (direct & outbox)
├── tests/
│   ├── MessageBridge.*.Tests/            # Unit tests per module
│   └── MessageBridge.IntegrationTests/   # End-to-end tests
├── docs/                                 # Developer & operations guides
├── docker-compose.yml                    # Local dev services
├── MessageBridge.sln                     # Solution file
└── README.md                             # This file
```

## Building & Testing

### Build

```bash
dotnet build MessageBridge.sln
```

### Run All Tests

```bash
dotnet test MessageBridge.sln
```

### Run Tests by Category

```bash
# Unit tests only
dotnet test tests/MessageBridge.Domain.Tests/MessageBridge.Domain.Tests.csproj --filter Category=Unit --logger "console;verbosity=detailed"

# Integration tests only (requires Docker Compose running)
dotnet test tests/MessageBridge.IntegrationTests/MessageBridge.IntegrationTests.csproj
```

### Run the Sample Client

```bash
dotnet run --project samples/MessageBridge.SampleClient/MessageBridge.SampleClient.csproj
```

### Documentation Validation

```bash
markdownlint README.md docs/*.md samples/MessageBridge.SampleClient/README.md
lychee README.md docs/*.md samples/MessageBridge.SampleClient/README.md
```

Use the markdown and link checks above before publishing doc-only changes.

## Commands Reference

| Command | Purpose |
| --- | --- |
| `docker-compose up -d` | Start local RabbitMQ & PostgreSQL |
| `docker-compose down` | Stop services |
| `docker-compose ps` | Check service status & health |
| `dotnet build MessageBridge.sln` | Compile all projects |
| `dotnet test MessageBridge.sln` | Run all tests |
| `dotnet publish src/MessageBridge.Worker -c Release` | Build worker for deployment |
| `buf lint` | Check protobuf contract syntax |
| `buf breaking --against '.git#branch=main'` | Detect breaking contract changes |

## Troubleshooting

**Build fails with "project not found"**
→ Ensure Docker Compose is running: `docker-compose ps`

**RabbitMQ management UI unreachable**
→ Check container: `docker-compose logs rabbitmq`

**PostgreSQL connection timeout**
→ Wait 10–15 seconds for health check, then retry. Check logs: `docker-compose logs postgres`

**Tests fail with "address already in use"**
→ Ports 5432, 5672, 15672 in use. Run `lsof -i :5432` (macOS/Linux) or `netstat -ano | findstr :5432` (Windows)

## Contributing

- Follow [Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)
- Keep modules focused & dependency-free where possible
- Always write tests before implementation (red → green → refactor)
- Verify contract changes: `buf breaking --against '.git#branch=main'`

## Package Distribution

**MessageBridge.Publisher** is published to a private NuGet feed. See [Deployment](docs/deployment.md) for feed configuration.

## Next Steps

- [Set up local development](docs/local-development.md)
- [Review message contracts](docs/contracts.md)
- [Run the sample client](samples/MessageBridge.SampleClient/README.md)
- [Deploy to production](docs/deployment.md)

## License

See [LICENSE](LICENSE) file.
