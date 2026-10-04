'use strict';

const http = require('http');
const crypto = require('crypto');
const { WebSocketServer } = require('ws');
const { Hub } = require('./hub');

const PORT = Number(process.env.PORT) || 8080;   // free hosts inject PORT
let secret = process.env.TOKEN_SECRET;
if (!secret) {
  secret = crypto.randomBytes(32).toString('hex');
  console.warn('TOKEN_SECRET not set: using a random one, memberships will not survive a restart.');
}
const hub = new Hub({ secret, allowText: process.env.ALLOW_TEXT === '1' });

const server = http.createServer((req, res) => {
  if (req.url === '/healthz') { res.writeHead(200, { 'content-type': 'text/plain' }); return res.end('ok'); }
  res.writeHead(404); res.end();
});

const wss = new WebSocketServer({ server, path: '/ws', maxPayload: 64 * 1024 });
wss.on('connection', (ws) => {
  const conn = hub.newConn(obj => { if (ws.readyState === 1) ws.send(JSON.stringify(obj)); });
  ws.isAlive = true;
  ws.on('pong', () => { ws.isAlive = true; });
  ws.on('message', (data) => {
    let msg; try { msg = JSON.parse(data.toString()); } catch { msg = null; }
    hub.handle(conn, msg);
  });
  ws.on('close', () => hub.close(conn));
  ws.on('error', () => ws.terminate());
});

// Drop dead sockets; the traffic also keeps free hosts from idling the connection.
setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) { ws.terminate(); continue; }
    ws.isAlive = false; ws.ping();
  }
}, 30000).unref();

server.listen(PORT, () => console.log(`namazbar-relay listening on :${PORT}`));
