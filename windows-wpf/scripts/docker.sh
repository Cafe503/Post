#!/usr/bin/env bash
set -euo pipefail

if docker info >/dev/null 2>&1; then
    exec docker "$@"
fi

# newgrp używa członkostwa zapisanego w systemie również przed ponownym logowaniem.
if id -nG "$(id -un)" | tr ' ' '\n' | rg -qx docker; then
    # newgrp uruchamia powłokę z /etc/passwd (na tym hoście fish).
    # Argumenty przekazujemy przez JSON, aby zachować także nowe linie i stdin.
    WPF_DOCKER_ARGV=$(python3 -c 'import json,sys; print(json.dumps(["docker",*sys.argv[1:]]))' "$@")
    export WPF_DOCKER_ARGV
    exec 3<&0
    exec newgrp docker <<'SH'
exec /usr/bin/python3 -c 'import json,os; os.execvp("docker",json.loads(os.environ.pop("WPF_DOCKER_ARGV")))' <&3
SH
fi

echo 'Brak dostępu do Dockera. Uruchom scripts/setup-host.sh przez pkexec lub sudo.' >&2
exit 1
