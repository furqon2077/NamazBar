# NamazBar group chat protocol (v1)

Transport: WebSocket, path `/ws`, one JSON object per text frame. Both clients (Mac/Swift, Windows/C#) implement exactly this; the server (`server/`) is the only place with logic.

## Identity
- `userId`: random 16–64 url-safe chars, generated once per install and stored locally. It is the user's identity (no accounts).
- Profile: `nick` (≤24 chars) and optional `avatar` — a `data:image/(png|jpeg|webp);base64,...` URL, ≤32 KB (clients downscale to ~96×96).
- Membership proof: when a user creates or joins a group the server returns a `token`. The client stores `{code, name, token}` and sends them in `hello`, so memberships survive server restarts and free-host sleep (the server keeps no database).

## Flow
1. Connect, send `hello`; receive `welcome` (your groups, presets).
2. **Create** a group (`createGroup`) → share its 6-char `code` out of band, or
3. **Join**: `joinRequest{code}` → every online member gets `joinRequest`; any one of them answers `decide{requestId, approve}`; the requester gets `joined` (with token) or `joinDenied`. Requests expire after 5 min.
4. **Chat**: `send` a preset (or text, if the server enables it). Everyone in the group receives `message`.

## Client → server
| type | fields |
|---|---|
| `hello` | `userId, nick, avatar?, groups?: [{code,name,token}]` — must be first |
| `updateProfile` | `nick, avatar?` |
| `createGroup` | `name` |
| `joinRequest` | `code` |
| `decide` | `requestId, approve: bool` |
| `send` | `code, kind: "preset", preset` or `kind: "text", text` |
| `leave` | `code` |
| `ping` | — (reply `pong`; use as keep-alive, ~every 25 s) |

## Server → client
| type | fields |
|---|---|
| `welcome` | `userId, presets{id:{en,ru,uz}}, allowText, groups:[Group]` |
| `groupCreated` | `group, token` |
| `joinPending` | `code, name` (to the requester) |
| `joinRequest` | `requestId, code, from:{userId,nick,avatar}` (to members) |
| `joinSettled` | `requestId, approved, by` (to members: hide the prompt) |
| `joined` | `group, token` |
| `joinDenied` | `code, reason` |
| `members` | `code, members:[Member]` (online flags changed, someone joined/left) |
| `message` | `code, id, ts, from, nick, kind, preset?, text` |
| `left` | `code` |
| `error` | `code, message` |
| `pong` | — |

`Group = {code, name, members:[Member], history:[message]}` (last 50 messages, memory only). `Member = {userId, nick, avatar, online}`.

Preset ids: `together, coming, wait, where, ready, done`. Clients show their own localized text by id; `message.text` is the English fallback.

## Error codes
`bad_request, no_hello, unknown_type, bad_user, bad_nick, bad_avatar, bad_name, no_group, already_member, group_full, too_many_groups, nobody_online, already_pending, busy, no_request, forbidden, bad_preset, bad_kind, bad_text, text_disabled, rate_limited, internal`.

## Client behaviour
- Reconnect with exponential backoff (1 s → 30 s cap); free hosts sleep when idle and need up to ~60 s to wake. Re-send `hello` with saved groups after every reconnect.
- Limits: 50 members/group, 10 groups/user, 12 messages per 10 s per connection.
- Auto-suggest: when the PC locks near prayer time, offer/send the `together` preset to the chosen group.
