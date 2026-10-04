# NamazBar relay server

Tiny Node.js WebSocket relay for NamazBar group chat. In-memory only (no database), one dependency (`ws`). Protocol: [`../docs/protocol.md`](../docs/protocol.md).

```
npm ci
npm test
TOKEN_SECRET=change-me npm start      # ws://localhost:8080/ws, health: /healthz
```

Env: `PORT` (injected by hosts), `TOKEN_SECRET` (keep it stable — it signs membership tokens, so groups survive restarts), `ALLOW_TEXT=1` to allow free text besides presets.

## Free hosting
- **Render** (included): `render.yaml` at the repo root; New → Blueprint. Free web services support WebSockets but sleep after ~15 min idle; the first connect after sleep takes up to a minute, and clients reconnect automatically.
- **Fly.io / Koyeb / Railway free tiers** work too: `npm ci --omit=dev && node src/index.js`, set `TOKEN_SECRET`.
- Because state is memory-only, a restart drops live groups until members reconnect; clients restore them from their saved tokens.
