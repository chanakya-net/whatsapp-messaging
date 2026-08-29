#!/usr/bin/env bash
set -euo pipefail

: "${FAKE_AZ_CASE:?}"
: "${FAKE_AZ_CALL_LOG:?}"
: "${FAKE_AZ_STATE_DIR:?}"
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
  local key=$1 file="$FAKE_AZ_STATE_DIR/$1.count" count=0
  [[ ! -f "$file" ]] || read -r count <"$file"
  count=$((count + 1))
  printf '%s\n' "$count" >"$file"
  printf '%s' "$count"
}

json_bool() {
  jq -e "($1) as \$value | if \$value == null then true else \$value end" \
    <<<"$FAKE_AZ_CASE" >/dev/null
}

if [[ "$1 $2 $3" == "containerapp job update" ]]; then
  json_bool '.migration.update_success' || exit 1
  exit 0
fi

if [[ "$1 $2 $3" == "containerapp job start" ]]; then
  name="$(arg_value --name "$@")"
  count="$(next_count "start-$name")"
  jq -n --arg name "$name-execution-$count" '{name: $name}'
  exit 0
fi

if [[ "$1 $2 $3 $4" == "containerapp job execution show" ]]; then
  job="$(arg_value --job "$@")"
  execution="$(arg_value --name "$@")"
  index="${execution##*-}"
  if [[ "$job" == "$MIGRATION_JOB_NAME" ]]; then
    status="$(jq -r '.migration.status' <<<"$FAKE_AZ_CASE")"
  else
    status="$(jq -r --argjson index "$((index - 1))" '.smoke.statuses[$index]' <<<"$FAKE_AZ_CASE")"
  fi
  jq -n --arg status "$status" '{properties: {status: $status}}'
  exit 0
fi

if [[ "$1 $2 $3" == "containerapp revision show" ]]; then
  revision="$(arg_value --revision "$@")"
  if [[ "$revision" == "$WORKER_NAME--new" ]]; then stage=new; else stage=rollback; fi
  jq -c --arg stage "$stage" '
    .revision[$stage] |
    {properties: {provisioningState: .provisioning, healthState: .health,
      template: {containers: [{image: ("image@" + .digest)}]}}}
  ' <<<"$FAKE_AZ_CASE"
  exit 0
fi

if [[ "$1 $2" == "containerapp show" ]]; then
  query="$(arg_value --query "$@")"
  if [[ "$query" == *containers*image* ]]; then
    json_bool '.prior_capture_success' || exit 1
    jq -r '.prior_image' <<<"$FAKE_AZ_CASE"
  else
    updates=0
    [[ ! -f "$FAKE_AZ_STATE_DIR/worker-updates.count" ]] || read -r updates <"$FAKE_AZ_STATE_DIR/worker-updates.count"
    if ((updates == 1)); then printf '%s\n' "$WORKER_NAME--new"; else printf '%s\n' "$WORKER_NAME--rollback"; fi
  fi
  exit 0
fi

if [[ "$1 $2" == "containerapp update" ]]; then
  count="$(next_count worker-updates)"
  if ((count == 1)); then json_bool '.worker_update_success' || exit 1; fi
  if ((count > 1)); then json_bool '.rollback_update_success' || exit 1; fi
  exit 0
fi

printf 'Unexpected az invocation: %s\n' "$*" >&2
exit 2
