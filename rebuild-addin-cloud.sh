#!/usr/bin/env bash
set -euo pipefail

echo "=== DGT Add-in Cloud Rebuild ==="
echo

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

# Ensure GitHub CLI is installed
gh_cmd="gh"
if ! command -v "$gh_cmd" >/dev/null 2>&1; then
  # Git Bash on Windows (Parallels) often lacks the PATH entry
  if [[ -x "/c/Program Files/GitHub CLI/gh.exe" ]]; then
    gh_cmd="/c/Program Files/GitHub CLI/gh.exe"
  elif [[ -x "/c/Program Files (x86)/GitHub CLI/gh.exe" ]]; then
    gh_cmd="/c/Program Files (x86)/GitHub CLI/gh.exe"
  else
    echo "GitHub CLI (gh) is not installed or not on PATH." >&2
    echo "Install it from https://cli.github.com/ and run 'gh auth login'." >&2
    exit 1
  fi
fi

# Ensure gh is authenticated
if ! "$gh_cmd" auth status >/dev/null 2>&1; then
  echo "GitHub CLI is not authenticated." >&2
  echo "Run: gh auth login" >&2
  exit 1
fi

# Ensure workflow exists
workflow_path="$repo_root/.github/workflows/rebuild-addin.yml"
if [[ ! -f "$workflow_path" ]]; then
  echo "Missing workflow: $workflow_path" >&2
  echo "Create it first so GitHub can run the rebuild." >&2
  exit 1
fi

branch="$(git rev-parse --abbrev-ref HEAD | tr -d '\r')"
head_sha="$(git rev-parse HEAD | tr -d '\r')"

if ! git rev-parse --abbrev-ref --symbolic-full-name '@{u}' >/dev/null 2>&1; then
  echo "No upstream configured for branch '$branch'." >&2
  echo "Push with: git push -u origin $branch" >&2
  exit 1
fi

local_ahead="$(git rev-list --count '@{u}..HEAD' | tr -d '\r')"
if [[ "$local_ahead" -gt 0 ]]; then
  echo "Local branch is ahead of remote by $local_ahead commit(s)."
  read -r -p "Push now so cloud build uses latest commit? (y/n) " push_resp
  if [[ "$push_resp" == "y" || "$push_resp" == "Y" ]]; then
    git push
  else
    echo "Aborting cloud rebuild." >&2
    exit 1
  fi
fi

echo "Triggering GitHub Actions workflow..."
workflow_name="rebuild-addin.yml"

if ! "$gh_cmd" workflow run "$workflow_name"; then
  echo "Failed to trigger workflow." >&2
  exit 1
fi

echo "Waiting for run to start..."
run_id=""
max_wait_seconds=120
elapsed=0

while [[ -z "$run_id" && "$elapsed" -lt "$max_wait_seconds" ]]; do
  run_id="$("$gh_cmd" run list --workflow "$workflow_name" --branch "$branch" --limit 5 \
    --json databaseId,headSha \
    --jq ".[] | select(.headSha==\"$head_sha\") | .databaseId" | head -n 1 | tr -d '\r')"
  if [[ -n "$run_id" ]]; then
    break
  fi
  sleep 5
  elapsed=$((elapsed + 5))
done

if [[ -z "$run_id" ]]; then
  echo "Could not find a matching workflow run for commit $head_sha." >&2
  echo "Check runs with: gh run list --workflow $workflow_name" >&2
  exit 1
fi

echo "Run ID: $run_id"
echo "Watching build..."

if ! "$gh_cmd" run watch "$run_id"; then
  echo "Build failed or was canceled." >&2
  exit 1
fi

artifact_dir="$repo_root/artifacts/rebuild-addin/$run_id"
if [[ -d "$artifact_dir" ]]; then
  rm -rf "$artifact_dir"
fi
mkdir -p "$artifact_dir"

echo "Downloading artifacts to: $artifact_dir"
if ! "$gh_cmd" run download "$run_id" -D "$artifact_dir"; then
  echo "Artifact download failed." >&2
  exit 1
fi

echo
echo "=== Cloud Build Complete ==="
echo "Artifacts:"
echo "  $artifact_dir"

# Copy artifacts to the same publish paths as local rebuild
xll32="$(find "$artifact_dir" -type f -name "DominoGovernanceTracker-AddIn-packed.xll" | head -n 1 || true)"
xll64="$(find "$artifact_dir" -type f -name "DominoGovernanceTracker-AddIn64-packed.xll" | head -n 1 || true)"

if [[ -z "$xll32" || -z "$xll64" ]]; then
  echo "Could not find packed XLLs in: $artifact_dir" >&2
  exit 1
fi

publish_dir="$repo_root/src/DominoGovernanceTracker/bin/Debug/net472/publish"
mkdir -p "$publish_dir"

dest32="$publish_dir/DominoGovernanceTracker-AddIn-packed.xll"
dest64="$publish_dir/DominoGovernanceTracker-AddIn64-packed.xll"

cp -f "$xll32" "$dest32"
cp -f "$xll64" "$dest64"

echo "Copied artifacts to publish folder:"
echo "  32-bit: $dest32"
echo "  64-bit: $dest64"

# Optional: copy to shared artifacts mount if present
if [[ -d "/mnt/artifacts" ]]; then
  results_dir="/mnt/artifacts/results"
  mkdir -p "$results_dir"
  cp -f "$dest32" "$results_dir/"
  cp -f "$dest64" "$results_dir/"
  echo "Copied artifacts to: $results_dir"
fi
