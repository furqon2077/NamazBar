'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { Hub } = require('../src/hub');

function client(hub, id, nick) {
  const inbox = [];
  const conn = hub.newConn(m => inbox.push(m));
  const api = {
    conn, inbox,
    send: m => hub.handle(conn, m),
    last: type => [...inbox].reverse().find(m => m.type === type),
    hello: (groups) => api.send({ type: 'hello', userId: id.padEnd(16, 'x'), nick, avatar: null, groups }),
  };
  return api;
}

test('create, join with confirmation, group chat reaches everyone', () => {
  const hub = new Hub({ secret: 's' });
  const a = client(hub, 'alice', 'Alice'); a.hello();
  a.send({ type: 'createGroup', name: 'Office' });
  const { group } = a.last('groupCreated');

  const b = client(hub, 'bob', 'Bob'); b.hello();
  b.send({ type: 'joinRequest', code: group.code });
  assert.ok(b.last('joinPending'));
  const req = a.last('joinRequest');
  assert.equal(req.from.nick, 'Bob');

  // a non-member cannot approve
  const c = client(hub, 'carol', 'Carol'); c.hello();
  c.send({ type: 'decide', requestId: req.requestId, approve: true });
  assert.equal(c.last('error').code, 'forbidden');

  a.send({ type: 'decide', requestId: req.requestId, approve: true });
  assert.ok(b.last('joined'));
  assert.equal(a.last('members').members.length, 2);

  b.send({ type: 'send', code: group.code, kind: 'preset', preset: 'together' });
  assert.equal(a.last('message').preset, 'together');
  assert.equal(b.last('message').text, "Let's do namaz together");
  assert.equal(c.last('message'), undefined);
});

test('denied request, bad preset, text disabled, rate limit', () => {
  let t = 0;
  const hub = new Hub({ secret: 's', now: () => t });
  const a = client(hub, 'alice', 'Alice'); a.hello();
  a.send({ type: 'createGroup', name: 'G' });
  const code = a.last('groupCreated').group.code;
  const b = client(hub, 'bob', 'Bob'); b.hello();
  b.send({ type: 'joinRequest', code });
  a.send({ type: 'decide', requestId: a.last('joinRequest').requestId, approve: false });
  assert.equal(b.last('joinDenied').code, code);
  b.send({ type: 'send', code, kind: 'preset', preset: 'together' });
  assert.equal(b.last('error').code, 'forbidden');

  a.send({ type: 'send', code, kind: 'preset', preset: 'nope' });
  assert.equal(a.last('error').code, 'bad_preset');
  a.send({ type: 'send', code, kind: 'text', text: 'hi' });   // свободного текста нет
  assert.equal(a.last('error').code, 'bad_kind');
  for (let i = 0; i < 15; i++) a.send({ type: 'ping' });
  assert.equal(a.last('error').code, 'rate_limited');
});

test('memberships restore from tokens after server restart; forged tokens ignored', () => {
  const hub1 = new Hub({ secret: 's' });
  const a = client(hub1, 'alice', 'Alice'); a.hello();
  a.send({ type: 'createGroup', name: 'Office' });
  const { group, token } = a.last('groupCreated');

  const hub2 = new Hub({ secret: 's' });   // "restarted"
  const a2 = client(hub2, 'alice', 'Alice');
  a2.hello([{ code: group.code, name: 'Office', token }]);
  assert.equal(a2.last('welcome').groups[0].name, 'Office');

  const m = client(hub2, 'mallory', 'Mallory');
  m.hello([{ code: group.code, name: 'x', token }]);   // alice's token, wrong user
  assert.equal(m.last('welcome').groups.length, 0);
});

test('avatar and nickname validation', () => {
  const hub = new Hub({ secret: 's' });
  const a = client(hub, 'alice', 'Alice');
  a.send({ type: 'hello', userId: 'alice'.padEnd(16, 'x'), nick: 'A', avatar: 'javascript:alert(1)' });
  assert.equal(a.last('error').code, 'bad_avatar');
  a.send({ type: 'hello', userId: 'alice'.padEnd(16, 'x'), nick: '  ', avatar: null });
  assert.equal(a.last('error').code, 'bad_nick');
});

test('leaving removes empty group', () => {
  const hub = new Hub({ secret: 's' });
  const a = client(hub, 'alice', 'Alice'); a.hello();
  a.send({ type: 'createGroup', name: 'G' });
  const code = a.last('groupCreated').group.code;
  a.send({ type: 'leave', code });
  assert.ok(a.last('left'));
  assert.equal(hub.groups.size, 0);
});

test('history: clearHistory (members only, sane time) and 12 h safety TTL', () => {
  let t = 1_000_000_000_000;
  const hub = new Hub({ secret: 's', now: () => t });
  const a = client(hub, 'alice', 'Alice'); a.hello();
  a.send({ type: 'createGroup', name: 'G' });
  const code = a.last('groupCreated').group.code;
  const b = client(hub, 'bob', 'Bob'); b.hello();

  a.send({ type: 'send', code, kind: 'preset', preset: 'together' });
  t += 60_000;
  a.send({ type: 'send', code, kind: 'preset', preset: 'ready' });
  assert.equal(hub.groups.get(code).history.length, 2);

  b.send({ type: 'clearHistory', code, before: t });                 // not a member
  assert.equal(b.last('error').code, 'forbidden');
  a.send({ type: 'clearHistory', code, before: t + 1000 });          // from the future
  assert.equal(a.last('error').code, 'bad_time');
  a.send({ type: 'clearHistory', code, before: t - 24 * 3600 * 1000 - 1 });   // too old
  assert.equal(a.last('error').code, 'bad_time');

  a.send({ type: 'clearHistory', code, before: t - 30_000 });        // wipes only the first message
  assert.equal(hub.groups.get(code).history.length, 1);
  assert.equal(a.last('historyCleared').code, code);
  a.send({ type: 'clearHistory', code, before: t });
  assert.equal(hub.groups.get(code).history.length, 0);

  a.send({ type: 'send', code, kind: 'preset', preset: 'done' });
  t += 13 * 3600 * 1000;                                             // 13 h later: TTL
  const c = client(hub, 'alice', 'Alice');
  c.hello([{ code, name: 'G', token: a.last('groupCreated').token }]);
  assert.equal(c.last('welcome').groups[0].history.length, 0);
});

test('clearChat: owner only; ownership passes on when the owner leaves', () => {
  const hub = new Hub({ secret: 's' });
  const a = client(hub, 'alice', 'Alice'); a.hello();
  a.send({ type: 'createGroup', name: 'G' });
  const created = a.last('groupCreated');
  const code = created.group.code;
  assert.equal(created.group.owner, 'alice'.padEnd(16, 'x'));
  const b = client(hub, 'bob', 'Bob'); b.hello();
  b.send({ type: 'joinRequest', code });
  a.send({ type: 'decide', requestId: a.last('joinRequest').requestId, approve: true });
  a.send({ type: 'send', code, kind: 'preset', preset: 'together' });
  assert.equal(hub.groups.get(code).history.length, 1);

  b.send({ type: 'clearChat', code });
  assert.equal(b.last('error').code, 'not_owner');
  assert.equal(hub.groups.get(code).history.length, 1);

  a.send({ type: 'clearChat', code });
  assert.equal(hub.groups.get(code).history.length, 0);
  assert.equal(b.last('historyCleared').code, code);

  a.send({ type: 'leave', code });
  assert.equal(b.last('members').owner, 'bob'.padEnd(16, 'x'));
  b.send({ type: 'clearChat', code });
  assert.equal(b.last('historyCleared').code, code);
});
