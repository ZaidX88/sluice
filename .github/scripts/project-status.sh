#!/usr/bin/env bash
# Puts an issue or pull request on the Sluice project board (if it isn't there yet) and sets its Status column.
#
#   project-status.sh <node id of the issue or pull request> <Triage|Ready|In progress|In review|Done>
#
# Needs GH_TOKEN with the "project" scope (the PROJECT_TOKEN secret): a project owned by a personal account can't be
# changed with the workflow's own token. PROJECT_OWNER and PROJECT_NUMBER name the board (defaults: the repository
# owner's project 1). Only reads and writes project data through the API; it never runs code from a pull request.
set -euo pipefail

content_id="$1"
status="$2"
owner="${PROJECT_OWNER:-${GITHUB_REPOSITORY_OWNER:?}}"
number="${PROJECT_NUMBER:-1}"

project=$(gh api graphql -f query='
  query($owner: String!, $number: Int!) {
    user(login: $owner) {
      projectV2(number: $number) {
        id
        field(name: "Status") { ... on ProjectV2SingleSelectField { id options { id name } } }
      }
    }
  }' -f owner="$owner" -F number="$number" --jq '.data.user.projectV2')

project_id=$(jq -r '.id' <<<"$project")
field_id=$(jq -r '.field.id' <<<"$project")
option_id=$(jq -r --arg s "$status" '.field.options[] | select(.name == $s) | .id' <<<"$project")
if [ -z "$option_id" ]; then
  echo "The board has no \"$status\" column" >&2
  exit 1
fi

# Adding an item that is already on the board returns the existing item.
item_id=$(gh api graphql -f query='
  mutation($project: ID!, $content: ID!) {
    addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } }
  }' -f project="$project_id" -f content="$content_id" --jq '.data.addProjectV2ItemById.item.id')

gh api graphql -f query='
  mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
    updateProjectV2ItemFieldValue(input: {
      projectId: $project, itemId: $item, fieldId: $field, value: { singleSelectOptionId: $option }
    }) { projectV2Item { id } }
  }' -f project="$project_id" -f item="$item_id" -f field="$field_id" -f option="$option_id" > /dev/null

echo "$content_id -> $status"
