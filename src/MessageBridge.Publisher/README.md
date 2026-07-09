MessageBridge Publisher

Typed, topology-agnostic publisher for WhatsApp and email confirmation messages.

- Use `AddMessageBridgePublisher(...)` for DI registration.
- Publish via `IMessageBridgePublisher.PublishWhatsAppMessageAsync(...)`.
- Publish via `IMessageBridgePublisher.PublishEmailConfirmationAsync(...)`.

The package handles request normalization, ID/correlation generation, tenant validation,
and RabbitMQ/protobuf transport details internally.
