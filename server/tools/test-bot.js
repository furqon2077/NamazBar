'use strict';
// Test bot: joins a group as "Test Bot" and sends preset messages, so you can see how
// notifications look on your PC.
//   node tools/test-bot.js <server-url> <GROUP-CODE> [preset] [count] [delaySeconds]
//   e.g. node tools/test-bot.js wss://namazbar-relay.onrender.com/ws ABC123 together 3 5
// The first run asks the group to confirm "Test Bot" (approve it in NamazBar). The bot keeps
// its identity in tools/.bot.json, so later runs just send.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const WebSocket = require('ws');

const [url, codeArg, preset = 'together', count = '1', delay = '3'] = process.argv.slice(2);
if (!url || !codeArg) { console.error('usage: node tools/test-bot.js <ws-url> <CODE> [preset] [count] [delaySeconds]'); process.exit(1); }
const code = codeArg.toUpperCase();
const file = path.join(__dirname, '.bot.json');
let id = {}; try { id = JSON.parse(fs.readFileSync(file, 'utf8')); } catch {}
if (!id.userId) id = { userId: crypto.randomBytes(12).toString('hex'), tokens: {} };
const save = () => fs.writeFileSync(file, JSON.stringify(id));

const ws = new WebSocket(/\/ws$/.test(url) ? url : url.replace(/\/?$/, '/ws'));
const send = o => ws.send(JSON.stringify(o));
let sent = 0;

function burst() {
  const t = setInterval(() => {
    if (sent >= Number(count)) { clearInterval(t); setTimeout(() => ws.close(), 1000); return; }
    send({ type: 'send', code, kind: 'preset', preset }); sent++; console.log(`sent "${preset}" (${sent}/${count})`);
  }, Math.max(1, Number(delay)) * 1000);
}

ws.on('open', () => send({ type: 'hello', userId: id.userId, nick: 'Test Bot', avatar: null,
  groups: Object.entries(id.tokens).map(([c, token]) => ({ code: c, name: c, token })) }));
ws.on('message', raw => {
  const m = JSON.parse(raw);
  if (m.type === 'welcome') {
    if (m.groups.some(g => g.code === code)) { console.log('already a member'); burst(); }
    else { console.log('asking to join ' + code + ' - approve "Test Bot" in NamazBar (5 min)'); send({ type: 'joinRequest', code }); }
  } else if (m.type === 'joined') { id.tokens[code] = m.token; save(); console.log('approved'); burst(); }
  else if (m.type === 'joinDenied') { console.error('denied: ' + m.reason); process.exit(2); }
  else if (m.type === 'error') { console.error('error: ' + m.code + ' - ' + m.message); }
});
ws.on('error', e => { console.error('connection failed: ' + e.message); process.exit(3); });
ws.on('close', () => process.exit(0));
