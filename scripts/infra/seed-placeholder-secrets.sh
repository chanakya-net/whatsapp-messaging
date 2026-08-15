#!/usr/bin/env bash
set +x
set -euo pipefail

readonly SCRIPT_NAME="${0##*/}"
readonly -a APPROVED_SECRET_NAMES=(
  'rabbitmq-connection-string'
  'new-relic-otlp-headers'
  'whatsapp-provider-placeholder'
  'email-provider-placeholder'
)

usage() {
  printf '%s\n' \
    "Usage: $SCRIPT_NAME --vault-name NAME [--subscription UUID] [--overwrite-non-placeholder]" \
    '' \
    'Seeds only these approved placeholders:' \
    '  rabbitmq-connection-string' \
    '  new-relic-otlp-headers' \
    '  whatsapp-provider-placeholder' \
    '  email-provider-placeholder' \
    '' \
    'Options:' \
    '  --vault-name NAME              Target kv-msgbr-{dev|prod}-cin-NNN vault.' \
    '  --subscription UUID            Azure subscription containing the vault.' \
    '  --overwrite-non-placeholder    Request guarded replacement of existing values.' \
    '  -h, --help                     Show this help without contacting Azure.'
}

fail() {
  printf 'ERROR: %s\n' "$1" >&2
  exit 1
}

placeholder_for() {
  case "$1" in
    rabbitmq-connection-string)
      printf '%s' 'amqps://placeholder:placeholder@rabbitmq.invalid:5671/messagebridge'
      ;;
    new-relic-otlp-headers)
      printf '%s' 'api-key=PLACEHOLDER_NOT_A_REAL_KEY'
      ;;
    whatsapp-provider-placeholder)
      printf '%s' 'whatsapp-provider-disabled-placeholder'
      ;;
    email-provider-placeholder)
      printf '%s' 'email-provider-disabled-placeholder'
      ;;
    *) fail 'Internal approved-placeholder mapping is incomplete.' ;;
  esac
}

secret_exists() {
  local target="$1"
  local existing
  while IFS= read -r existing; do
    [[ "$existing" == "$target" ]] && return 0
  done <<<"$existing_secret_names"
  return 1
}

run_az() {
  if [[ -n "$subscription" ]]; then
    az "$@" --subscription "$subscription"
  else
    az "$@"
  fi
}

parse_args() {
  vault_name=''
  subscription=''
  overwrite_non_placeholder=false

  while (($#)); do
    case "$1" in
      --vault-name)
        (($# >= 2)) || fail '--vault-name requires a value.'
        vault_name="$2"
        shift 2
        ;;
      --subscription)
        (($# >= 2)) || fail '--subscription requires a value.'
        subscription="$2"
        shift 2
        ;;
      --overwrite-non-placeholder)
        overwrite_non_placeholder=true
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *) fail "Unknown argument: $1" ;;
    esac
  done

  [[ -n "$vault_name" ]] || fail '--vault-name is required.'
  [[ "$vault_name" =~ ^kv-msgbr-(dev|prod)-cin-[0-9]{3}$ ]] || fail 'Vault name must match kv-msgbr-{dev|prod}-cin-NNN.'
  if [[ -n "$subscription" ]]; then
    [[ "$subscription" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$ ]] || fail '--subscription must be a UUID.'
  fi
}

inspect_current_values() {
  local index name current_value approved_value
  actions=()

  if ! existing_secret_names="$(run_az keyvault secret list \
    --vault-name "$vault_name" \
    --query '[].name' \
    --output tsv \
    --only-show-errors 2>/dev/null)"; then
    fail "Unable to inspect secret names in vault $vault_name; no changes made."
  fi

  for index in "${!APPROVED_SECRET_NAMES[@]}"; do
    name="${APPROVED_SECRET_NAMES[$index]}"
    if ! secret_exists "$name"; then
      actions[$index]='create'
      continue
    fi

    if ! current_value="$(run_az keyvault secret show \
      --vault-name "$vault_name" \
      --name "$name" \
      --query value \
      --output tsv \
      --only-show-errors 2>/dev/null)"; then
      unset current_value
      fail "Unable to inspect $vault_name/$name; no changes made."
    fi

    approved_value="$(placeholder_for "$name")"
    if [[ "$current_value" == "$approved_value" ]]; then
      actions[$index]='skip'
    else
      actions[$index]='replace'
    fi
    unset current_value approved_value
  done
  unset existing_secret_names
}

confirm_replacements() {
  local index name expected confirmation
  for index in "${!APPROVED_SECRET_NAMES[@]}"; do
    [[ "${actions[$index]}" == 'replace' ]] || continue
    name="${APPROVED_SECRET_NAMES[$index]}"

    if [[ "$overwrite_non_placeholder" != true ]]; then
      fail "Refusing to overwrite non-placeholder $vault_name/$name; no changes made."
    fi

    expected="overwrite $vault_name/$name"
    printf 'Type "%s" to confirm replacement of %s/%s: ' "$expected" "$vault_name" "$name" >&2
    IFS= read -r confirmation || fail 'Confirmation input ended; no changes made.'
    [[ "$confirmation" == "$expected" ]] || fail 'Confirmation did not match; no changes made.'
    unset confirmation
  done
}

write_placeholders() {
  local index name approved_value
  for index in "${!APPROVED_SECRET_NAMES[@]}"; do
    name="${APPROVED_SECRET_NAMES[$index]}"
    if [[ "${actions[$index]}" == 'skip' ]]; then
      printf 'Placeholder already active: %s/%s\n' "$vault_name" "$name"
      continue
    fi

    approved_value="$(placeholder_for "$name")"
    if ! run_az keyvault secret set \
      --vault-name "$vault_name" \
      --name "$name" \
      --value "$approved_value" \
      --only-show-errors \
      --output none >/dev/null 2>&1; then
      unset approved_value
      fail "Failed to seed $vault_name/$name."
    fi
    unset approved_value
    printf 'Seeded placeholder: %s/%s\n' "$vault_name" "$name"
  done
}

main() {
  parse_args "$@"
  command -v az >/dev/null 2>&1 || fail 'Azure CLI (az) is required.'

  inspect_current_values
  confirm_replacements
  write_placeholders
}

main "$@"
