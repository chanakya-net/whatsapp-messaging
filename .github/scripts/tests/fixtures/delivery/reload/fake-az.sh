#!/usr/bin/env bash
set -euo pipefail

: "${FAKE_AZ_CASE:?}" "${FAKE_AZ_CALL_LOG:?}" "${FAKE_AZ_STATE_DIR:?}"
mkdir -p "$FAKE_AZ_STATE_DIR"
printf '%s\n' "$*" >>"$FAKE_AZ_CALL_LOG"

arg_value() {
  local wanted=$1
  shift
  while (($#)); do
    if [[ "$1" == "$wanted" ]]; then printf '%s' "$2"; return; fi
    shift
  done
}

next_count() {
  local file="$FAKE_AZ_STATE_DIR/$1.count" count=0
  [[ ! -f "$file" ]] || read -r count <"$file"
  count=$((count + 1)); printf '%s\n' "$count" >"$file"; printf '%s' "$count"
}

json_bool() {
  jq -e "($1) as \$value | if \$value == null then true else \$value end" \
    <<<"$FAKE_AZ_CASE" >/dev/null
}

prior_digest="sha256:$(printf 'c%.0s' {1..64})"
prior_image="ghcr.io/chanakya-net/whatsapp-messaging/worker@$prior_digest"
prior_revision="$WORKER_NAME--prior"
new_revision="$WORKER_NAME--reload-123-4"
active_file="$FAKE_AZ_STATE_DIR/active"
[[ -f "$active_file" ]] || printf '%s\n' "$prior_revision" >"$active_file"

if [[ "$1 $2" == "containerapp show" ]]; then
  query="$(arg_value --query "$@")"
  case "$query" in
    *configuration.secrets*)
      serial="${RESOURCE_GROUP##*-}"
      identity="/subscriptions/$ARM_SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-messagebridge-runtime-$RELOAD_ENVIRONMENT-cin-$serial"
      suffix=""; value=null
      jq -e '.metadata.versioned == true' <<<"$FAKE_AZ_CASE" >/dev/null && suffix="/0123456789abcdef"
      if jq -e '.metadata.secret_value != null' <<<"$FAKE_AZ_CASE" >/dev/null; then
        value="$(jq -r '.metadata.secret_value' <<<"$FAKE_AZ_CASE")"
      fi
      jq -n --arg identity "$identity" --arg suffix "$suffix" --argjson value "$(jq -n --arg value "$value" 'if $value == "null" then null else $value end')" \
        '{secrets: [
          {name: "rabbitmq-connection-string", identity: $identity, keyVaultUrl: ("https://kv-messagebridge.vault.azure.net/secrets/rabbitmq-connection-string" + $suffix), value: $value},
          {name: "new-relic-otlp-headers", identity: $identity, keyVaultUrl: ("https://kv-messagebridge.vault.azure.net/secrets/new-relic-otlp-headers" + $suffix), value: null}
        ], identities: {($identity): {}}}'
      ;;
    *containers*image*) printf '%s\n' "$prior_image" ;;
    *latestRevisionName*) sed -n '1p' "$active_file" ;;
    *) exit 2 ;;
  esac
  exit 0
fi

if [[ "$1 $2 $3" == "containerapp revision show" ]]; then
  revision="$(arg_value --revision "$@")"
  query="$(arg_value --query "$@")"
  if [[ "$query" == properties.active ]]; then
    active="$(<"$active_file")"
    [[ "$revision" == "$active" ]] && printf 'true\n' || printf 'false\n'
    exit 0
  fi
  [[ "$revision" == "$new_revision" ]] && stage=new || stage=prior
  jq -c --arg stage "$stage" --arg image "$prior_image" '
    .revision[$stage] | {properties: {provisioningState: .provisioning,
      healthState: .health, active: true, template: {containers: [{image: $image}]}}}
  ' <<<"$FAKE_AZ_CASE"
  exit 0
fi

if [[ "$1 $2" == "containerapp update" ]]; then
  next_count updates >/dev/null
  json_bool '.update_success' || exit 1
  printf '%s\n' "$new_revision" >"$active_file"
  exit 0
fi

if [[ "$1 $2 $3" == "containerapp revision activate" ]]; then
  next_count activations >/dev/null
  json_bool '.rollback.activate_success' || exit 1
  printf '%s\n' "$prior_revision" >"$active_file"
  exit 0
fi

if [[ "$1 $2 $3" == "containerapp job start" ]]; then
  count="$(next_count smoke-starts)"
  jq -n --arg name "$SMOKE_JOB_NAME-execution-$count" '{name: $name}'
  exit 0
fi

if [[ "$1 $2 $3 $4" == "containerapp job execution show" ]]; then
  execution="$(arg_value --name "$@")"; index="${execution##*-}"
  status="$(jq -r --argjson index "$((index - 1))" '.smoke.statuses[$index]' <<<"$FAKE_AZ_CASE")"
  jq -n --arg status "$status" '{properties: {status: $status}}'
  exit 0
fi

printf 'Unexpected az invocation.\n' >&2
exit 2
