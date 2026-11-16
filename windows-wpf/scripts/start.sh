#!/usr/bin/env bash
set -euo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"

if [[ ! -f .env ]]; then
    umask 077
    python - <<'PY'
from pathlib import Path
import secrets
Path('.env').write_text('WINDOWS_IMAGE=dockurr/windows:latest\nWINDOWS_USERNAME=potter\nWINDOWS_PASSWORD=' + secrets.token_hex(16) + '\n')
PY
fi

mkdir -p storage shared/projects shared/status shared/artifacts
if [[ $(findmnt -n -o FSTYPE -T storage) == btrfs ]]; then
    # Nowe pliki dysku odziedziczą NOCOW; istniejące pliki pozostają bez zmian.
    chattr +C storage
fi
exec ./scripts/docker.sh compose up -d

