#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Локальный деплой — уже на сервере
if [ "$1" = "--local" ] || [ "$1" = "-l" ]; then
    echo "=== Local deploy ==="
    cd "$SCRIPT_DIR"
    mkdir -p data

    if [ ! -f data/config.json ]; then
        cat > data/config.json << 'CFG'
{
  "TargetSteamId64": "76561198123456789",
  "CommendFriendly": true,
  "CommendLeader": true,
  "CommendTeacher": true,
  "CooldownHours": 12,
  "LoginDelayMs": 5000,
  "BatchSize": 10,
  "BatchDelayMs": 30000,
  "MatchId": 8,
  "AccountsFile": "accounts.txt"
}
CFG
        echo "Created data/config.json — не забудь вписать свой SteamID64!"
    fi

    [ ! -f data/accounts.txt ] && touch data/accounts.txt

    docker compose up -d --build 2>&1

    echo ""
    echo "=== Готово ==="
    echo "Панель: http://$(hostname -I | awk '{print $1}'):5050"
    exit 0
fi

# Удалённый деплой — с локальной машины на сервер
SERVER="${1:-$FARM_SERVER}"
if [ -z "$SERVER" ]; then
    echo "Usage: ./deploy.sh --local     # на этом сервере"
    echo "       ./deploy.sh user@host   # удалённо через ssh"
    exit 1
fi

echo "=== Deploying to $SERVER ==="

echo "[1/3] Syncing files..."
rsync -az --delete \
    --exclude='bin/' \
    --exclude='obj/' \
    --exclude='publish/' \
    --exclude='data/' \
    --exclude='.git/' \
    --exclude='*.md' \
    "$SCRIPT_DIR/" "$SERVER:/opt/cs2-commend-farm/"

echo "[2/3] Building & starting..."
ssh "$SERVER" bash -s << 'REMOTE'
set -e
cd /opt/cs2-commend-farm
mkdir -p data

if [ ! -f data/config.json ]; then
cat > data/config.json << 'CFG'
{
  "TargetSteamId64": "76561198123456789",
  "CommendFriendly": true,
  "CommendLeader": true,
  "CommendTeacher": true,
  "CooldownHours": 12,
  "LoginDelayMs": 5000,
  "BatchSize": 10,
  "BatchDelayMs": 30000,
  "MatchId": 8,
  "AccountsFile": "accounts.txt"
}
CFG
echo "Created data/config.json — не забудь вписать свой SteamID64!"
fi

[ ! -f data/accounts.txt ] && touch data/accounts.txt

docker compose up -d --build 2>&1

echo ""
echo "=== Готово ==="
echo "Панель: http://$(hostname -I | awk '{print $1}'):5050"
REMOTE

echo "[3/3] Done."
