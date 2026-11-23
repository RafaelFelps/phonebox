import { API, PANEL_HEADERS, api, fmtDur, h, haptic, shortDur, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// Steam: the Steam client on the PC (games, downloads, play time) and, with a Web API key in
// Settings › Steam, friends and achievements. Three widgets share one reading of /api/steam.
const STATES = ['Offline', 'Online', 'Busy', 'Away', 'Snooze', 'Looking to trade', 'Looking to play'];
const STATUS_TEXT = {
  off: 'Add a Steam widget to a page',
  'not-installed': 'Steam is not installed on the PC',
};

// one request for every Steam widget on screen
let shared = null, sharedAt = 0, inflight = null;
async function steamData(maxAge = 2500) {
  if (shared && Date.now() - sharedAt < maxAge) return shared;
  inflight ??= api('/my-steam').then((d) => { shared = d; sharedAt = Date.now(); return d; }).finally(() => { inflight = null; });
  return inflight;
}

const cover = (app, kind = 'portrait') => `${API}/my-steam/cover?app=${app}&kind=${kind}`;
const proxied = (url) => (url ? `${API}/my-steam/img?u=${encodeURIComponent(url)}` : '');
const img = (src, cls, alt = '') => {
  const i = h('img', cls);
  i.alt = alt; i.loading = 'lazy'; i.decoding = 'async';
  if (src) i.src = src; else i.classList.add('broken');
  i.onerror = () => i.classList.add('broken');
  return i;
};
const bytes = (n) => (n >= 1e9 ? (n / 1e9).toFixed(1) + ' GB' : n >= 1e6 ? Math.round(n / 1e6) + ' MB' : Math.round((n || 0) / 1e3) + ' KB');
const hours = (min) => (min >= 60 ? `${(min / 60).toFixed(min >= 600 ? 0 : 1)} h` : `${min} min`);

async function post(path, okText) {
  haptic();
  try {
    const r = await fetch(`${API}/my-steam/${path}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) { toast(j.error || 'Steam did not answer', 'err'); return false; }
    if (okText) toast(okText, 'ok');
    return true;
  } catch (_) { toast('The PC did not answer', 'err'); return false; }
}

function message(box, s, needKey = false) {
  if (!s) return 'Loading…';
  if (s.status !== 'ok') return STATUS_TEXT[s.status] || s.status;
  if (needKey && !s.hasKey) return 'Add your Steam Web API key in Settings › Steam on the PC';
  if (needKey && s.error) return '⚠ ' + s.error;
  return null;
}

const lastPct = new Map();
const PHASES = { downloading: 'Downloading', installing: 'Installing', verifying: 'Verifying files', preparing: 'Preparing', paused: 'Paused', queued: 'Queued' };

function downloadRow(d) {
  const eta = d.bytesPerSec > 0 && d.total > d.done ? ` · ${shortDur(((d.total - d.done) / d.bytesPerSec) * 1000)} left` : '';
  const speed = d.bytesPerSec > 0 ? ` · ${bytes(d.bytesPerSec)}/s` : '';
  const phase = PHASES[d.phase] || 'Downloading';
  // Steam gives no byte count while it verifies or preallocates: a moving stripe instead of a stuck 0 %
  const busy = !d.total && (d.phase === 'verifying' || d.phase === 'preparing');
  const bar = h('div', 'mst-bar' + (busy ? ' busy' : ''), h('i'));
  // each poll builds a new bar: it starts where the last one was and slides to the new value
  if (!busy) {
    const i = bar.firstChild, from = lastPct.get(d.appId);
    i.style.width = `${from ?? d.progress}%`;
    if (from != null && from !== d.progress) requestAnimationFrame(() => requestAnimationFrame(() => { i.style.width = `${d.progress}%`; }));
    lastPct.set(d.appId, d.progress);
  }
  return h('div', 'mst-dl', img(cover(d.appId, 'header'), 'mst-dl-img'),
    h('div', 'mst-dl-main', h('div', 'mst-dl-name', d.name), bar,
      h('div', 'mst-dl-meta', (busy ? phase : `${phase} ${Math.round(d.progress)}%`) + (d.total ? ` · ${bytes(d.done)} of ${bytes(d.total)}` : '') + speed + eta)));
}

/** Asks Steam on the PC to update the game now (it verifies the files first, then downloads the new build). */
async function startUpdate(appId, name) {
  if (await post(`update/${appId}`, `Steam is checking ${name} and will update it`)) setTimeout(() => { sharedAt = 0; }, 1500);
}

/** A game with an update Steam has not started yet. */
function updateRow(u, control) {
  const main = h('div', 'mst-dl-main', h('div', 'mst-dl-name', u.name), h('div', 'mst-dl-meta', 'Update available'));
  const row = h('div', 'mst-dl mst-upd', img(cover(u.appId, 'header'), 'mst-dl-img'), main);
  if (control) {
    const b = h('button', 'mst-upd-btn', 'Update');
    b.addEventListener('click', (e) => { e.stopPropagation(); b.disabled = true; startUpdate(u.appId, u.name).finally(() => { b.disabled = false; }); });
    row.append(b);
  }
  return row;
}

// ================================================================ Now playing
W['my-steam'] = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, key = '';
    const control = o.control !== false;
    box.classList.add('st');
    const render = () => {
      const s = data;
      const k = JSON.stringify([s?.status, s?.user, s?.state, s?.playing, s?.achievements, s?.downloads, s?.updates, s?.error, s?.hasKey, box.clientWidth, box.clientHeight, Math.floor(Date.now() / 30000)]);
      if (k === key) return;
      key = k;
      const msg = message(box, s);
      if (msg) { box.replaceChildren(h('div', 'w-title', o.title || 'Steam'), h('div', 'empty', msg)); return; }
      const head = h('div', 'mst-head',
        s.avatar ? img(proxied(s.avatar), 'mst-av') : h('div', 'mst-av mst-av-none', '🎮'),
        h('div', 'mst-who', h('div', 'mst-name', s.user || 'Steam'),
          h('div', 'mst-state mst-s' + (s.state ?? (s.clientRunning ? 1 : 0)), s.state != null ? STATES[s.state] || 'Online' : s.clientRunning ? 'Steam is open' : 'Steam is closed')));
      if (control) {
        const chips = h('div', 'mst-chips');
        for (const [st, label] of [['online', 'Online'], ['away', 'Away'], ['invisible', 'Invisible'], ['offline', 'Offline']]) {
          const b = h('button', 'mst-chip' + ((st === 'online' && s.state === 1) || (st === 'away' && s.state === 3) || (st === 'offline' && s.state === 0 && s.clientRunning) ? ' on' : ''), label);
          b.addEventListener('click', async () => { if (await post(`status/${st}`)) { setTimeout(() => { sharedAt = 0; }, 1500); } });
          chips.append(b);
        }
        head.append(chips);
      }
      const p = s.playing, a = s.achievements;
      const body = h('div', 'mst-body');
      const app = p?.appId || a?.appId;
      if (app) {
        const info = h('div', 'mst-info',
          h('div', 'mst-label', p ? 'Playing now' : 'Last played'),
          h('div', 'mst-game', p?.name || a?.game || ''));
        if (p?.since) info.append(h('div', 'mst-meta', `Session: ${fmtDur(Date.now() - p.since)}`));
        if (p?.players) info.append(h('div', 'mst-meta', `${p.players.toLocaleString()} playing worldwide`));
        if (a && a.total) {
          const bar = h('div', 'mst-bar', h('i'));
          bar.firstChild.style.width = `${(a.done / a.total) * 100}%`;
          info.append(h('div', 'mst-ach-head', h('span', '', '🏆 Achievements'), h('b', '', `${a.done}/${a.total}`)), bar);
          const icons = h('div', 'mst-ach-icons', ...a.recent.slice(0, 6).map((x) => { const i = img(proxied(x.icon), 'mst-ach-ico', x.name); i.title = x.name; return i; }));
          if (a.recent.length) info.append(icons, h('div', 'mst-meta mst-ach-last', `Last: ${a.recent[0].name}`));
        } else if (!s.hasKey) info.append(h('div', 'mst-meta mst-hint', 'Achievements show once the game has been opened on this PC'));
        body.append(img(cover(app), 'mst-cover', ''), info);
      } else body.append(h('div', 'empty', 'Not playing anything'));
      const parts = [head, body];
      const rows = [...(s.downloads || []).slice(0, 2).map(downloadRow), ...(s.updates || []).slice(0, Math.max(0, 2 - (s.downloads?.length || 0))).map((u) => updateRow(u, control))];
      if (rows.length) parts.push(h('div', 'mst-dls', ...rows));
      if (control) {
        const bar = h('div', 'mst-actions');
        for (const [page, label] of [['friends', 'Friends'], ['library', 'Library'], ['downloads', 'Downloads'], ['bigpicture', 'Big Picture']]) {
          const b = h('button', 'mst-btn', label);
          b.addEventListener('click', () => post(`open/${page}`));
          bar.append(b);
        }
        parts.push(bar);
      }
      box.replaceChildren(...parts);
    };
    const tick = every(3000, async () => { try { data = await steamData(); render(); return true; } catch (_) { return false; } }, 5000);
    return { update: () => { tick(); render(); } };
  },
};

// ================================================================ Friends
W['my-steam-friends'] = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, key = '';
    box.classList.add('st');
    const list = h('div', 'mst-friends');
    const title = h('div', 'w-title', o.title || 'Steam friends', h('span', 'mst-count'));
    box.append(title, list);
    const render = () => {
      const s = data;
      const msg = message(box, s, true);
      const k = JSON.stringify([msg, s?.friends, o.offline]);
      if (k === key) return;
      key = k;
      if (msg) { list.replaceChildren(h('div', 'empty', msg)); return; }
      const online = s.friends.filter((f) => f.state > 0);
      title.querySelector('.mst-count').textContent = ` ${online.length} online`;
      const shown = o.offline ? s.friends : online;
      if (!shown.length) { list.replaceChildren(h('div', 'empty', s.friends.length ? 'No friends online' : 'Your friends list is empty or private')); return; }
      list.replaceChildren(...shown.map((f) => h('div', 'mst-friend' + (f.game ? ' playing' : f.state === 0 ? ' off' : ''),
        img(proxied(f.avatar), 'mst-fav'),
        h('div', 'mst-fmain', h('div', 'mst-fname', f.name),
          h('div', 'mst-fgame', f.game ? f.game : f.state === 0 ? (f.lastOff ? `Last online ${shortDur(Date.now() - f.lastOff)} ago` : 'Offline') : STATES[f.state] || 'Online')))));
    };
    const tick = every(5000, async () => { try { data = await steamData(); render(); return true; } catch (_) { return false; } }, 5000);
    return { update: () => { tick(); render(); } };
  },
};

// ================================================================ Game sheet (library tap)
const ago = (ms) => (Date.now() - ms < 86_400_000 ? 'today' : `${shortDur(Date.now() - ms)} ago`);
const day = (ms) => new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });

/** A dialog with the game's details and its achievements; it closes like the other overlays (Back, idle return, page change). */
function openGame(g, control) {
  haptic();
  const backdrop = h('div', 'session-backdrop mst-sheet-back');
  const panel = h('div', 'mst-sheet');
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-modal', 'true');
  const close = h('button', 'session-close mst-sheet-close', '✕');
  close.setAttribute('aria-label', 'Close');
  const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
  const dismiss = () => { backdrop.remove(); document.removeEventListener('keydown', onKey); };
  close.addEventListener('click', dismiss);
  backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
  document.addEventListener('keydown', onKey);

  // the game's art fades in behind everything; two columns on a landscape panel: the game, then its achievements
  const hero = h('div', 'mst-sheet-hero', img(cover(g.appId, 'hero'), 'mst-sheet-banner', ''));
  const info = h('div', 'mst-sheet-info', h('h2', 'mst-sheet-title', g.name), h('div', 'empty', 'Loading…'));
  const body = h('div', 'mst-sheet-ach');
  panel.append(hero, h('div', 'mst-sheet-side', info), body, close);
  backdrop.append(panel);
  document.body.append(backdrop);

  let filter = 'all', d = null;
  const stat = (label, value) => h('div', 'mst-stat', h('span', null, label), h('b', null, value));
  const render = () => {
    const tags = [];
    if (d.playing) tags.push(h('span', 'mst-tag live', 'Playing now'));
    if (d.downloading) tags.push(h('span', 'mst-tag warn', 'Downloading'));
    else if (d.updateAvailable) tags.push(h('span', 'mst-tag warn', 'Update available'));
    tags.push(h('span', 'mst-tag', d.installed ? `Installed${d.sizeBytes ? ' · ' + bytes(d.sizeBytes) : ''}` : 'Not installed'));
    const stats = h('div', 'mst-stats',
      stat('Play time', d.playtimeMin ? hours(d.playtimeMin) : 'Never played'),
      stat('Last 2 weeks', d.twoWeeksMin ? hours(d.twoWeeksMin) : '—'),
      stat('Last played', d.lastPlayed ? ago(d.lastPlayed * 1000) : '—'),
      stat('Playing now', d.players != null ? d.players.toLocaleString() : '—'));
    const side = [h('h2', 'mst-sheet-title', d.name), h('div', 'mst-tags', ...tags), stats];
    if (control && d.installed && !d.playing) {
      const play = h('button', 'mst-play', '▶  Play on the PC');
      play.disabled = d.downloading;
      play.addEventListener('click', async () => { if (await post(`launch/${d.appId}`, `Starting ${d.name} on the PC…`)) dismiss(); });
      side.push(play);
    }
    if (control && d.installed && d.updateAvailable && !d.downloading) {
      const upd = h('button', 'mst-play mst-play-upd', '⟳  Update on the PC');
      upd.addEventListener('click', async () => { upd.disabled = true; await startUpdate(d.appId, d.name); d.updateAvailable = false; render(); });
      side.push(upd, h('div', 'mst-meta mst-hint', 'Steam checks the game\'s files first, then downloads the update'));
    }
    info.replaceChildren(...side);
    // achievements
    const ach = h('div', 'mst-ach-sec');
    if (d.total) {
      const pct = Math.round((d.done / d.total) * 100);
      const bar = h('div', 'mst-bar', h('i'));
      bar.firstChild.style.width = `${pct}%`;
      const seg = h('div', 'seg mst-ach-seg');
      for (const [k, label] of [['all', 'All'], ['done', `Unlocked ${d.done}`], ['todo', `Locked ${d.total - d.done}`]]) {
        const b = h('button', filter === k ? 'on' : null, label);
        b.addEventListener('click', () => { haptic(); filter = k; render(); });
        seg.append(b);
      }
      const shown = d.achievements.filter((a) => filter === 'all' || (filter === 'done') === a.achieved);
      ach.append(h('div', 'mst-ach-head', h('span', '', '🏆 Achievements'), h('b', '', `${d.done}/${d.total} · ${pct}%`)), bar, seg,
        h('div', 'mst-ach-list', ...(shown.length ? shown.map((a) => h('div', 'mst-ach-row' + (a.achieved ? ' got' : ''),
          img(proxied(a.icon), 'mst-ach-big'),
          h('div', 'mst-ach-main', h('div', 'mst-ach-name', a.name),
            h('div', 'mst-ach-desc', a.description || (a.hidden ? 'Hidden achievement' : ''))),
          h('div', 'mst-ach-side',
            a.achieved ? h('div', null, day(a.unlockedAt)) : h('div', 'mst-ach-lock', '🔒'),
            a.percent != null ? h('small', a.percent < 10 ? 'rare' : null, `${a.percent}% of players`) : '')))
          : [h('div', 'empty', filter === 'done' ? 'Nothing unlocked yet' : 'All unlocked 🎉')])));
    } else ach.append(h('div', 'mst-ach-head', h('span', '', '🏆 Achievements')), h('div', 'empty mst-ach-note', d.achievementsNote || 'This game has no achievements'));
    const scroll = body.scrollTop;
    body.replaceChildren(ach);
    body.scrollTop = scroll;
  };
  api(`/my-steam/game/${g.appId}`, 'GET', 12_000).then((x) => { d = x; if (backdrop.isConnected) render(); })
    .catch(() => { if (backdrop.isConnected) info.replaceChildren(h('h2', 'mst-sheet-title', g.name), h('div', 'empty', 'The PC did not answer')); });
}

// ================================================================ Library (launcher)
W['my-steam-library'] = {
  needs: ['tick'],
  create(box, { o }) {
    let games = null, data = null, key = '';
    const control = o.control !== false;
    box.classList.add('st');
    const dls = h('div', 'mst-dls');
    const grid = h('div', 'mst-grid');
    box.append(h('div', 'w-title', o.title || 'Steam library'), dls, grid);
    const render = () => {
      const msg = message(box, data);
      const k = JSON.stringify([msg, games?.map((g) => [g.appId, g.downloading]), data?.downloads, data?.updates, o.sort, box.clientWidth]);
      if (k === key) return;
      key = k;
      if (msg) { grid.replaceChildren(h('div', 'empty', msg)); dls.replaceChildren(); return; }
      const pending = new Set((data.updates || []).map((u) => u.appId));
      dls.replaceChildren(...(data.downloads?.length ? data.downloads.slice(0, 1).map(downloadRow) : (data.updates || []).slice(0, 1).map((u) => updateRow(u, control))));
      if (!games) { grid.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!games.length) { grid.replaceChildren(h('div', 'empty', 'No games installed')); return; }
      const list = [...games];
      if (o.sort === 'name') list.sort((a, b) => a.name.localeCompare(b.name));
      else if (o.sort === 'playtime') list.sort((a, b) => b.playtimeMin - a.playtimeMin);
      grid.replaceChildren(...list.map((g) => {
        const card = h('button', 'mst-card' + (g.downloading ? ' busy' : ''), img(cover(g.appId), 'mst-card-img', g.name),
          h('div', 'mst-card-name', g.name), h('div', 'mst-card-meta', g.playtimeMin ? hours(g.playtimeMin) : 'Never played'));
        if (!g.downloading && pending.has(g.appId)) card.append(h('span', 'mst-card-upd', 'Update'));
        card.addEventListener('click', () => openGame(g, control));
        return card;
      }));
    };
    const loadGames = every(60_000, async () => { try { games = (await api('/my-steam/library')).games || []; render(); return true; } catch (_) { return false; } }, 10_000);
    const tick = every(4000, async () => { try { data = await steamData(); render(); return true; } catch (_) { return false; } }, 5000);
    return { update: () => { loadGames(); tick(); render(); } };
  },
};
