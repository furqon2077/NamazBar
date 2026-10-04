'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { spawn } = require('node:child_process');
const WebSocket = require('ws');

test('server end-to-end over WebSocket', async (t) => {
  const port = 18080 + Math.floor(Math.random() * 500);
  const proc = spawn(process.execPath, ['src/index.js'], { env: { ...process.env, PORT: port, TOKEN_SECRET: 'x' }, stdio: 'pipe' });
  t.after(() => proc.kill());
  await new Promise(r => proc.stdout.on('data', d => /listening/.test(d) && r()));

  const health = await fetch(`http://127.0.0.1:${port}/healthz`);
  assert.equal(await health.text(), 'ok');

  const open = (id, nick) => new Promise(res => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
    const q = [], waiters = [];
    ws.on('message', d => { const m = JSON.parse(d); const w = waiters.findIndex(x => x.type === m.type);
      if (w >= 0) waiters.splice(w, 1)[0].res(m); else q.push(m); });
    const api = { ws, send: m => ws.send(JSON.stringify(m)),
      next: type => { const i = q.findIndex(m => m.type === type); return i >= 0 ? Promise.resolve(q.splice(i, 1)[0])
        : new Promise(res => waiters.push({ type, res })); } };
    ws.on('open', () => { api.send({ type: 'hello', userId: id.padEnd(16, 'x'), nick, avatar: null }); res(api); });
  });

  const a = await open('alice', 'Alice'); await a.next('welcome');
  const b = await open('bob', 'Bob'); await b.next('welcome');
  a.send({ type: 'createGroup', name: 'Office' });
  const { group } = await a.next('groupCreated');
  b.send({ type: 'joinRequest', code: group.code });
  const req = await a.next('joinRequest');
  a.send({ type: 'decide', requestId: req.requestId, approve: true });
  await b.next('joined');
  b.send({ type: 'send', code: group.code, kind: 'preset', preset: 'coming' });
  assert.equal((await a.next('message')).preset, 'coming');
  a.ws.close(); b.ws.close();
});
