#!/usr/bin/env bash
set -euo pipefail

echo "=== DGT Backend Server ==="

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

if [[ ! -d "$repo_root/backend" ]]; then
  echo "Missing backend directory: $repo_root/backend" >&2
  exit 1
fi

# Prefer uvicorn if available; otherwise fall back to python -m uvicorn
if command -v uvicorn >/dev/null 2>&1; then
  uvicorn backend.app:app --host 0.0.0.0 --port 8888
else
  python -m uvicorn backend.app:app --host 0.0.0.0 --port 8888
fi
