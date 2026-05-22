#!/bin/bash
# Автодеплой CS2 Commend Farm на сервер
# Запуск: ./deploy.sh user@host
# Или: ./deploy.sh (использует FARM_SERVER из env)

set -e

if [ "$1" = "--local" ] || [ "$1" = "-l" ] || [ -z "$1" ] && [ -z "$FARM_SERVER" ]; then
    # Локальный деплой — уже на сервере
    echo "=== Local deploy ==="
    cd "$(dirname "$0")"
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

SERVER="${1:-$FARM_SERVER}"
if [ -z "$SERVER" ]; then
    echo "Usage: ./deploy.sh --local     # на этом сервере"
    echo "       ./deploy.sh user@host   # удалённо через ssh"
    exit 1
fi

REMOTE_DIR="/opt/cs2-commend-farm"

echo "=== Deploying to $SERVER ==="

echo "[1/3] Syncing files..."
rsync -az --delete \
    --exclude='bin/' \
    --exclude='obj/' \
    --exclude='publish/' \
    --exclude='data/' \
    --exclude='.git/' \
    --exclude='*.md' \
    ./ "$SERVER:$REMOTE_DIR/"

echo "[2/3] Building & starting..."
ssh "$SERVER" bash -s << 'REMOTE'
set -e
cd /opt/cs2-commend-farm

mkdir -p data

# Дефолтный конфиг если нет
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

echo "[3/3] Done. Панель: http://<IP>:5050"
