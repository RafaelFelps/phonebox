import { LOCALE, api, h, haptic, store } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// Chess: your recent chess.com / Lichess games and a move-by-move review of a finished one: the board, an evaluation
// bar, the quality of each move (best, inaccuracy, mistake, blunder), the better move as an arrow, and accuracy.
// The analysis is Lichess's own, or Stockfish on the PC. Finished games only, never a game in progress.
const GLYPH = { k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟' };
const CLASS = {
  best: ['Best move', '★'], good: ['Good', '✓'], inaccuracy: ['Inaccuracy', '?!'], mistake: ['Mistake', '?'], blunder: ['Blunder', '??'],
};
const SPEED = { ultraBullet: 'UltraBullet', bullet: 'Bullet', blitz: 'Blitz', rapid: 'Rapid', classical: 'Classical', correspondence: 'Daily', daily: 'Daily' };

const sq = (name) => ({ f: name.charCodeAt(0) - 97, r: name.charCodeAt(1) - 49 });

function parseFen(fen) {
  const rows = fen.split(' ')[0].split('/');
  const board = [];
  rows.forEach((row, ri) => {
    let f = 0;
    for (const c of row) {
      if (/\d/.test(c)) { f += +c; continue; }
      board.push({ f, r: 7 - ri, p: c });
      f++;
    }
  });
  return board;
}

/** White's winning chances (0–100) from an evaluation in pawns or a mate. */
const whiteWin = (m) => {
  if (!m) return 50;
  if (m.mate != null) return m.mate > 0 ? 100 : 0;
  if (m.eval == null) return 50;
  return 50 + 50 * (2 / (1 + Math.exp(-0.00368208 * Math.max(-1000, Math.min(1000, m.eval * 100)))) - 1);
};
const evalText = (m) => {
  if (!m) return '0.0';
  if (m.mate != null) return (m.mate > 0 ? '+' : '−') + 'M' + Math.abs(m.mate);
  if (m.eval == null) return '';
  if (Math.abs(m.eval) >= 99) return '#';
  return (m.eval > 0 ? '+' : m.eval < 0 ? '−' : '') + Math.abs(m.eval).toFixed(1);
};

W.chess = {
  needs: ['tick'],
  create(box, { o, w }) {
    const site = o.site === 'chesscom' ? 'chesscom' : 'lichess';
    const user = String(o.user || '').trim();
    const memo = `chess:${w?.id || site + user}`;
    let list = null, review = null, gameId = store.get(memo, null), ply = -1, view = 'board', lastKey = '';
    box.classList.add('cz');

    const loadList = async () => {
      if (!user) return true;
      try {
        list = await api(`/chess/games?site=${site}&user=${encodeURIComponent(user)}`, 'GET', 20000);
        if (!gameId && list.games?.length) gameId = list.games[0].id;
        return !list.error;
      } catch (_) { return false; }
    };
    const loadGame = async () => {
      if (!gameId) return;
      try {
        const next = await api(`/chess/game/${encodeURIComponent(gameId)}`, 'GET', 8000);
        const fresh = !review || review.game.id !== next.game.id;
        review = next;
        if (fresh) ply = review.moves.length - 1;
      } catch (_) { review = review?.game.id === gameId ? review : null; }
    };
    const choose = async (g) => {
      haptic();
      gameId = g.id; store.set(memo, gameId); view = 'board'; review = null;
      render(true);
      await loadGame(); render(true);
    };
    const go = (to) => {
      if (!review) return;
      ply = Math.max(-1, Math.min(review.moves.length - 1, to));
      haptic(); render(true);
    };
    const nextError = () => {
      if (!review) return;
      const whiteFirst = review.startFen.split(' ')[1] === 'w';
      const mine = (i) => ((i % 2 === 0) === whiteFirst) === (review.game.me === 'white');
      const bad = (m) => m.class === 'mistake' || m.class === 'blunder';
      let i = review.moves.findIndex((m, j) => j > ply && mine(j) && bad(m));
      if (i < 0) i = review.moves.findIndex((m, j) => mine(j) && bad(m));
      if (i >= 0) go(i);
    };

    const boardEl = (fen, last, best, flip) => {
      const b = h('div', 'cz-board' + (flip ? ' flip' : ''));
      for (let r = 7; r >= 0; r--) for (let f = 0; f < 8; f++) {
        const cell = h('div', 'cz-sq ' + ((f + r) % 2 ? 'l' : 'd'));
        cell.style.gridRow = String(flip ? r + 1 : 8 - r);
        cell.style.gridColumn = String(flip ? 8 - f : f + 1);
        if (last && ((last.from.f === f && last.from.r === r) || (last.to.f === f && last.to.r === r))) cell.classList.add('last');
        b.append(cell);
      }
      for (const { f, r, p } of parseFen(fen)) {
        const white = p === p.toUpperCase();
        const piece = h('div', 'cz-pc ' + (white ? 'w' : 'b'), GLYPH[p.toLowerCase()] + '︎');
        piece.style.gridRow = String(flip ? r + 1 : 8 - r);
        piece.style.gridColumn = String(flip ? 8 - f : f + 1);
        b.append(piece);
      }
      if (best) {
        const x = (s) => (flip ? 7 - s.f : s.f) + 0.5, y = (s) => (flip ? s.r : 7 - s.r) + 0.5;
        const a = best.from, z = best.to;
        const dx = x(z) - x(a), dy = y(z) - y(a), len = Math.hypot(dx, dy) || 1;
        const ex = x(z) - (dx / len) * 0.32, ey = y(z) - (dy / len) * 0.32;
        b.insertAdjacentHTML('beforeend', `<svg class="cz-arrow" viewBox="0 0 8 8"><defs><marker id="czh" markerWidth="3" markerHeight="3" refX="1.5" refY="1.5" orient="auto"><path d="M0,0 L3,1.5 L0,3 z" fill="currentColor"/></marker></defs><line x1="${x(a)}" y1="${y(a)}" x2="${ex}" y2="${ey}" stroke="currentColor" stroke-width=".17" stroke-linecap="round" marker-end="url(#czh)"/></svg>`); // numbers only
      }
      return b;
    };
    const uciMove = (u) => (u && u.length >= 4 ? { from: sq(u.slice(0, 2)), to: sq(u.slice(2, 4)) } : null);

    const player = (p, color, me) => h('div', 'cz-pl' + (me ? ' me' : ''),
      h('span', 'cz-dot ' + color),
      h('span', 'cz-pn', p.name), p.rating ? h('small', null, ' ' + p.rating) : '',
      p.accuracy != null ? h('span', 'cz-acc', p.accuracy.toFixed(1) + '%') : '');

    const renderList = () => {
      const games = list?.games || [];
      const ratings = Object.entries(list?.ratings || {}).map(([k, v]) => `${SPEED[k] || k} ${v}`).join(' · ');
      const back = h('button', 'cz-btn', '✕');
      back.addEventListener('click', () => { haptic(); view = 'board'; render(true); });
      box.replaceChildren(
        h('div', 'w-title cz-head', (site === 'lichess' ? 'Lichess · ' : 'chess.com · ') + user, h('span', 'cz-rt', ratings), back),
        h('div', 'cz-games', ...games.map((g) => {
          const opp = g.me === 'white' ? g.black : g.white;
          const row = h('div', 'cz-g ' + g.outcome + (g.id === gameId ? ' sel' : ''),
            h('span', 'cz-res', g.outcome === 'win' ? 'W' : g.outcome === 'loss' ? 'L' : '½'),
            h('span', 'cz-gn', (g.me === 'white' ? '○ ' : '● ') + opp.name, opp.rating ? h('small', null, ' ' + opp.rating) : ''),
            h('span', 'cz-gs', `${SPEED[g.speed] || g.speed} · ${new Date(g.end).toLocaleDateString(LOCALE, { day: 'numeric', month: 'short' })}`));
          row.addEventListener('click', () => choose(g));
          return row;
        }), ...(games.length ? [] : [h('div', 'empty', 'No finished games')])));
    };

    const render = (force = false) => {
      const key = JSON.stringify([view, list?.games?.length, list?.error, review?.status, review?.progress, review?.game?.id, ply, box.clientWidth, box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!user) { box.replaceChildren(h('div', 'empty', 'Set your chess.com or Lichess username in the widget settings on the PC')); return; }
      box.classList.toggle('list', view === 'list');
      if (view === 'list') box.classList.remove('narrow');
      if (view === 'list') { renderList(); return; }
      if (list?.error && !review) { box.replaceChildren(h('div', 'empty', '⚠ ' + list.error)); return; }
      if (!review) { box.replaceChildren(h('div', 'empty', list && !list.games?.length ? 'No finished games yet' : 'Loading games…')); return; }

      const g = review.game, moves = review.moves, cur = ply >= 0 ? moves[ply] : null;
      const flip = g.me === 'black';
      const fen = cur ? cur.fen : review.startFen;
      const showBest = cur && cur.best && (cur.class === 'mistake' || cur.class === 'blunder' || cur.class === 'inaccuracy');
      const board = boardEl(fen, cur ? uciMove(cur.uci) : null, showBest ? uciMove(cur.best) : null, flip);
      const win = whiteWin(cur || null);
      const evalBar = h('div', 'cz-eval' + (flip ? ' flip' : ''), h('div', 'cz-evw'), h('span', 'cz-evt', review.status === 'done' ? evalText(cur) : ''));
      evalBar.firstChild.style.height = (review.status === 'done' ? win : 50).toFixed(1) + '%';
      const boardWrap = h('div', 'cz-bw', evalBar, board);
      board.addEventListener('click', (e) => { const r = board.getBoundingClientRect(); go(ply + (e.clientX - r.left < r.width / 2 ? -1 : 1)); });

      const firstWhite = review.startFen.split(' ')[1] === 'w';
      const moveNo = (i) => Math.floor((i + (firstWhite ? 0 : 1)) / 2) + 1 + ((i + (firstWhite ? 0 : 1)) % 2 ? '… ' : '. ');
      const [label, mark] = CLASS[cur?.class] || ['', ''];
      const info = cur
        ? h('div', 'cz-mv ' + (cur.class || ''), h('b', null, moveNo(ply) + cur.san + (mark && cur.class !== 'good' && cur.class !== 'best' ? mark : '')),
          label ? h('span', 'cz-tag', label) : '',
          showBest && cur.bestSan ? h('div', 'cz-better', 'Better: ' + cur.bestSan) : '')
        : h('div', 'cz-mv', h('b', null, 'Start'), g.opening ? h('div', 'cz-better', g.opening) : '');

      const mine = (i) => ((i % 2 === 0) === firstWhite) === (g.me === 'white');
      const count = (c) => moves.filter((m, i) => mine(i) && m.class === c).length;
      const summary = review.status === 'done'
        ? h('div', 'cz-sum', h('span', 'blunder', count('blunder') + ' ??'), h('span', 'mistake', count('mistake') + ' ?'), h('span', 'inaccuracy', count('inaccuracy') + ' ?!'))
        : review.status === 'analysing' || review.status === 'pending'
          ? h('div', 'cz-sum', h('span', 'cz-prog', `Analysing… ${Math.round((review.progress || 0) * 100)}%`))
          : h('div', 'cz-sum', h('span', 'cz-err', review.error || ''));

      const ctl = (label2, fn, cls = '') => { const b = h('button', 'cz-btn ' + cls, label2); b.addEventListener('click', fn); return b; };
      const controls = h('div', 'cz-ctl',
        ctl('⏮', () => go(-1)), ctl('◀', () => go(ply - 1)), ctl('▶', () => go(ply + 1)), ctl('⏭', () => go(moves.length - 1)),
        ...(review.status === 'done' ? [ctl('⚠', nextError, 'warn')] : []),
        ctl('☰', () => { haptic(); view = 'list'; render(true); }));

      const res = h('span', 'cz-out ' + g.outcome, g.result);
      const top = flip ? g.white : g.black, bottom = flip ? g.black : g.white;
      const side = h('div', 'cz-side',
        h('div', 'w-title cz-head', `${SPEED[g.speed] || g.speed}`, res, h('span', 'cz-src', review.source || '')),
        player(top, flip ? 'w' : 'b', !flip ? g.me === 'black' : g.me === 'white'),
        player(bottom, flip ? 'b' : 'w', true),
        info, summary, controls);
      box.classList.toggle('narrow', box.clientWidth < box.clientHeight + 140);
      box.replaceChildren(boardWrap, side);
    };

    const listTick = every(120_000, async () => { const ok = await loadList(); if (!review) await loadGame(); render(); return ok; }, 15_000);
    const gameTick = every(1500, async () => {
      if (review && review.status !== 'pending' && review.status !== 'analysing' && review.game.id === gameId) return;
      await loadGame(); render();
    });
    return { update: () => { listTick(); gameTick(); render(); } };
  },
};
