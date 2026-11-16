import { API, PANEL_HEADERS, api, h, haptic, t, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// Upstream's Discord widget (#112) as type "discord-hub", beside the fork's own Discord widget: classes are dh-* (the
// fork's widget uses dc-*), data comes from /discord/hub and images through /discord-avatar?k=… (already allowed by
// the phone app).
// Discord through the desktop app on the PC: the voice channel / call you are in, who is there, mute / deafen / leave,
// voice channels to join and recent notifications. Buttons only work when the widget allows control.
W['discord-hub'] = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, lastKey = '', busy = null;
    const open = new Set(); // servers whose channels are unfolded
    // a round logo: the image once it has loaded, the first letter until then (and when there is no image)
    const ico = (src, name) => {
      const w = h('span', 'dh-ico', (name || '?').trim().charAt(0).toUpperCase() || '?');
      if (src) { const im = new Image(); im.alt = ''; im.onload = () => w.replaceChildren(im); im.src = src; }
      return w;
    };
    const load = async () => { try { data = await api('/discord/hub'); } catch (_) {} };
    const img = (kind, id, hash) => `${API}/discord-avatar?k=${kind}&u=${encodeURIComponent(id)}&h=${encodeURIComponent(hash)}`;
    const act = async (action, channel) => {
      if (busy) return; // one press at a time: a double tap must not toggle twice
      haptic();
      busy = action + (channel || ''); render(true);
      try {
        const r = await fetch(`${API}/discord/hub/${action}` + (channel ? '?channel=' + encodeURIComponent(channel) : ''), { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || t('w.discord.failed'), 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      await new Promise((done) => setTimeout(done, 600));
      await load(); busy = null; render(true);
    };
    const render = (force = false) => {
      const key = JSON.stringify([data, busy, [...open], box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'w-title', 'Discord'), h('div', 'empty', t('w.discord.turnOn'))); return; }
      const s = data.snapshot;
      if (!s.connected) { box.replaceChildren(h('div', 'w-title', 'Discord'), h('div', 'empty', s.error || t('panel.connecting'))); return; }
      const ctl = data.control;
      const btn = (cls, label, on, action) => {
        const b = h('button', 'dh-btn ' + cls + (on ? ' on' : '') + (busy === action ? ' busy' : ''), label);
        if (ctl && !busy) b.addEventListener('click', () => act(action)); else b.disabled = true;
        return b;
      };
      const kids = [h('div', 'dh-top',
        h('span', 'dh-where', s.channelName ? `${s.channelName}` : t('w.discord.notInChannel')),
        s.channelKind ? h('span', 'dh-kind', s.channelKind) : null,
        btn('mute', s.muted ? '🔇' : '🎙️', s.muted, 'mute'), btn('deaf', s.deafened ? '🔕' : '🎧', s.deafened, 'deafen'),
        s.channelId ? btn('leave', '📞', false, 'leave') : null)];
      if (s.channelId && s.members.length) {
        kids.push(h('div', 'dh-members', ...s.members.map((m) => h('span', 'dh-member' + (m.deafened ? ' deaf' : m.muted ? ' muted' : ''), ico(m.avatar && img('user', m.id, m.avatar), m.name), (m.deafened ? '🔕 ' : m.muted ? '🔇 ' : '') + m.name))));
      }
      if (o.channels !== false && s.channels.length) {
        // one block per server; tap it to unfold its voice channels (the server you are in starts unfolded)
        // grouped by server id, not by name: two servers can have the same name
        const servers = new Map();
        for (const c of s.channels) {
          const gid = c.guildId || c.guild || 'other';
          if (!servers.has(gid)) servers.set(gid, []);
          servers.get(gid).push(c);
        }
        kids.push(h('div', 'dh-servers', ...[...servers].map(([gid, chans]) => {
          const here = chans.some((c) => c.id === s.channelId);
          const isOpen = open.has(gid) || (here && !open.has('!' + gid));
          const g = chans[0];
          const name = g.guild || t('w.discord.otherServer');
          const head = h('button', 'dh-server' + (isOpen ? ' open' : '') + (here ? ' here' : ''), ico(g.guildIcon && g.guildId && img('guild', g.guildId, g.guildIcon), name),
            h('span', 'dh-stext', h('b', null, name), h('small', null, t(chans.length === 1 ? 'w.discord.channel' : 'w.discord.channels', { n: chans.length }))));
          head.addEventListener('click', () => {
            haptic();
            if (isOpen) { open.delete(gid); if (here) open.add('!' + gid); } else { open.add(gid); open.delete('!' + gid); }
            render(true);
          });
          const block = h('div', 'dh-block' + (isOpen ? ' open' : ''), head);
          if (isOpen) {
            block.append(h('div', 'dh-chans', ...chans.map((c) => {
              const b = h('button', 'dh-chan' + (c.id === s.channelId ? ' cur' : '') + (busy === 'join' + c.id ? ' busy' : ''), '🔊 ' + c.name);
              if (ctl && !busy && c.id !== s.channelId) b.addEventListener('click', () => act('join', c.id));
              return b;
            })));
          }
          return block;
        })));
      }
      if (o.notifications !== false && s.notifications.length) {
        kids.push(h('div', 'dh-notes', ...s.notifications.slice(0, 6).map((n) => h('div', 'dh-note', h('b', null, n.title), h('span', null, n.body)))));
      }
      box.replaceChildren(...kids);
    };
    const tick = every(2000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};
