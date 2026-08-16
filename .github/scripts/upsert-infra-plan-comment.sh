#!/usr/bin/env bash
set -Eeuo pipefail

MAX_BYTES=60000
MAX_CHANGES=100

fail() {
  printf 'infra plan comment: %s\n' "$1" >&2
  exit 1
}

validate_layer() {
  case "${1:-}" in
    shared|dev|prod) ;;
    *) fail 'invalid layer' ;;
  esac
}

validate_plan() {
  local plan_json="$1"
  jq -e '
    def valid_actions: [
      ["no-op"], ["create"], ["read"], ["update"], ["delete"],
      ["delete", "create"], ["create", "delete"]
    ];
    type == "object" and
    (.format_version == "1.2") and
    (.resource_changes | type == "array") and
    all(.resource_changes[];
      type == "object" and
      (.mode == "managed" or .mode == "data") and
      (.type | type == "string" and test("^[A-Za-z0-9_-]+$") and length <= 128) and
      (.name | type == "string" and test("^[A-Za-z0-9_-]+$") and length <= 128) and
      (.change | type == "object") and
      (.change.actions as $actions | any(valid_actions[]; . == $actions))
    )
  ' "$plan_json" >/dev/null 2>&1 || fail 'invalid or unsupported plan JSON'
}

normalize_plan() {
  local plan_json="$1"
  jq '
    def category:
      if . == ["create"] then "Create"
      elif . == ["read"] then "Read"
      elif . == ["update"] then "Update"
      elif . == ["delete"] then "Delete"
      elif . == ["delete", "create"] or . == ["create", "delete"] then "Replace"
      else "No-op"
      end;
    [.resource_changes[] | {
      action: (.change.actions | category),
      label: (.type + "." + .name)
    }] as $resources |
    {
      counts: {
        Create: ([$resources[] | select(.action == "Create")] | length),
        Read: ([$resources[] | select(.action == "Read")] | length),
        Update: ([$resources[] | select(.action == "Update")] | length),
        Delete: ([$resources[] | select(.action == "Delete")] | length),
        Replace: ([$resources[] | select(.action == "Replace")] | length),
        Unchanged: ([$resources[] | select(.action == "No-op")] | length)
      },
      changes: [$resources[] | select(.action != "No-op")]
    }
  ' "$plan_json"
}

render_comment() {
  local layer="$1" plan_json="$2" summary_file="$3"
  validate_layer "$layer"
  [[ -f "$plan_json" && ! -L "$plan_json" ]] || fail 'plan JSON missing'
  [[ -n "$summary_file" && -d "$(dirname "$summary_file")" ]] || fail 'summary directory missing'
  command -v jq >/dev/null || fail 'jq unavailable'
  validate_plan "$plan_json"

  local normalized temporary total
  normalized="$(mktemp)"
  temporary="$(mktemp "${summary_file}.tmp.XXXXXX")"
  trap 'rm -f "$normalized" "$temporary"' RETURN
  normalize_plan "$plan_json" >"$normalized" 2>/dev/null || fail 'plan normalization failed'
  total="$(jq '.changes | length' "$normalized")"

  {
    printf '<!-- messagebridge-infra-plan:%s -->\n' "$layer"
    printf '## OpenTofu plan: `%s`\n\n' "$layer"
    printf -- '- Create: %s\n' "$(jq -r '.counts.Create' "$normalized")"
    printf -- '- Read: %s\n' "$(jq -r '.counts.Read' "$normalized")"
    printf -- '- Update: %s\n' "$(jq -r '.counts.Update' "$normalized")"
    printf -- '- Delete: %s\n' "$(jq -r '.counts.Delete' "$normalized")"
    printf -- '- Replace: %s\n' "$(jq -r '.counts.Replace' "$normalized")"
    printf -- '- Unchanged: %s\n' "$(jq -r '.counts.Unchanged' "$normalized")"
    printf '\n### Changed resources\n'
    jq -r --argjson limit "$MAX_CHANGES" \
      '.changes[:$limit][] | "- \(.action): `\(.label)`"' "$normalized"
    if ((total > MAX_CHANGES)); then
      printf '\nShowing first %s of %s changed resources.\n' "$MAX_CHANGES" "$total"
    fi
  } >"$temporary"

  [[ "$(wc -c <"$temporary" | tr -d ' ')" -le "$MAX_BYTES" ]] || fail 'rendered summary too large'
  mv "$temporary" "$summary_file"
  rm -f "$normalized"
  trap - RETURN
}

validate_summary() {
  local layer="$1" summary_file="$2" marker="$3"
  [[ -f "$summary_file" && ! -L "$summary_file" ]] || fail 'summary missing'
  [[ "$(wc -c <"$summary_file" | tr -d ' ')" -le "$MAX_BYTES" ]] || fail 'summary too large'
  [[ "$(grep -Fxc -- "$marker" "$summary_file" || true)" -eq 1 ]] || fail 'summary marker invalid'
  [[ "$(grep -Eoc '<!-- messagebridge-infra-plan:(shared|dev|prod) -->' "$summary_file" || true)" -eq 1 ]] ||
    fail 'summary contains an unexpected marker'
  if LC_ALL=C grep -q $'[\001-\010\013\014\016-\037\177]' "$summary_file"; then
    fail 'summary contains control characters'
  fi
}

send_comment() {
  local method="$1" endpoint="$2" summary_file="$3" response
  if ! response="$(jq -n --rawfile body "$summary_file" '{body: $body}' |
    gh api --method "$method" "$endpoint" --input -)"; then
    fail 'GitHub comment API failed'
  fi
  jq -e '.id | type == "number"' <<<"$response" >/dev/null || fail 'invalid GitHub API response'
}

upsert_comment() {
  local layer="$1" summary_file="$2" repository="$3" pr_number="$4"
  validate_layer "$layer"
  [[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail 'invalid repository'
  [[ "$pr_number" =~ ^[1-9][0-9]*$ ]] || fail 'invalid pull request number'
  [[ -n "${GH_TOKEN:-}" ]] || fail 'GH_TOKEN missing'
  command -v gh >/dev/null || fail 'gh unavailable'
  command -v jq >/dev/null || fail 'jq unavailable'

  local marker comments marker_count matches count comment_id
  marker="<!-- messagebridge-infra-plan:$layer -->"
  validate_summary "$layer" "$summary_file" "$marker"
  if ! comments="$(gh api --paginate --slurp \
    "repos/$repository/issues/$pr_number/comments?per_page=100")"; then
    fail 'GitHub comment lookup failed'
  fi
  jq -e 'type == "array" and all(.[]; type == "array")' <<<"$comments" >/dev/null ||
    fail 'invalid GitHub comments response'
  marker_count="$(jq --arg marker "$marker" '[.[][] |
    (.body? // "") | if type == "string" then (split($marker) | length - 1) else 0 end] |
    add // 0' <<<"$comments")"
  ((marker_count <= 1)) || fail 'multiple layer markers found'
  matches="$(jq --arg marker "$marker" '[.[][] |
    select((.body? | type == "string") and (.body | contains($marker)))]' <<<"$comments")"
  count="$(jq 'length' <<<"$matches")"
  ((count <= 1)) || fail 'multiple layer comments found'

  if ((count == 0)); then
    send_comment POST "repos/$repository/issues/$pr_number/comments" "$summary_file"
    return
  fi
  comment_id="$(jq -r '.[0].id' <<<"$matches")"
  [[ "$comment_id" =~ ^[1-9][0-9]*$ ]] || fail 'invalid existing comment ID'
  send_comment PATCH "repos/$repository/issues/comments/$comment_id" "$summary_file"
}

case "${1:-}" in
  render)
    (($# == 4)) || fail 'usage: render <layer> <plan-json> <summary-file>'
    render_comment "$2" "$3" "$4"
    ;;
  upsert)
    (($# == 5)) || fail 'usage: upsert <layer> <summary-file> <owner/repo> <pr-number>'
    upsert_comment "$2" "$3" "$4" "$5"
    ;;
  *) fail 'usage: render|upsert' ;;
esac
