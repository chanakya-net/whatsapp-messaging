# Message Contracts

Public contracts live in `MessageBridge.Contracts` and are defined with Protocol Buffers Edition 2024.

## Edition 2024

Each contract file starts with:

```proto
edition = "2024";
```

Current package and namespace:

- Proto package: `messagebridge.contracts.v1`
- C# namespace: `MessageBridge.Contracts.V1`

## Buf Validation

Use Buf to validate the contract set:

```bash
buf lint
buf breaking --against '.git#branch=main'
```

## Versioning

Contract versioning follows the protobuf package, not the routing key.

- Additive changes stay in `v1`
- Breaking changes require a new package and namespace, such as `messagebridge.contracts.v2`

## Current Routing Keys

The current publisher defaults and sample configuration use:

- WhatsApp: `whatsapp.send`
- Email confirmation: `email.confirmation`

Keep these values aligned across publisher configuration, deployment docs, and consuming services.

## SendWhatsAppMessageCommand

Location: `src/MessageBridge.Contracts/protos/messagebridge/contracts/v1/send_whatsapp_message_command.proto`

Fields:

- `message_id`
- `tenant_id`
- `recipient_phone_number`
- `template_name`
- `template_language`
- `template_parameters`
- `correlation_id`
- `requested_at_utc`

Recommended meanings:

- `message_id` uniquely identifies the message
- `tenant_id` scopes the message to a tenant
- `recipient_phone_number` is the destination phone number
- `template_name` identifies the provider template
- `template_language` is the provider locale
- `template_parameters` carries optional template values
- `correlation_id` ties the message to a request or workflow
- `requested_at_utc` captures the publish time

## SendEmailConfirmationCommand

Location: `src/MessageBridge.Contracts/protos/messagebridge/contracts/v1/send_email_confirmation_command.proto`

Fields:

- `message_id`
- `tenant_id`
- `recipient_email`
- `recipient_name`
- `confirmation_token`
- `expires_at_utc`
- `correlation_id`
- `requested_at_utc`

Recommended meanings:

- `recipient_email` is the destination address
- `recipient_name` is optional display text
- `confirmation_token` is the token reference to send
- `expires_at_utc` is the token expiry time

## RabbitMQ Topology

The current topology uses a single command exchange:

- Exchange: `messagebridge.commands`
- WhatsApp routing key: `whatsapp.send`
- Email routing key: `email.confirmation`

Queue naming is host-specific and may be prefixed by environment settings, so document queue names only when you are describing a particular deployment.

## Validation Checklist

Before publishing contract changes:

1. Run `buf lint`
2. Run `buf breaking --against '.git#branch=main'`
3. Update routing-key references in the README and deployment docs
4. Regenerate C# code if the `.proto` files changed
