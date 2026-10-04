'use strict';

const crypto = require('crypto');
const { PRESETS } = require('./presets');

const LIMITS = {
  nick: 24,
  groupName: 40,
  avatarBytes: 32 * 1024,   // data URL length cap (clients send ~96x96)
  text: 500,
  members: 50,
  groupsPerUser: 10,
  history: 50,
  pendingPerGroup: 20,
  pendingTtlMs: 5 * 60 * 1000,
  rate: { count: 12, perMs: 10 * 1000 },
};

const CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'; // no I, L, O, 0, 1
const AVATAR_RE = /^data:image\/(png|jpeg|webp);base64,[A-Za-z0-9+/=]+$/;

class ProtocolError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

const clean = (s, max) => String(s ?? '').replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, max);

class Hub {
  /**
   * @param {object} opts
   * @param {string} opts.secret      HMAC key for membership tokens (stateless restore after restart)
   * @param {boolean} [opts.allowText] allow free text besides presets
   * @param {() => number} [opts.now]
   */
  constructor({ secret, allowText = false, now = Date.now } = {}) {
    if (!secret) throw new Error('secret required');
    this.secret = secret;
    this.allowText = allowText;
    this.now = now;
    this.groups = new Map();   // code -> {code,name,members:Map<userId,profile>,history:[]}
    this.online = new Map();   // userId -> Set<conn>
    this.pending = new Map();  // requestId -> {code, userId, conn, timer}
  }

  // ---- tokens -------------------------------------------------------------
  token(code, userId) {
    return crypto.createHmac('sha256', this.secret).update(`${code}|${userId}`).digest('base64url');
  }
  validToken(code, userId, token) {
    const want = Buffer.from(this.token(code, userId));
    const got = Buffer.from(String(token ?? ''));
    return want.length === got.length && crypto.timingSafeEqual(want, got);
  }

  // ---- connection lifecycle ----------------------------------------------
  /** conn: { send(obj), userId?, groups:Set, hits:[] } */
  newConn(send) { return { send, userId: null, profile: null, groups: new Set(), hits: [] }; }

  handle(conn, msg) {
    try {
      if (!msg || typeof msg !== 'object' || typeof msg.type !== 'string') throw new ProtocolError('bad_request', 'invalid message');
      this.rateLimit(conn);
      if (msg.type === 'ping') return conn.send({ type: 'pong' });
      if (msg.type === 'hello') return this.hello(conn, msg);
      if (!conn.userId) throw new ProtocolError('no_hello', 'send hello first');
      const fn = { updateProfile: 'updateProfile', createGroup: 'createGroup', joinRequest: 'joinRequest',
                   decide: 'decide', send: 'sendMessage', leave: 'leave' }[msg.type];
      if (!fn) throw new ProtocolError('unknown_type', `unknown type ${clean(msg.type, 32)}`);
      this[fn](conn, msg);
    } catch (e) {
      if (!(e instanceof ProtocolError)) { console.error(e); e = new ProtocolError('internal', 'internal error'); }
      conn.send({ type: 'error', code: e.code, message: e.message });
    }
  }

  rateLimit(conn) {
    const t = this.now();
    conn.hits = conn.hits.filter(h => t - h < LIMITS.rate.perMs);
    if (conn.hits.length >= LIMITS.rate.count) throw new ProtocolError('rate_limited', 'too many messages, slow down');
    conn.hits.push(t);
  }

  close(conn) {
    if (!conn.userId) return;
    const set = this.online.get(conn.userId);
    set?.delete(conn);
    for (const [id, p] of this.pending) if (p.conn === conn) { clearTimeout(p.timer); this.pending.delete(id); }
    if (set && set.size === 0) {
      this.online.delete(conn.userId);
      for (const code of conn.groups) this.broadcastMembers(code);
    }
  }

  // ---- handlers -----------------------------------------------------------
  profileOf(msg) {
    const userId = clean(msg.userId, 64);
    if (!/^[A-Za-z0-9_-]{16,64}$/.test(userId)) throw new ProtocolError('bad_user', 'userId must be 16-64 url-safe chars');
    const nick = clean(msg.nick, LIMITS.nick);
    if (!nick) throw new ProtocolError('bad_nick', 'nickname required');
    let avatar = msg.avatar ?? null;
    if (avatar !== null && (typeof avatar !== 'string' || avatar.length > LIMITS.avatarBytes || !AVATAR_RE.test(avatar)))
      throw new ProtocolError('bad_avatar', 'avatar must be a small png/jpeg/webp data URL');
    return { userId, nick, avatar };
  }

  hello(conn, msg) {
    if (conn.userId) throw new ProtocolError('already_hello', 'hello already sent');
    const p = this.profileOf(msg);
    conn.userId = p.userId; conn.profile = p;
    if (!this.online.has(p.userId)) this.online.set(p.userId, new Set());
    this.online.get(p.userId).add(conn);

    // Restore memberships proven by token (survives server restarts).
    const restored = [];
    for (const g of Array.isArray(msg.groups) ? msg.groups.slice(0, LIMITS.groupsPerUser) : []) {
      const code = clean(g?.code, 12).toUpperCase();
      if (!this.validToken(code, p.userId, g?.token)) continue;
      let group = this.groups.get(code);
      if (!group) { group = this.makeGroup(code, clean(g.name, LIMITS.groupName) || code); }
      if (!group.members.has(p.userId) && group.members.size >= LIMITS.members) continue;
      group.members.set(p.userId, p);
      conn.groups.add(code);
      restored.push(code);
    }
    for (const code of this.userGroups(p.userId)) conn.groups.add(code);
    conn.send({ type: 'welcome', userId: p.userId, presets: PRESETS, allowText: this.allowText,
                groups: [...conn.groups].map(c => this.snapshot(this.groups.get(c))) });
    for (const code of conn.groups) this.broadcastMembers(code, conn);
  }

  updateProfile(conn, msg) {
    const p = this.profileOf({ ...msg, userId: conn.userId });
    conn.profile = p;
    for (const code of this.userGroups(conn.userId)) {
      this.groups.get(code).members.set(conn.userId, p);
      this.broadcastMembers(code);
    }
  }

  createGroup(conn, msg) {
    if (this.userGroups(conn.userId).length >= LIMITS.groupsPerUser) throw new ProtocolError('too_many_groups', 'group limit reached');
    const name = clean(msg.name, LIMITS.groupName);
    if (!name) throw new ProtocolError('bad_name', 'group name required');
    let code; do { code = this.randomCode(); } while (this.groups.has(code));
    const group = this.makeGroup(code, name);
    this.addMember(group, conn);
    conn.send({ type: 'groupCreated', group: this.snapshot(group), token: this.token(code, conn.userId) });
  }

  joinRequest(conn, msg) {
    const code = clean(msg.code, 12).toUpperCase();
    const group = this.groups.get(code);
    if (!group) throw new ProtocolError('no_group', 'group not found (or nobody from it is online)');
    if (group.members.has(conn.userId)) throw new ProtocolError('already_member', 'already in this group');
    if (group.members.size >= LIMITS.members) throw new ProtocolError('group_full', 'group is full');
    if (this.userGroups(conn.userId).length >= LIMITS.groupsPerUser) throw new ProtocolError('too_many_groups', 'group limit reached');
    const voters = [...group.members.keys()].flatMap(id => [...(this.online.get(id) ?? [])]);
    if (voters.length === 0) throw new ProtocolError('nobody_online', 'nobody from this group is online to confirm');
    const open = [...this.pending.values()].filter(p => p.code === code);
    if (open.some(p => p.userId === conn.userId)) throw new ProtocolError('already_pending', 'request already sent');
    if (open.length >= LIMITS.pendingPerGroup) throw new ProtocolError('busy', 'too many pending requests');

    const requestId = crypto.randomBytes(8).toString('hex');
    const timer = setTimeout(() => this.resolve(requestId, false, 'expired'), LIMITS.pendingTtlMs);
    timer.unref?.();
    this.pending.set(requestId, { code, userId: conn.userId, conn, timer });
    conn.send({ type: 'joinPending', code, name: group.name });
    for (const v of voters) v.send({ type: 'joinRequest', requestId, code, from: this.public(conn.profile) });
  }

  decide(conn, msg) {
    const p = this.pending.get(String(msg.requestId));
    if (!p) throw new ProtocolError('no_request', 'request not found or already decided');
    const group = this.groups.get(p.code);
    if (!group?.members.has(conn.userId)) throw new ProtocolError('forbidden', 'not a member of that group');
    this.resolve(String(msg.requestId), msg.approve === true, 'decided', conn.userId);
  }

  resolve(requestId, approve, why, byUserId) {
    const p = this.pending.get(requestId);
    if (!p) return;
    clearTimeout(p.timer);
    this.pending.delete(requestId);
    const group = this.groups.get(p.code);
    if (approve && group) {
      this.addMember(group, p.conn);
      p.conn.send({ type: 'joined', group: this.snapshot(group), token: this.token(p.code, p.userId) });
    } else {
      p.conn.send({ type: 'joinDenied', code: p.code, reason: why });
    }
    // tell other voters the request is settled
    if (group) for (const id of group.members.keys()) for (const c of this.online.get(id) ?? [])
      c.send({ type: 'joinSettled', requestId, approved: approve && !!group, by: byUserId ?? null });
  }

  sendMessage(conn, msg) {
    const code = clean(msg.code, 12).toUpperCase();
    const group = this.groups.get(code);
    if (!group?.members.has(conn.userId)) throw new ProtocolError('forbidden', 'not a member of that group');
    const out = { type: 'message', code, id: crypto.randomBytes(6).toString('hex'), ts: this.now(),
                  from: conn.userId, nick: group.members.get(conn.userId).nick };
    if (msg.kind === 'preset') {
      if (!Object.hasOwn(PRESETS, msg.preset)) throw new ProtocolError('bad_preset', 'unknown preset');
      Object.assign(out, { kind: 'preset', preset: msg.preset, text: PRESETS[msg.preset].en });
    } else if (msg.kind === 'text') {
      if (!this.allowText) throw new ProtocolError('text_disabled', 'free text is disabled on this server');
      const text = clean(msg.text, LIMITS.text);
      if (!text) throw new ProtocolError('bad_text', 'empty message');
      Object.assign(out, { kind: 'text', text });
    } else throw new ProtocolError('bad_kind', 'kind must be preset or text');
    group.history.push(out);
    if (group.history.length > LIMITS.history) group.history.shift();
    this.broadcast(group, out);
  }

  leave(conn, msg) {
    const code = clean(msg.code, 12).toUpperCase();
    const group = this.groups.get(code);
    if (!group?.members.delete(conn.userId)) throw new ProtocolError('forbidden', 'not a member of that group');
    for (const c of this.online.get(conn.userId) ?? []) { c.groups.delete(code); c.send({ type: 'left', code }); }
    if (group.members.size === 0) this.groups.delete(code); else this.broadcastMembers(code);
  }

  // ---- helpers ------------------------------------------------------------
  makeGroup(code, name) {
    const g = { code, name, members: new Map(), history: [] };
    this.groups.set(code, g);
    return g;
  }
  addMember(group, conn) {
    group.members.set(conn.userId, conn.profile);
    for (const c of this.online.get(conn.userId) ?? []) c.groups.add(group.code);
    this.broadcastMembers(group.code);
  }
  userGroups(userId) { return [...this.groups.values()].filter(g => g.members.has(userId)).map(g => g.code); }
  randomCode() {
    const b = crypto.randomBytes(6);
    return [...b].map(x => CODE_ALPHABET[x % CODE_ALPHABET.length]).join('');
  }
  public(p) { return { userId: p.userId, nick: p.nick, avatar: p.avatar }; }
  snapshot(g) {
    return { code: g.code, name: g.name, members: this.memberList(g), history: g.history };
  }
  memberList(g) {
    return [...g.members.values()].map(p => ({ ...this.public(p), online: this.online.has(p.userId) }));
  }
  broadcast(group, obj) {
    for (const id of group.members.keys()) for (const c of this.online.get(id) ?? []) c.send(obj);
  }
  broadcastMembers(code) {
    const g = this.groups.get(code);
    if (g) this.broadcast(g, { type: 'members', code, members: this.memberList(g) });
  }
}

module.exports = { Hub, LIMITS };
