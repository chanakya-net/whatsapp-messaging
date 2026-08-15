#!/usr/bin/env python3
import json
import re
import sys

APPROVED_NAMES = {
    "rabbitmq-connection-string",
    "new-relic-otlp-headers",
    "whatsapp-provider-placeholder",
    "email-provider-placeholder",
}
SECRET_HINT = re.compile(
    r"(password|credential|token|secret_(?:value|payload)|connection[_-]?string|api[_-]?key|otlp[_-]?headers)",
    re.IGNORECASE,
)
PAYLOAD_HINT = re.compile(r"^(?:amqps?://|api-key=)", re.IGNORECASE)
VERSIONLESS_URI = re.compile(
    r"^https://[a-z0-9-]+\.vault\.azure\.net/secrets/("
    + "|".join(re.escape(name) for name in sorted(APPROVED_NAMES))
    + r")$"
)


def load_fixture(path):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError):
        print("Fixture is unreadable or invalid JSON.", file=sys.stderr)
        raise SystemExit(1)


def inspect_string(key, value, location, errors):
    if key == "key_vault_secret_id":
        if not VERSIONLESS_URI.fullmatch(value):
            errors.append((location, "invalid or versioned Key Vault reference"))
        return
    if key in {"name", "secret_name"} and value in APPROVED_NAMES:
        return
    if PAYLOAD_HINT.search(value):
        errors.append((location, "secret-shaped payload"))
    elif SECRET_HINT.search(key):
        errors.append((location, "secret-shaped value field"))
    elif key == "value" and any(SECRET_HINT.search(part) for part in location[:-1]):
        errors.append((location, "secret-shaped output value"))


def inspect(node, location=(), errors=None):
    errors = [] if errors is None else errors
    if isinstance(node, dict):
        if node.get("type") == "azurerm_key_vault_secret":
            errors.append((location + ("type",), "forbidden Key Vault secret construct"))
        for key, value in node.items():
            child = location + (key,)
            if isinstance(value, str):
                inspect_string(key, value, child, errors)
            inspect(value, child, errors)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            inspect(value, location + (str(index),), errors)
    return errors


def main():
    if len(sys.argv) != 2:
        print("Usage: key-vault-plan-policy.py FILE", file=sys.stderr)
        return 2
    errors = inspect(load_fixture(sys.argv[1]))
    for location, reason in errors:
        print(f"{reason} at {'.'.join(location)}", file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
