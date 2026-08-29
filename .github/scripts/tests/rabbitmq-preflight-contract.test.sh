#!/usr/bin/env bash
set -euo pipefail

validate_rabbitmq_secret() {
  local connection_string="${RabbitMq__ConnectionString:-}"

  if [[ -z "$connection_string" ]]; then
    printf '%s\n' 'RabbitMQ connection string is required.' >&2
    return 1
  fi

  if [[ "$connection_string" != amqps://* ]]; then
    printf '%s\n' 'RabbitMQ connection string must use amqps://.' >&2
    return 1
  fi
}

if [[ "${1:-}" == '--validate' ]]; then
  validate_rabbitmq_secret
  exit $?
fi

synthetic_secret='amqps://user:synthetic-secret@rabbit.example/vhost'

if missing_output="$(env -u RabbitMq__ConnectionString "$0" --validate 2>&1)"; then
  printf '%s\n' 'Missing RabbitMQ secret unexpectedly passed.' >&2
  exit 1
fi

if insecure_output="$(env RabbitMq__ConnectionString='amqp://user:synthetic-secret@rabbit.example/vhost' "$0" --validate 2>&1)"; then
  printf '%s\n' 'Plaintext RabbitMQ secret unexpectedly passed.' >&2
  exit 1
fi

if ! env RabbitMq__ConnectionString="$synthetic_secret" "$0" --validate; then
  printf '%s\n' 'Secure RabbitMQ secret unexpectedly failed.' >&2
  exit 1
fi

if [[ "$missing_output$insecure_output" == *'synthetic-secret'* ]]; then
  printf '%s\n' 'RabbitMQ preflight leaked a secret.' >&2
  exit 1
fi
