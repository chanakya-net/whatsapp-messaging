#!/usr/bin/env bash
# Prepare retained/exact PostgreSQL egress tfvars or verify Azure's final exact set.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

show_help() {
  cat <<'EOF'
Usage: reconcile-postgres-egress.sh MODE [OPTIONS]

Modes:
  retained  Write current Azure ranges unioned with reviewed dev/prod ranges.
  exact     Write only the complete reviewed dev/prod range union.
  verify    Fail unless Azure's complete rule set equals the reviewed union.

Options:
  --dev-root DIR          Dev OpenTofu root (default: .tofu/envs/dev)
  --prod-root DIR         Prod OpenTofu root (default: .tofu/envs/prod)
  --reviewed-ranges-file FILE
                          Prior shared exact range map (retained only)
  --dev-output-file FILE  Pre-collected, validated dev output
  --prod-output-file FILE Pre-collected, validated prod output
  --resource-group NAME   PostgreSQL resource group (retained/verify required)
  --server-name NAME      PostgreSQL flexible server (retained/verify required)
  --output FILE           JSON tfvars destination (retained/exact required)
  --parallelism N         Explicit apply parallelism override in guidance only
  --help                  Show this help

OpenTofu uses default parallelism unless --parallelism N is explicitly supplied.
This helper reads outputs/rules and prepares or verifies data; it never applies or mutates.
EOF
}

fail() { printf 'Error: %s\n' "$1" >&2; exit 1; }

take_value() {
  [ "$#" -ge 2 ] && [ -n "$2" ] || fail "$1 requires a value"
}

parse_args() {
  [ "$#" -gt 0 ] || { show_help >&2; exit 2; }
  [ "$1" != "--help" ] || { show_help; exit 0; }
  MODE="$1"
  shift
  DEV_ROOT="$REPO_ROOT/.tofu/envs/dev"
  PROD_ROOT="$REPO_ROOT/.tofu/envs/prod"
  RESOURCE_GROUP="" SERVER_NAME="" OUTPUT_FILE="" PARALLELISM=""
  REVIEWED_RANGES_FILE="" DEV_OUTPUT_FILE="" PROD_OUTPUT_FILE=""
  ROOTS_EXPLICIT=false
  while [ "$#" -gt 0 ]; do
    case "$1" in
      --dev-root|--prod-root|--reviewed-ranges-file|--dev-output-file|--prod-output-file|--resource-group|--server-name|--output|--parallelism)
        take_value "$@"
        case "$1" in
          --dev-root) DEV_ROOT="$2"; ROOTS_EXPLICIT=true ;;
          --prod-root) PROD_ROOT="$2"; ROOTS_EXPLICIT=true ;;
          --reviewed-ranges-file) REVIEWED_RANGES_FILE="$2" ;;
          --dev-output-file) DEV_OUTPUT_FILE="$2" ;;
          --prod-output-file) PROD_OUTPUT_FILE="$2" ;;
          --resource-group) RESOURCE_GROUP="$2" ;;
          --server-name) SERVER_NAME="$2" ;;
          --output) OUTPUT_FILE="$2" ;;
          --parallelism) PARALLELISM="$2" ;;
        esac
        shift 2 ;;
      --help) show_help; exit 0 ;;
      *) fail "unknown option: $1" ;;
    esac
  done
}

validate_args() {
  case "$MODE" in retained|exact|verify) ;; *) fail "mode must be retained, exact, or verify" ;; esac
  if [ "$MODE" = retained ] || [ "$MODE" = verify ]; then
    [ -n "$RESOURCE_GROUP" ] || fail '--resource-group is required for retained/verify'
    [ -n "$SERVER_NAME" ] || fail '--server-name is required for retained/verify'
  fi
  if [ "$MODE" = retained ] || [ "$MODE" = exact ]; then
    [ -n "$OUTPUT_FILE" ] || fail '--output is required for retained/exact'
  fi
  if [ -n "$PARALLELISM" ]; then
    [[ "$PARALLELISM" =~ ^[1-9][0-9]*$ ]] || fail '--parallelism must be a positive integer'
  fi
  local file_pair=false
  if [ -n "$DEV_OUTPUT_FILE" ] || [ -n "$PROD_OUTPUT_FILE" ]; then
    [ -n "$DEV_OUTPUT_FILE" ] && [ -n "$PROD_OUTPUT_FILE" ] ||
      fail '--dev-output-file and --prod-output-file must be supplied together'
    file_pair=true
  fi
  if [ -n "$REVIEWED_RANGES_FILE" ] && [ "$file_pair" = true ]; then
    fail '--reviewed-ranges-file cannot be combined with environment output files'
  fi
  if [ "$ROOTS_EXPLICIT" = true ] && { [ -n "$REVIEWED_RANGES_FILE" ] || [ "$file_pair" = true ]; }; then
    fail 'explicit roots cannot be combined with file inputs'
  fi
  if [ -n "$REVIEWED_RANGES_FILE" ] && [ "$MODE" != retained ]; then
    fail '--reviewed-ranges-file is accepted only in retained mode'
  fi
  SOURCE_KIND=roots
  [ -z "$REVIEWED_RANGES_FILE" ] || SOURCE_KIND=snapshot
  [ "$file_pair" = false ] || SOURCE_KIND=files
}

validate_input_file() {
  local file=$1 label=$2
  [ -f "$file" ] && [ ! -L "$file" ] && [ -r "$file" ] || fail "$label must be a readable regular file"
}

validate_reviewed_map() {
  local file=$1
  validate_input_file "$file" 'reviewed ranges file'
  jq -e '
    def cidr32:
      type == "string" and
      test("^(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(\\.(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}/32$") and
      . != "0.0.0.0/32";
    type == "object" and length > 0 and
    ([.[]] | length == (unique | length)) and
    all(to_entries[]; (.value | cidr32) and
      .key == ("ip-" + (.value | rtrimstr("/32") | gsub("\\."; "-"))))
  ' "$file" >/dev/null || fail 'reviewed PostgreSQL egress map is incomplete or invalid'
}

validate_environment() {
  local expected=$1 file=$2
  validate_input_file "$file" "$expected output file"
  jq -e --arg expected "$expected" '
    def cidr32:
      type == "string" and
      test("^(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(\\.(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}/32$") and
      . != "0.0.0.0/32";
    ["container_environment", "worker", "migration", "smoke"] as $required |
    . as $root |
    type == "object" and .environment == $expected and
    (.sources | type == "object") and
    ((.sources | keys | sort) == ($required | sort)) and
    all($required[]; . as $source |
      ($root.sources[$source] | type == "array" and length > 0 and all(.[]; cidr32))) and
    (.ranges | type == "array" and length > 0 and all(.[]; cidr32)) and
    (([$required[] as $source | $root.sources[$source][]] | unique | sort) ==
      ($root.ranges | unique | sort))
  ' "$file" >/dev/null || fail "$expected reviewed PostgreSQL egress output is incomplete or invalid"
}

collect_environment() {
  local root=$1 expected=$2 destination=$3
  tofu -chdir="$root" output -json reviewed_postgres_egress \
    >"$destination" 2>"$WORK_DIR/$expected-output.log" ||
    fail "unable to read $expected reviewed PostgreSQL egress output"
  validate_environment "$expected" "$destination"
}

collect_reviewed_ranges() {
  case "$SOURCE_KIND" in
    snapshot)
      validate_reviewed_map "$REVIEWED_RANGES_FILE"
      jq '[.[]] | unique | sort' "$REVIEWED_RANGES_FILE" >"$WORK_DIR/reviewed.json"
      ;;
    files)
      validate_environment dev "$DEV_OUTPUT_FILE"
      validate_environment prod "$PROD_OUTPUT_FILE"
      jq -s '[.[].ranges[]] | unique | sort' "$DEV_OUTPUT_FILE" "$PROD_OUTPUT_FILE" >"$WORK_DIR/reviewed.json"
      ;;
    roots)
      collect_environment "$DEV_ROOT" dev "$WORK_DIR/dev.json"
      collect_environment "$PROD_ROOT" prod "$WORK_DIR/prod.json"
      jq -s '[.[].ranges[]] | unique | sort' "$WORK_DIR/dev.json" "$WORK_DIR/prod.json" >"$WORK_DIR/reviewed.json"
      ;;
  esac
}

collect_azure_ranges() {
  az postgres flexible-server firewall-rule list \
    --resource-group "$RESOURCE_GROUP" --name "$SERVER_NAME" --output json \
    >"$WORK_DIR/azure.json" 2>"$WORK_DIR/azure-list.log" ||
    fail 'unable to list PostgreSQL firewall rules'
  jq -e '
    def ipv4:
      type == "string" and
      test("^(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(\\.(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}$");
    type == "array" and all(.[];
      type == "object" and (.startIpAddress | ipv4) and
      .startIpAddress == .endIpAddress and .startIpAddress != "0.0.0.0")
  ' "$WORK_DIR/azure.json" >/dev/null || fail 'Azure firewall contains broad, ranged, or invalid access'
  jq '[.[] | "\(.startIpAddress)/32"] | unique | sort' "$WORK_DIR/azure.json" >"$WORK_DIR/current.json"
}

write_tfvars() {
  local ranges_file=$1 destination_dir temporary
  destination_dir="$(dirname "$OUTPUT_FILE")"
  mkdir -p "$destination_dir"
  temporary="$OUTPUT_FILE.tmp.$$"
  jq '{reviewed_egress_ranges: (map({
    key: ("ip-" + (rtrimstr("/32") | gsub("\\."; "-"))), value: .
  }) | from_entries)}' "$ranges_file" >"$temporary"
  mv "$temporary" "$OUTPUT_FILE"
  if [ -n "$PARALLELISM" ]; then
    printf 'Prepared %s tfvars at %s; explicit OpenTofu apply override: -parallelism=%s\n' \
      "$MODE" "$OUTPUT_FILE" "$PARALLELISM" >&2
  else
    printf 'Prepared %s tfvars at %s; OpenTofu apply uses default parallelism.\n' \
      "$MODE" "$OUTPUT_FILE" >&2
  fi
}

reconcile() {
  collect_reviewed_ranges
  case "$MODE" in
    exact) write_tfvars "$WORK_DIR/reviewed.json" ;;
    retained)
      collect_azure_ranges
      jq -s 'add | unique | sort' "$WORK_DIR/current.json" "$WORK_DIR/reviewed.json" >"$WORK_DIR/retained.json"
      write_tfvars "$WORK_DIR/retained.json" ;;
    verify)
      collect_azure_ranges
      if ! jq -e --slurpfile actual "$WORK_DIR/current.json" '. == $actual[0]' "$WORK_DIR/reviewed.json" >/dev/null; then
        fail 'Azure PostgreSQL firewall set differs from the complete reviewed dev/prod union'
      fi
      printf 'Azure PostgreSQL firewall set exactly matches reviewed dev/prod egress.\n'
      ;;
  esac
}

parse_args "$@"
validate_args
WORK_DIR="$(mktemp -d)"
trap 'rm -rf -- "$WORK_DIR"' EXIT
reconcile
