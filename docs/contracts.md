# Message Contracts

Public message contracts are defined in `MessageBridge.Contracts` using Protocol Buffers Edition 2024, generated to C# via Buf, and validated for backward-compatibility at build time.

## Protobuf Edition 2024

All `.proto` files use Protobuf Edition 2024:

```proto
edition = "2024";

package messagebridge.contracts.v1;

option csharp_namespace = "MessageBridge.Contracts.V1";
```

- Edition 2024 provides modern syntax improvements over proto3.
- Requires `protoc` 23.0+.
- Imported standard types use Edition 2024: `google.protobuf.Timestamp`.

## Buf: Linting and Breaking-Change Checks

Buf provides linting and breaking-change detection for protobuf code.

### Running Buf Locally

From repository root:

```bash
# Lint all .proto files
buf lint

# Check for breaking changes against the main branch
buf breaking --against '.git#branch=main'

# Format .proto files (must commit formatted output)
buf format -w
```

Configuration:

- `buf.yaml` defines module, lint rules, and breaking-change policies.
- `buf.gen.yaml` configures C# code generation.
- Lint rules enforce Edition 2024 usage and correct naming/casing.
- Breaking-change baseline is the current `main` branch.

If `main` is updated with additive changes, no additional baseline file is required.

### CI Verification

CI runs both checks automatically:

```bash
buf lint
buf breaking --against '.git#branch=main'
```

If breaking changes are detected, the PR is blocked until resolved.

## Versioning and Package Namespaces

MessageBridge uses semantic versioning for contracts.

**Proto Package and Namespace:**

- **Current:** `messagebridge.contracts.v1` (proto package), `MessageBridge.Contracts.V1` (C# namespace)
- **Breaking change required:** new v2 with distinct proto package `messagebridge.contracts.v2` and namespace `MessageBridge.Contracts.V2`

**Version Examples:**

- **Additive change** (e.g., new optional field): Contracts minor version bump (`1.1.0` → `1.2.0`)
- **Breaking change** (e.g., remove field, rename message): new major version with v2 proto package and namespace

**Example:** To introduce a new required field or remove an optional field:

1. Define new message in `messagebridge/contracts/v2/send_whatsapp_message_command.proto`
2. Use proto package `messagebridge.contracts.v2`, namespace `MessageBridge.Contracts.V2`
3. Update Publisher to support both v1 and v2 routing keys:
   - `send-whatsapp-message.v1` → v1 consumer
   - `send-whatsapp-message.v2` → v2 consumer
4. Run Buf linting and breaking-change checks on the new v2 definition

## Message Definitions

### SendWhatsAppMessageCommand

**Location:** `src/MessageBridge.Contracts/protos/messagebridge/contracts/v1/send_whatsapp_message_command.proto`

**Routing Key:** `send-whatsapp-message.v1`

**Fields:**

| Field | Type | Required | Max Length | Notes |
|-------|------|----------|-----------|-------|
| `message_id` | string | ✓ | 128 | Unique message identifier (ULID or custom) |
| `tenant_id` | string | ✓ | 128 | Multi-tenant isolation key |
| `recipient_phone_number` | string | ✓ | — | E.164 format (e.g., `+1234567890`) |
| `template_name` | string | ✓ | 128 | Provider template identifier |
| `template_language` | string | ✓ | — | BCP-47 language tag (e.g., `en`, `en-US`) |
| `template_parameters` | map | — | 50 entries | Optional template variables (key/value max 128 each) |
| `correlation_id` | string | — | 128 | W3C trace context or custom correlation identifier |
| `requested_at_utc` | Timestamp | ✓ | — | Request timestamp (must not be too far in future) |

**Constraints:**

- Phone numbers must be valid E.164 format
- Template language must be valid BCP-47
- Template parameters map may have up to 50 entries
- All string field max lengths enforced by protobuf field constraints

### SendEmailConfirmationCommand

**Location:** `src/MessageBridge.Contracts/protos/messagebridge/contracts/v1/send_email_confirmation_command.proto`

**Routing Key:** `send-email-confirmation.v1`

**Fields:**

| Field | Type | Required | Max Length | Notes |
|-------|------|----------|-----------|-------|
| `message_id` | string | ✓ | 128 | Unique message identifier |
| `tenant_id` | string | ✓ | 128 | Multi-tenant isolation key |
| `recipient_email` | string | ✓ | 320 | Valid email address format |
| `recipient_name` | string | — | 200 | Optional recipient display name |
| `confirmation_token` | string | ✓ | 512 | Token reference (not full URL) |
| `expires_at_utc` | Timestamp | ✓ | — | Token expiration (must be after `requested_at_utc`) |
| `correlation_id` | string | — | 128 | Trace context or custom correlation identifier |
| `requested_at_utc` | Timestamp | ✓ | — | Request timestamp |

**Constraints:**

- Email must be valid format (per RFC 5321)
- Confirmation token must not contain full URL (just the token reference)
- Expiration must be after request time

## Field Rules

All protobuf fields follow strict rules to support contract evolution:

1. **Never reuse field numbers** — removed fields must reserve their numbers
2. **Reserve removed fields and names** — when deprecating:
   ```proto
   reserved 3, 4;  // field numbers
   reserved "old_field_name";  // field names
   ```
3. **Forward compatibility** — absent optional fields must have safe defaults
4. **Backward compatibility** — new optional fields must not break old clients

**Example:** Deprecating a field

```proto
message SendWhatsAppMessageCommand {
  string message_id = 1;
  string tenant_id = 2;
  // ... other fields ...
  
  // Remove old_field at number 10:
  reserved 10;
  reserved "old_field";
}
```

## RabbitMQ Topology

Contracts define routing keys and exchange bindings:

| Entity | Value |
|--------|-------|
| Exchange | `messagebridge.commands` (topic, durable) |
| WhatsApp Routing Key | `send-whatsapp-message.v1` |
| WhatsApp Queue | `messagebridge.send-whatsapp-message.v1` (durable) |
| Email Routing Key | `send-email-confirmation.v1` |
| Email Queue | `messagebridge.send-email-confirmation.v1` (durable) |

Routing keys are **semantic and stable** across contract versions. New v2 commands use distinct routing keys (e.g., `send-whatsapp-message.v2`).

## Contract Testing

Verify serialization/deserialization for all commands:

```bash
dotnet test tests/MessageBridge.Contracts.Tests/MessageBridge.Contracts.Tests.csproj
```

Contract tests should:

- Round-trip each message (serialize → deserialize)
- Verify field types and constraints
- Test optional field defaults
- Verify integration with generated Protobuf code
