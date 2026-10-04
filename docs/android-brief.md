# NamazBar — краткое содержание проекта (для разработки Android-клиента)

## Что это
NamazBar — утилита для времён намаза (Windows и macOS) + **групповой чат только готовыми фразами**
(«Давайте вместе на намаз», «Иду», «Подождите» …). Группа = коллеги/друзья/семья; свободного
текста нет. Работает через разные сети — через relay-сервер (WebSocket).

## Состав репозитория (github.com/furqon2077/NamazBar)
| Папка | Что внутри |
|---|---|
| `server/` | Node.js relay (`ws`), вся логика в `src/hub.js`, список фраз `src/presets.js`, тесты `test/`, **`tools/test-bot.js` — готовый пример клиента на ~50 строк** |
| `docs/protocol.md` | Полное описание протокола (JSON поверх WebSocket) — это единственное, что нужно Android-приложению |
| `windows/` | C# (.NET Framework 4.x), WinForms; чат в `Chat.cs`, скины `Skins.cs` |
| `mac/` | Swift/SwiftUI; чат в `Sources/Chat.swift` |
| `render.yaml` | Деплой сервера на Render (free) |
| `.github/workflows/` | CI: windows.yml, mac.yml, server.yml, release.yml (релиз вручную: Actions → Release → версия) |

Сервер в работе: **`wss://namazbar-relay.onrender.com/ws`** (free Render: после простоя просыпается до ~60 с). Состояние — в памяти.

## Протокол (минимум для Android)
Все сообщения — JSON `{type: ...}`.

Клиент → сервер:
- `hello {userId, nick, avatar?, groups?:[{code,name,token}]}` — первое сообщение. `userId` 16–64 символов `[A-Za-z0-9_-]` (генерируется один раз и хранится), `nick` ≤ 24, `avatar` — data-URL png/jpeg/webp ≤ 32 КБ (или null).
- `createGroup {name}` → `groupCreated {group, token}`
- `joinRequest {code}` → `joinPending`; участники получают `joinRequest`, любой отвечает `decide {requestId, approve}` → заявителю `joined {group, token}` или `joinDenied`. Заявка живёт 5 мин.
- `send {code, kind:"preset", preset}` — рассылает всем в группе `message`. Фразы: `together, coming, wait, where, ready, done` (тексты en/ru/uz приходят в `welcome.presets`).
- `clearHistory {code, before}` (клиенты чистят историю через 20 мин после намаза), `clearChat {code}` (только владелец), `leave {code}`, `updateProfile {nick, avatar?}`, `ping` (→ `pong`, раз в ~25 с).

Сервер → клиент: `welcome {userId, presets, groups[]}`, `groupCreated`, `joined`, `joinPending`, `joinRequest`, `joinSettled`, `joinDenied`, `members {code, owner, members[]}`, `message {code,id,ts,from,nick,kind,preset,text}`, `historyCleared {code,before}`, `left`, `error {code,message}`, `pong`.

`Group = {code, name, owner, members:[{userId,nick,avatar,online}], history:[message≤50]}`.
Код группы — 6 символов (`ABCDEFGHJKMNPQRSTUVWXYZ23456789`).

Правила: токен членства (`token`) выдаётся при создании/вступлении и нужен в `hello.groups` после переподключения/рестарта сервера — **сохранять**. Лимиты: 50 участников, 10 групп на пользователя, 12 сообщений за 10 с. Переподключение с backoff 1→30 с, после каждого реконнекта снова `hello` с сохранёнными группами.

## Как ведут себя десктопные клиенты
- Уведомление слева над виджетом при каждом новом сообщении (если чат не открыт), кнопки Allow/Deny на заявку.
- История группы стирается через 20 мин после каждого намаза (клиенты считают время сами) + сервер удаляет всё старше 12 ч.
- Если включено «авто»: при перерыве на намаз отправляется фраза `together`.
- Панель чата: вкладки групп, «Чат / Участники», быстрые фразы-кнопки, код группы с иконкой копирования, владелец видит кнопку «Очистить чат», окно тянется вверх за верхнюю кромку.

## Что нужно Android-приложению (идея)
Тестовый/пульт-клиент: войти в существующую группу по коду (заявку подтверждает участник на Windows/Mac) и **отправить напоминание о намазе** (preset) друзьям/коллегам; видеть участников online/offline и входящие сообщения. Реализация = WebSocket (OkHttp) + JSON; ориентир — `server/tools/test-bot.js` и `docs/protocol.md`.
Для фонового режима понадобится foreground-service или FCM (relay сейчас FCM не поддерживает — только пока приложение подключено).

## Чего знать не нужно
Вся логика prayer-times, скины, меню, MSIX/SmartScreen, CI Windows/Mac — к Android не относится.
