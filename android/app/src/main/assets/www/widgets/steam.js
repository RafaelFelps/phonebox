import { API, PANEL_HEADERS, api, fmtDur, h, haptic, shortDur, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// Steam: the Steam client on the PC (games, downloads, play time, screenshots) and, with a Web API key in
// Settings › Steam, friends, achievements, news and wishlist sales. Five widgets share one reading of /api/steam.
const STATES = ['Offline', 'Online', 'Busy', 'Away', 'Snooze', 'Looking to trade', 'Looking to play'];
const STATUS_TEXT = {
  off: 'Add a Steam widget to a page',
  'not-installed': 'Steam is not installed on the PC',
};

// one request for every Steam widget on screen
let shared = null, sharedAt = 0, inflight = null;
async function steamData(maxAge = 2500) {
  if (shared && Date.now() - sharedAt < maxAge) return shared;
  inflight ??= api('/steam').then((d) => { shared = d; sharedAt = Date.now(); return d; }).finally(() => { inflight = null; });
  return inflight;
}

const cover = (app, kind = 'portrait') => `${API}/steam/cover?app=${app}&kind=${kind}`;
const proxied = (url) => (url ? `${API}/steam/img?u=${encodeURIComponent(url)}` : '');
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
    const r = await fetch(`${API}/steam/${path}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
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
  const bar = h('div', 'st-bar' + (busy ? ' busy' : ''), h('i'));
  // each poll builds a new bar: it starts where the last one was and slides to the new value
  if (!busy) {
    const i = bar.firstChild, from = lastPct.get(d.appId);
    i.style.width = `${from ?? d.progress}%`;
    if (from != null && from !== d.progress) requestAnimationFrame(() => requestAnimationFrame(() => { i.style.width = `${d.progress}%`; }));
    lastPct.set(d.appId, d.progress);
  }
  return h('div', 'st-dl', img(cover(d.appId, 'header'), 'st-dl-img'),
    h('div', 'st-dl-main', h('div', 'st-dl-name', d.name), bar,
      h('div', 'st-dl-meta', (busy ? phase : `${phase} ${Math.round(d.progress)}%`) + (d.total ? ` · ${bytes(d.done)} of ${bytes(d.total)}` : '') + speed + eta)));
}

/** Asks Steam on the PC to update the game now (it verifies the files first, then downloads the new build). */
async function startUpdate(appId, name) {
  if (await post(`update/${appId}`, `Steam is checking ${name} and will update it`)) setTimeout(() => { sharedAt = 0; }, 1500);
}

/** A game with an update Steam has not started yet. */
function updateRow(u, control) {
  const main = h('div', 'st-dl-main', h('div', 'st-dl-name', u.name), h('div', 'st-dl-meta', 'Update available'));
  const row = h('div', 'st-dl st-upd', img(cover(u.appId, 'header'), 'st-dl-img'), main);
  if (control) {
    const b = h('button', 'st-upd-btn', 'Update');
    b.addEventListener('click', (e) => { e.stopPropagation(); b.disabled = true; startUpdate(u.appId, u.name).finally(() => { b.disabled = false; }); });
    row.append(b);
  }
  return row;
}

// ================================================================ Now playing
W.steam = {
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
      const head = h('div', 'st-head',
        s.avatar ? img(proxied(s.avatar), 'st-av') : h('div', 'st-av st-av-none', '🎮'),
        h('div', 'st-who', h('div', 'st-name', s.user || 'Steam'),
          h('div', 'st-state st-s' + (s.state ?? (s.clientRunning ? 1 : 0)), s.state != null ? STATES[s.state] || 'Online' : s.clientRunning ? 'Steam is open' : 'Steam is closed')));
      if (control) {
        const chips = h('div', 'st-chips');
        for (const [st, label] of [['online', 'Online'], ['away', 'Away'], ['invisible', 'Invisible'], ['offline', 'Offline']]) {
          const b = h('button', 'st-chip' + ((st === 'online' && s.state === 1) || (st === 'away' && s.state === 3) || (st === 'offline' && s.state === 0 && s.clientRunning) ? ' on' : ''), label);
          b.addEventListener('click', async () => { if (await post(`status/${st}`)) { setTimeout(() => { sharedAt = 0; }, 1500); } });
          chips.append(b);
        }
        head.append(chips);
      }
      const p = s.playing, a = s.achievements;
      const body = h('div', 'st-body');
      const app = p?.appId || a?.appId;
      if (app) {
        const info = h('div', 'st-info',
          h('div', 'st-label', p ? 'Playing now' : 'Last played'),
          h('div', 'st-game', p?.name || a?.game || ''));
        if (p?.since) info.append(h('div', 'st-meta', `Session: ${fmtDur(Date.now() - p.since)}`));
        if (p?.players) info.append(h('div', 'st-meta', `${p.players.toLocaleString()} playing worldwide`));
        if (a && a.total) {
          const bar = h('div', 'st-bar', h('i'));
          bar.firstChild.style.width = `${(a.done / a.total) * 100}%`;
          info.append(h('div', 'st-ach-head', h('span', '', '🏆 Achievements'), h('b', '', `${a.done}/${a.total}`)), bar);
          const icons = h('div', 'st-ach-icons', ...a.recent.slice(0, 6).map((x) => { const i = img(proxied(x.icon), 'st-ach-ico', x.name); i.title = x.name; return i; }));
          if (a.recent.length) info.append(icons, h('div', 'st-meta st-ach-last', `Last: ${a.recent[0].name}`));
        } else if (!s.hasKey) info.append(h('div', 'st-meta st-hint', 'Achievements show once the game has been opened on this PC'));
        body.append(img(cover(app), 'st-cover', ''), info);
      } else body.append(h('div', 'empty', 'Not playing anything'));
      const parts = [head, body];
      const rows = [...(s.downloads || []).slice(0, 2).map(downloadRow), ...(s.updates || []).slice(0, Math.max(0, 2 - (s.downloads?.length || 0))).map((u) => updateRow(u, control))];
      if (rows.length) parts.push(h('div', 'st-dls', ...rows));
      if (control) {
        const bar = h('div', 'st-actions');
        for (const [page, label] of [['friends', 'Friends'], ['library', 'Library'], ['downloads', 'Downloads'], ['bigpicture', 'Big Picture']]) {
          const b = h('button', 'st-btn', label);
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
W['steam-friends'] = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, key = '';
    box.classList.add('st');
    const list = h('div', 'st-friends');
    const title = h('div', 'w-title', o.title || 'Steam friends', h('span', 'st-count'));
    box.append(title, list);
    const render = () => {
      const s = data;
      const msg = message(box, s, true);
      const k = JSON.stringify([msg, s?.friends, o.offline]);
      if (k === key) return;
      key = k;
      if (msg) { list.replaceChildren(h('div', 'empty', msg)); return; }
      const online = s.friends.filter((f) => f.state > 0);
      title.querySelector('.st-count').textContent = ` ${online.length} online`;
      const shown = o.offline ? s.friends : online;
      if (!shown.length) { list.replaceChildren(h('div', 'empty', s.friends.length ? 'No friends online' : 'Your friends list is empty or private')); return; }
      list.replaceChildren(...shown.map((f) => h('div', 'st-friend' + (f.game ? ' playing' : f.state === 0 ? ' off' : ''),
        img(proxied(f.avatar), 'st-fav'),
        h('div', 'st-fmain', h('div', 'st-fname', f.name),
          h('div', 'st-fgame', f.game ? f.game : f.state === 0 ? (f.lastOff ? `Last online ${shortDur(Date.now() - f.lastOff)} ago` : 'Offline') : STATES[f.state] || 'Online')))));
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
  const backdrop = h('div', 'session-backdrop st-sheet-back');
  const panel = h('div', 'st-sheet');
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-modal', 'true');
  const close = h('button', 'session-close st-sheet-close', '✕');
  close.setAttribute('aria-label', 'Close');
  const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
  const dismiss = () => { backdrop.remove(); document.removeEventListener('keydown', onKey); };
  close.addEventListener('click', dismiss);
  backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
  document.addEventListener('keydown', onKey);

  // the game's art fades in behind everything; two columns on a landscape panel: the game, then its achievements
  const hero = h('div', 'st-sheet-hero', img(cover(g.appId, 'hero'), 'st-sheet-banner', ''));
  const info = h('div', 'st-sheet-info', h('h2', 'st-sheet-title', g.name), h('div', 'empty', 'Loading…'));
  const body = h('div', 'st-sheet-ach');
  panel.append(hero, h('div', 'st-sheet-side', info), body, close);
  backdrop.append(panel);
  document.body.append(backdrop);

  let filter = 'all', d = null;
  const stat = (label, value) => h('div', 'st-stat', h('span', null, label), h('b', null, value));
  const render = () => {
    const tags = [];
    if (d.playing) tags.push(h('span', 'st-tag live', 'Playing now'));
    if (d.downloading) tags.push(h('span', 'st-tag warn', 'Downloading'));
    else if (d.updateAvailable) tags.push(h('span', 'st-tag warn', 'Update available'));
    tags.push(h('span', 'st-tag', d.installed ? `Installed${d.sizeBytes ? ' · ' + bytes(d.sizeBytes) : ''}` : 'Not installed'));
    const stats = h('div', 'st-stats',
      stat('Play time', d.playtimeMin ? hours(d.playtimeMin) : 'Never played'),
      stat('Last 2 weeks', d.twoWeeksMin ? hours(d.twoWeeksMin) : '—'),
      stat('Last played', d.lastPlayed ? ago(d.lastPlayed * 1000) : '—'),
      stat('Playing now', d.players != null ? d.players.toLocaleString() : '—'));
    const side = [h('h2', 'st-sheet-title', d.name), h('div', 'st-tags', ...tags), stats];
    if (control && d.installed && !d.playing) {
      const play = h('button', 'st-play', '▶  Play on the PC');
      play.disabled = d.downloading;
      play.addEventListener('click', async () => { if (await post(`launch/${d.appId}`, `Starting ${d.name} on the PC…`)) dismiss(); });
      side.push(play);
    }
    if (control && d.installed && d.updateAvailable && !d.downloading) {
      const upd = h('button', 'st-play st-play-upd', '⟳  Update on the PC');
      upd.addEventListener('click', async () => { upd.disabled = true; await startUpdate(d.appId, d.name); d.updateAvailable = false; render(); });
      side.push(upd, h('div', 'st-meta st-hint', 'Steam checks the game\'s files first, then downloads the update'));
    }
    info.replaceChildren(...side);
    // achievements
    const ach = h('div', 'st-ach-sec');
    if (d.total) {
      const pct = Math.round((d.done / d.total) * 100);
      const bar = h('div', 'st-bar', h('i'));
      bar.firstChild.style.width = `${pct}%`;
      const seg = h('div', 'seg st-ach-seg');
      for (const [k, label] of [['all', 'All'], ['done', `Unlocked ${d.done}`], ['todo', `Locked ${d.total - d.done}`]]) {
        const b = h('button', filter === k ? 'on' : null, label);
        b.addEventListener('click', () => { haptic(); filter = k; render(); });
        seg.append(b);
      }
      const shown = d.achievements.filter((a) => filter === 'all' || (filter === 'done') === a.achieved);
      ach.append(h('div', 'st-ach-head', h('span', '', '🏆 Achievements'), h('b', '', `${d.done}/${d.total} · ${pct}%`)), bar, seg,
        h('div', 'st-ach-list', ...(shown.length ? shown.map((a) => h('div', 'st-ach-row' + (a.achieved ? ' got' : ''),
          img(proxied(a.icon), 'st-ach-big'),
          h('div', 'st-ach-main', h('div', 'st-ach-name', a.name),
            h('div', 'st-ach-desc', a.description || (a.hidden ? 'Hidden achievement' : ''))),
          h('div', 'st-ach-side',
            a.achieved ? h('div', null, day(a.unlockedAt)) : h('div', 'st-ach-lock', '🔒'),
            a.percent != null ? h('small', a.percent < 10 ? 'rare' : null, `${a.percent}% of players`) : '')))
          : [h('div', 'empty', filter === 'done' ? 'Nothing unlocked yet' : 'All unlocked 🎉')])));
    } else ach.append(h('div', 'st-ach-head', h('span', '', '🏆 Achievements')), h('div', 'empty st-ach-note', d.achievementsNote || 'This game has no achievements'));
    const scroll = body.scrollTop;
    body.replaceChildren(ach);
    body.scrollTop = scroll;
  };
  api(`/steam/game/${g.appId}`, 'GET', 12_000).then((x) => { d = x; if (backdrop.isConnected) render(); })
    .catch(() => { if (backdrop.isConnected) info.replaceChildren(h('h2', 'st-sheet-title', g.name), h('div', 'empty', 'The PC did not answer')); });
}

// ================================================================ Library (launcher)
W['steam-library'] = {
  needs: ['tick'],
  create(box, { o }) {
    let games = null, data = null, key = '';
    const control = o.control !== false;
    box.classList.add('st');
    const dls = h('div', 'st-dls');
    const grid = h('div', 'st-grid');
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
        const card = h('button', 'st-card' + (g.downloading ? ' busy' : ''), img(cover(g.appId), 'st-card-img', g.name),
          h('div', 'st-card-name', g.name), h('div', 'st-card-meta', g.playtimeMin ? hours(g.playtimeMin) : 'Never played'));
        if (!g.downloading && pending.has(g.appId)) card.append(h('span', 'st-card-upd', 'Update'));
        card.addEventListener('click', () => openGame(g, control));
        return card;
      }));
    };
    const loadGames = every(60_000, async () => { try { games = (await api('/steam/library')).games || []; render(); return true; } catch (_) { return false; } }, 10_000);
    const tick = every(4000, async () => { try { data = await steamData(); render(); return true; } catch (_) { return false; } }, 5000);
    return { update: () => { loadGames(); tick(); render(); } };
  },
};

// ================================================================ Screenshots
W['steam-screenshots'] = {
  needs: ['tick'],
  create(box, { o }) {
    let shots = null, key = '', status = null;
    box.classList.add('st');
    const grid = h('div', 'st-shots');
    box.append(h('div', 'w-title', o.title || 'Steam screenshots'), grid);
    const open = (s) => {
      haptic();
      const big = img(`${API}/steam/shot?app=${s.app}&file=${encodeURIComponent(s.file)}`, 'st-shot-big', s.game);
      const layer = h('div', 'st-viewer', big, h('div', 'st-viewer-cap', `${s.game} · ${new Date(s.taken).toLocaleString()}`));
      layer.addEventListener('click', () => layer.remove());
      document.body.append(layer);
    };
    const render = () => {
      const msg = status ? message(box, status) : null;
      const k = JSON.stringify([msg, shots?.map((s) => s.file)]);
      if (k === key) return;
      key = k;
      if (msg) { grid.replaceChildren(h('div', 'empty', msg)); return; }
      if (!shots) { grid.replaceChildren(h('div', 'empty', 'Loading…')); return; }
      if (!shots.length) { grid.replaceChildren(h('div', 'empty', 'No screenshots yet: press F12 in a game')); return; }
      grid.replaceChildren(...shots.slice(0, +o.count || 24).map((s) => {
        const b = h('button', 'st-shot', img(`${API}/steam/shot?app=${s.app}&file=${encodeURIComponent(s.file)}&thumb=true`, 'st-shot-img', s.game),
          h('div', 'st-shot-cap', s.game));
        b.addEventListener('click', () => open(s));
        return b;
      }));
    };
    const load = every(30_000, async () => {
      try { status = await steamData(10_000); shots = (await api('/steam/screenshots')).shots || []; render(); return true; } catch (_) { return false; }
    }, 10_000);
    return { update: () => { load(); render(); } };
  },
};

// ================================================================ Store: wishlist sales and news
W['steam-store'] = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, key = '', tab = o.start === 'news' ? 'news' : 'sales';
    box.classList.add('st');
    const tabs = h('div', 'seg', h('button', tab === 'sales' ? 'on' : null, 'Sales'), h('button', tab === 'news' ? 'on' : null, 'News'));
    const list = h('div', 'st-store');
    box.append(h('div', 'st-store-head', h('span', 'w-title', o.title || 'Steam'), tabs), list);
    [...tabs.children].forEach((b, i) => b.addEventListener('click', () => {
      haptic(); tab = i ? 'news' : 'sales';
      [...tabs.children].forEach((x, j) => x.classList.toggle('on', j === i));
      key = ''; render();
    }));
    const render = () => {
      const s = data;
      const msg = message(box, s, tab === 'sales');
      const k = JSON.stringify([msg, tab, s?.sales, s?.news, s?.newsGame]);
      if (k === key) return;
      key = k;
      if (msg) { list.replaceChildren(h('div', 'empty', msg)); return; }
      if (tab === 'sales') {
        if (!s.sales.length) { list.replaceChildren(h('div', 'empty', 'No wishlist game is on sale right now')); return; }
        list.replaceChildren(...s.sales.map((x) => h('div', 'st-sale', img(cover(x.appId, 'header'), 'st-sale-img', x.name),
          h('div', 'st-sale-main', h('div', 'st-sale-name', x.name),
            h('div', 'st-sale-price', h('span', 'st-off', `-${x.discount}%`), x.was ? h('s', '', x.was) : '', h('b', '', x.price))))));
      } else {
        if (!s.news.length) { list.replaceChildren(h('div', 'empty', 'No news')); return; }
        list.replaceChildren(h('div', 'st-news-game', `News · ${s.newsGame || ''}`), ...s.news.map((n) => h('div', 'st-news',
          h('div', 'st-news-title', n.title),
          h('div', 'st-news-meta', [n.feed, n.date ? new Date(n.date).toLocaleDateString() : ''].filter(Boolean).join(' · ')),
          h('div', 'st-news-text', n.contents))));
      }
    };
    const tick = every(30_000, async () => { try { data = await steamData(10_000); render(); return true; } catch (_) { return false; } }, 10_000);
    return { update: () => { tick(); render(); } };
  },
};
