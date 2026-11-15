import { API, PANEL_HEADERS, api, h, haptic, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// Discord: the voice channel you are in on the PC, who is talking (a green ring), who is muted or deafened, and
// mute / deafen / leave buttons. The agent talks to the Discord app over its local RPC socket (Settings › General ›
// Discord); avatars come through the agent.
const STATUS_TEXT = {
  off: 'Turn on Discord in Settings › General on the PC',
  setup: 'Add your Discord application in Settings › General › Discord',
  'not-running': 'Discord is not running on the PC',
  connecting: 'Connecting to Discord…',
  authorize: 'Press Connect in Settings › General › Discord',
};

const svg = (body) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;
const SLASH = '<path d="M3 3l18 18"/>';
const MIC = '<rect x="9" y="2" width="6" height="12" rx="3"/><path d="M5 10v1a7 7 0 0 0 14 0v-1M12 18v4"/>';
const PHONES = '<path d="M3 18v-6a9 9 0 0 1 18 0v6"/><path d="M21 19a2 2 0 0 1-2 2h-1v-6h3zM3 19a2 2 0 0 0 2 2h1v-6H3z"/>';
const ICONS = {
  mic: svg(MIC), micOff: svg(MIC + SLASH), phones: svg(PHONES), phonesOff: svg(PHONES + SLASH),
  leave: svg('<path d="M3 15c5-5 13-5 18 0l-2 3-4-1v-3a10 10 0 0 0-6 0v3l-4 1z"/>'),
};
const icon = (name, cls = 'dc-ico') => { const e = h('span', cls); e.innerHTML = ICONS[name]; return e; }; // static markup only

W.discord = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, busy = null, lastKey = '';
    const head = h('div', 'w-title dc-head', h('span', 'dc-chan', o.title || 'Discord'), h('span', 'dc-guild'));
    const list = h('div', 'dc-list');
    const bar = h('div', 'dc-bar');
    box.classList.add('dc');
    box.append(head, list, bar);
    const load = async () => {
      try { data = await api('/discord'); return true; } catch (_) { return false; }
    };
    const act = async (action) => {
      haptic();
      busy = action; render(true);
      try {
        const r = await fetch(`${API}/discord/${action}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) toast(j.error || 'Discord did not answer', 'err');
      } catch (_) { toast('The PC did not answer', 'err'); }
      busy = null;
      await load(); render(true);
    };
    const btn = (action, ico, label, on) => {
      const b = h('button', 'dc-btn' + (on ? ' on' : '') + (busy === action ? ' busy' : ''), icon(ico), h('span', 'dc-lbl', label));
      b.addEventListener('click', () => act(action));
      return b;
    };
    const avatar = (m) => {
      const a = h('div', 'dc-av');
      if (m.avatar) {
        const img = h('img');
        img.alt = '';
        img.src = `${API}/discord-avatar?u=${encodeURIComponent(m.id)}&h=${encodeURIComponent(m.avatar)}`;
        img.onerror = () => img.remove();
        a.append(img);
      }
      a.dataset.i = (m.name || '?').trim().charAt(0).toUpperCase();
      return a;
    };
    const render = (force = false) => {
      const s = data?.snapshot;
      const key = JSON.stringify([s?.status, s?.message, s?.channel, s?.members, s?.selfMute, s?.selfDeaf, busy, data?.control, box.clientHeight, box.clientWidth]);
      if (!force && key === lastKey) return;
      lastKey = key;
      const small = box.clientHeight < 150;
      box.classList.toggle('small', small);
      if (!s) { list.replaceChildren(h('div', 'empty', 'Loading…')); bar.replaceChildren(); return; }
      if (s.status !== 'ready') {
        head.querySelector('.dc-chan').textContent = o.title || 'Discord';
        head.querySelector('.dc-guild').textContent = '';
        list.replaceChildren(h('div', 'empty', s.status === 'error' ? '⚠ ' + (s.message || 'Discord error') : (STATUS_TEXT[s.status] || s.message || '')));
        bar.replaceChildren();
        return;
      }
      if (!s.channel) {
        head.querySelector('.dc-chan').textContent = o.title || 'Discord';
        head.querySelector('.dc-guild').textContent = s.user ? ' ' + s.user : '';
        list.replaceChildren(h('div', 'empty', 'Not in a voice channel'));
      } else {
        head.querySelector('.dc-chan').textContent = '🔊 ' + s.channel.name;
        head.querySelector('.dc-guild').textContent = s.channel.guild ? ' ' + s.channel.guild : '';
        const members = s.members || [];
        list.classList.toggle('grid', !small && members.length > 4 && box.clientWidth > 260);
        list.replaceChildren(...members.map((m) => {
          const deaf = m.deaf || m.selfDeaf, mute = m.mute || m.selfMute;
          return h('div', 'dc-row' + (m.speaking ? ' talk' : '') + (deaf || mute ? ' quiet' : ''),
            avatar(m),
            h('span', 'dc-name', m.name, m.bot ? h('small', 'dc-bot', 'BOT') : ''),
            h('span', 'dc-flags', ...(mute ? [icon('micOff', 'dc-flag')] : []), ...(deaf ? [icon('phonesOff', 'dc-flag')] : [])));
        }));
        if (!members.length) list.replaceChildren(h('div', 'empty', 'Nobody here yet'));
      }
      if (data.control === false) { bar.replaceChildren(); return; }
      bar.replaceChildren(
        btn('mute', s.selfMute ? 'micOff' : 'mic', s.selfMute ? 'Unmute' : 'Mute', s.selfMute),
        btn('deafen', s.selfDeaf ? 'phonesOff' : 'phones', s.selfDeaf ? 'Undeafen' : 'Deafen', s.selfDeaf),
        ...(s.channel ? [btn('leave', 'leave', 'Leave', false)] : []));
      bar.lastElementChild?.classList.toggle('danger', !!s.channel);
    };
    const tick = every(+o.interval > 0 ? +o.interval * 1000 : 1000, async () => { const ok = await load(); render(); return ok; }, 5000);
    return { update: () => { tick(); render(); } };
  },
};
