# CS2 Commend Farm

Автоматическая накрутка коммендов (friendly/leader/teacher) в CS2 через SteamKit2 Game Coordinator.
Работает на Ubuntu VPS, держит 200+ аккаунтов. 
**Авто-подхват Steam Guard с почты notletters.com. Встроенная веб-панель управления.**

## Как это работает

```
accounts.txt (формат: логин:пароль:почта:пароль_почты)
    │
    ▼
┌─────────────────────────────────┐
│  CommendFarm (C# + ASP.NET)      │
│                                   │
│  Веб-панель :5050                │
│  ┌─────────────────────────────┐ │
│  │ Статистика, чекинг, управление│ │
│  └─────────────────────────────┘ │
│                                   │
│  Ферма (фоном)                   │
│  → Логин → Steam Guard → GC →   │
│  → Комменд → Следующий акк       │
│                                   │
│  Каждые 12 часов повтор          │
└─────────────────────────────────┘
    │
    ▼
  Твой Steam аккаунт получает +200 коммендов каждые 12 часов
```

**Никакой запуск CS2 не требуется** — общение с Game Coordinator напрямую.
**Steam Guard код достаётся автоматом** — через IMAP на notletters.com.

## Требования

### VPS
- Ubuntu 20.04/22.04/24.04
- Минимум: 2 vCPU, 4 GB RAM (для 200+ аккаунтов)
- Рекомендуется: 4 vCPU, 8 GB RAM

### Аккаунты
- Steam аккаунты с CS2 в библиотеке (бесплатная игра)
- **С привязанной почтой notletters.com** (для авто-ввода Steam Guard)
- Prime НЕ обязателен
- Желательно: возраст > 7 дней, 5+ часов игры, Steam уровень 2+

### Где купить аккаунты
- Площадки: plati.market, funpay, lolz.live
- Искать: "steam аккаунт cs2 без прайма notletters"
- Цена: 5-15 руб/шт при опте 200+
- **Важно:** формат выдачи — `логин:пароль:почта:пароль_от_почты`

## Быстрый старт

```bash
# 1. Клонируем на VPS
cd /opt
# (скопируй папку cs2-commend-farm на сервер)

# 2. Запускаем деплой
chmod +x deploy.sh
./deploy.sh

# 3. Редактируем config.json
nano config.json
# Меняем TargetSteamId64 на свой (узнать: https://steamid.io)

# 4. Заполняем аккаунты
nano accounts.txt
# Формат: логин:пароль:почта:пароль_от_почты
# Пример: farmer1:pass123:farmer1@notletters.com:emailpass1

# 5. Тестовый запуск (один проход)
dotnet publish/CommendFarm.dll

# 6. Запуск с веб-панелью 24/7
dotnet publish/CommendFarm.dll --loop
# → Открыть http://VPS_IP:5050
```

## Веб-панель управления

После запуска с `--loop` открывается на порту **5050**.

**Возможности:**
- Статистика в реальном времени (всего/успешно/ошибок/прогресс)
- Таблица всех аккаунтов со статусами (ok/guard/banned/failed)
- **Проверка аккаунтов** — кнопка «Проверить» для одного или «Проверить все» для масс-чека
- Чекинг показывает: логинится ли, есть ли Steam Guard, не забанен ли
- Логи в реальном времени
- Запуск/остановка фермы
- Поиск по аккаунтам

```
┌──────────────────────────────────────────┐
│  CS2 Commend Farm           ▶ Запустить  │
│  ● Работает                ✓ Проверить   │
├──────────────────────────────────────────┤
│  Всего: 200  Успешно: 150  Ошибок: 5    │
│  ████████████████░░░░ 78%               │
├──────────────────────────────────────────┤
│  Аккаунты | Логи                        │
├──────────────────────────────────────────┤
│  Логин       Статус  Почта  Проверка    │
│  farmer1     OK      ✓      12:30 OK    │
│  farmer2     GUARD   ✓      12:31 GUARD │
│  farmer3     BANNED  ✗      12:32 BAN   │
│  ...                                    │
└──────────────────────────────────────────┘
```

## Формат accounts.txt

```
# Полный формат (с авто-вводом Steam Guard):
# логин:пароль:почта:пароль_от_почты
farmer1:steampass1:farmer1@notletters.com:emailpass1
farmer2:steampass2:farmer2@notletters.com:emailpass2

# Без почты (если нет Steam Guard):
# логин:пароль
farmer3:steampass3
```

**Как это работает:**
1. При логине Steam запрашивает код подтверждения
2. CommendBot подключается к `imap.notletters.com:993`
3. Ищет письмо от Steam (noreply@steampowered.com)
4. Извлекает 5-значный код
5. Автоматически вводит и продолжает

## Steam Guard — важная инфа

При **первом входе** с нового IP Steam **всегда** запросит код на почту.
После успешного входа сохраняется `machine auth token` и `login key` —
при следующих запусках код уже не нужен.

**НО:** токены хранятся в памяти процесса. При перезапуске программы —
 Steam снова запросит код. Это нормально, скрипт достанет его автоматом.

## Конфигурация (config.json)

```json
{
  "TargetSteamId64": "7656119XXXXXXXXXX",
  "CommendFriendly": true,
  "CommendLeader": true,
  "CommendTeacher": true,
  "CooldownHours": 12,
  "LoginDelayMs": 5000,
  "BatchSize": 10,
  "BatchDelayMs": 30000,
  "AccountsFile": "accounts.txt"
}
```

## Systemd сервис (автозапуск)

```bash
sudo cp commend-farm.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now commend-farm

# Проверить статус
sudo systemctl status commend-farm

# Логи
journalctl -u commend-farm -f
```

## Cron (каждые 12 часов)

```bash
crontab -e
0 */12 * * * cd /opt/cs2-commend-farm && dotnet publish/CommendFarm.dll >> logs/cron.log 2>&1
```

## Производительность

| Аккаунтов | Батчей (по 10) | Время на проход |
|-----------|----------------|-----------------|
| 100 | 10 | ~8 минут |
| 200 | 20 | ~15 минут |
| 500 | 50 | ~40 минут |

*С учётом авто-ввода Steam Guard (+5-10 сек на аккаунт при первом входе)*

## Как проверить что работает

Зайди в CS2 → открой профиль → посмотри количество коммендов.
После прохода фермы должно увеличиться.

## Структура проекта

```
cs2-commend-farm/
├── src/CommendFarm/
│   ├── CommendFarm.csproj    # .NET 8 Web + SteamKit2 + MailKit
│   ├── Program.cs             # Веб-сервер + ферма
│   ├── WebApi.cs              # API эндпоинты + стейт
│   ├── AccountChecker.cs      # Проверка аккаунтов (логин/бан/guard)
│   ├── EmailVerifier.cs       # IMAP клиент для notletters.com
│   ├── Config.cs              # Конфигурация
│   ├── Models.cs              # BotAccount
│   ├── AccountManager.cs      # Очередь, кулдауны
│   ├── CommendBot.cs          # Steam + GC логика
│   └── wwwroot/
│       └── index.html         # Панель управления
├── config.json
├── accounts.txt
├── deploy.sh
├── commend-farm.service
└── README.md
```