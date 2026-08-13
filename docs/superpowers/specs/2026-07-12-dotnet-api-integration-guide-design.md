# .NET API Integration Guide Design

## Purpose

Create a detailed, step-by-step guide for developers who want to integrate MessageBridge into another ASP.NET Core API. The guide must cover both direct RabbitMQ publishing and the Entity Framework Core outbox workflow while accurately reflecting the repository's current implementation.

## Documentation Structure

Add `docs/dotnet-api-integration.md` as the main integration tutorial and link it from the root `README.md`. Keep `docs/publisher.md` as the compact publisher API reference.

The integration guide will contain:

1. An architecture and responsibility overview showing the consuming API, RabbitMQ, the MessageBridge worker, and PostgreSQL where applicable.
2. A direct-versus-outbox decision table.
3. Prerequisites and compatibility requirements, including .NET 10, RabbitMQ, PostgreSQL for the outbox, and access to the private NuGet feed.
4. Private NuGet source configuration and required package installation commands.
5. Local infrastructure startup using the repository's Docker Compose configuration.
6. Complete ASP.NET Core configuration examples for `appsettings.json`, environment variables, MassTransit RabbitMQ registration, and MessageBridge publisher options.
7. An injectable application service and API endpoint examples for WhatsApp and email confirmation messages.
8. Publisher result handling, validation failure mapping, cancellation, correlation identifiers, tenant restrictions, logging, and secret-handling guidance.
9. EF Core outbox configuration, including the DbContext model, `IDbContextFactory<TContext>`, service registration, migrations, transaction boundaries, dispatcher settings, cleanup, and operational considerations.
10. Startup and end-to-end verification steps, plus focused troubleshooting guidance.

## Configuration Design

Examples will place connection details and publisher topology values in configuration rather than hard-code production credentials. The guide will show local placeholder values and their environment-variable equivalents. RabbitMQ credentials must be sourced from user secrets, environment variables, or a production secrets provider.

The direct setup will show the consuming API registering MassTransit with RabbitMQ before registering `IMessageBridgePublisher`. Publisher topology values will use the repository defaults:

- Exchange: `messagebridge.commands`
- WhatsApp routing key: `whatsapp.send`
- Email routing key: `email.confirmation`

## Direct Publishing Flow

The consuming controller will call an application-level messaging service, which maps API input to `SendWhatsAppMessageRequest` or `SendEmailConfirmationRequest`. The service will inspect `ErrorOr<MessageBridgePublisherResult>` and return an explicit success or validation result. Transport exceptions will be treated as infrastructure failures and translated at the API boundary without logging sensitive message content or confirmation codes.

## Outbox Flow

The guide will explain the intended transaction sequence:

1. Update application state.
2. Add a `MessageBridgeOutboxMessage` using the same scoped DbContext.
3. Commit both changes with one `SaveChangesAsync` call or explicit transaction.
4. Allow the hosted dispatcher to publish pending rows.
5. Retain or clean published rows according to the configured retention policy.

Because the current package does not provide a public typed publisher that automatically creates outbox rows, the guide will not describe `IMessageBridgePublisher` as an automatic outbox writer. Any manual serialization example will use the public protobuf contract types and reproduce the headers expected by the current transport.

## Current Compatibility Warnings

The guide will prominently document two implementation limitations:

1. `MassTransitMessageBridgeTransport` publishes a private envelope type, while the worker registers consumers for typed application messages. The repository currently lacks an end-to-end test proving that `IMessageBridgePublisher` messages are consumed by `MessageBridge.Worker` through the documented topology.
2. `AddMessageBridgeOutboxPublisher<TContext>` registers storage, dispatcher, and cleanup infrastructure, but it does not replace `IMessageBridgePublisher` with an outbox-backed implementation.

These warnings prevent users from treating the current packages as production-ready end-to-end integration without first validating or fixing these boundaries.

## Error Handling and Security

The guide will distinguish request validation errors from RabbitMQ transport failures and explain how callers should map them to HTTP responses. Examples will avoid logging phone numbers, message bodies, email addresses, confirmation codes, connection strings, and credentials.

## Verification

Documentation verification will include:

- Checking all referenced types, option names, defaults, and validation ranges against source code.
- Building the solution to ensure repository state remains valid.
- Running available Markdown and link checks when their tools are installed.
- Scanning the guide for placeholders, contradictions, and unsupported guarantees.

## Files in Scope

- Add `docs/dotnet-api-integration.md`.
- Update `README.md` with a link to the new guide and concise compatibility wording where necessary.
- Do not change runtime behavior as part of this documentation task.
