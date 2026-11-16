#!/usr/bin/env bash
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo 'Uruchom przez pkexec albo sudo.' >&2
    exit 1
fi

target_user=${1:?Podaj użytkownika hosta}
id "$target_user" >/dev/null
if [[ $(id -u "$target_user") -eq 0 ]]; then
    echo 'Podaj zwykłego użytkownika hosta.' >&2
    exit 1
fi

pacman -S --needed --noconfirm docker docker-compose
systemctl enable --now docker.service
usermod -aG docker "$target_user"
docker version
docker compose version
