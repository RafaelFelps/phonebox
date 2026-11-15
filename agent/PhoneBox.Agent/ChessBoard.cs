using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// A small, complete chess position for the Chess widget: legal moves, SAN → UCI, and FEN. It only replays finished
/// games (from chess.com or Lichess) so the phone can show every position and the engine can analyse them.
/// Squares are 0..63 with a1 = 0, h8 = 63; pieces are FEN letters (upper case white), '\0' for empty.
/// </summary>
public sealed partial class ChessBoard
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private readonly char[] _sq = new char[64];
    public bool WhiteToMove { get; private set; } = true;
    private string _castling = "KQkq";
    private int _ep = -1, _half, _full = 1;

    public static ChessBoard FromFen(string fen)
    {
        var b = new ChessBoard();
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = parts[0].Split('/');
        if (rows.Length != 8) throw new FormatException("bad FEN");
        for (var r = 0; r < 8; r++)
        {
            var file = 0;
            foreach (var c in rows[r])
            {
                if (char.IsDigit(c)) { file += c - '0'; continue; }
                if (file > 7) throw new FormatException("bad FEN");
                b._sq[(7 - r) * 8 + file++] = c;
            }
        }
        b.WhiteToMove = parts.Length < 2 || parts[1] == "w";
        b._castling = parts.Length > 2 && parts[2] != "-" ? parts[2] : "";
        b._ep = parts.Length > 3 && parts[3] != "-" ? Square(parts[3]) : -1;
        b._half = parts.Length > 4 && int.TryParse(parts[4], out var h) ? h : 0;
        b._full = parts.Length > 5 && int.TryParse(parts[5], out var f) ? f : 1;
        return b;
    }

    public string Fen()
    {
        var sb = new StringBuilder();
        for (var r = 7; r >= 0; r--)
        {
            var empty = 0;
            for (var f = 0; f < 8; f++)
            {
                var p = _sq[r * 8 + f];
                if (p == '\0') { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(p);
            }
            if (empty > 0) sb.Append(empty);
            if (r > 0) sb.Append('/');
        }
        sb.Append(WhiteToMove ? " w " : " b ").Append(_castling.Length > 0 ? _castling : "-")
          .Append(' ').Append(_ep >= 0 ? Name(_ep) : "-").Append(' ').Append(_half).Append(' ').Append(_full);
        return sb.ToString();
    }

    public static int Square(string s) => (s[1] - '1') * 8 + (s[0] - 'a');
    public static string Name(int sq) => $"{(char)('a' + sq % 8)}{(char)('1' + sq / 8)}";

    public readonly record struct Move(int From, int To, char Promo)
    {
        public string Uci => Name(From) + Name(To) + (Promo == '\0' ? "" : char.ToLowerInvariant(Promo).ToString());
    }

    private static bool IsWhite(char p) => p is >= 'A' and <= 'Z';
    private bool Own(char p) => p != '\0' && IsWhite(p) == WhiteToMove;
    private bool Enemy(char p) => p != '\0' && IsWhite(p) != WhiteToMove;

    private static readonly (int df, int dr)[] Knight = [(1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2)];
    private static readonly (int df, int dr)[] King = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
    private static readonly (int df, int dr)[] Rook = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int df, int dr)[] Bishop = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    private static int Step(int sq, int df, int dr)
    {
        int f = sq % 8 + df, r = sq / 8 + dr;
        return f is >= 0 and < 8 && r is >= 0 and < 8 ? r * 8 + f : -1;
    }

    /// <summary>Is <paramref name="sq"/> attacked by the side given (white = true)?</summary>
    private bool Attacked(int sq, bool byWhite)
    {
        char P(char c) => byWhite ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
        var pawnDir = byWhite ? -1 : 1; // a white pawn attacking sq stands one rank below it
        foreach (var df in new[] { -1, 1 })
            if (Step(sq, df, pawnDir) is var s and >= 0 && _sq[s] == P('p')) return true;
        foreach (var (df, dr) in Knight)
            if (Step(sq, df, dr) is var s and >= 0 && _sq[s] == P('n')) return true;
        foreach (var (df, dr) in King)
            if (Step(sq, df, dr) is var s and >= 0 && _sq[s] == P('k')) return true;
        foreach (var (dirs, a, b) in new[] { (Rook, 'r', 'q'), (Bishop, 'b', 'q') })
            foreach (var (df, dr) in dirs)
                for (var s = Step(sq, df, dr); s >= 0; s = Step(s, df, dr))
                {
                    if (_sq[s] == '\0') continue;
                    if (_sq[s] == P(a) || _sq[s] == P(b)) return true;
                    break;
                }
        return false;
    }

    public bool InCheck() => Array.IndexOf(_sq, WhiteToMove ? 'K' : 'k') is var k and >= 0 && Attacked(k, !WhiteToMove);

    public List<Move> LegalMoves()
    {
        var moves = new List<Move>();
        for (var from = 0; from < 64; from++)
        {
            var p = _sq[from];
            if (!Own(p)) continue;
            switch (char.ToLowerInvariant(p))
            {
                case 'p': Pawn(from, moves); break;
                case 'n': Jumps(from, Knight, moves); break;
                case 'k': Jumps(from, King, moves); Castles(from, moves); break;
                case 'b': Slides(from, Bishop, moves); break;
                case 'r': Slides(from, Rook, moves); break;
                case 'q': Slides(from, Bishop, moves); Slides(from, Rook, moves); break;
            }
        }
        return moves.Where(m =>
        {
            var next = Clone();
            next.Apply(m); // Apply keeps the side to move, so InCheck asks about the mover's own king
            return !next.InCheck();
        }).ToList();
    }

    private void Pawn(int from, List<Move> moves)
    {
        var dir = WhiteToMove ? 1 : -1;
        var startRank = WhiteToMove ? 1 : 6;
        var lastRank = WhiteToMove ? 7 : 0;
        void Add(int to)
        {
            if (to / 8 == lastRank) foreach (var promo in "qrbn") moves.Add(new(from, to, WhiteToMove ? char.ToUpperInvariant(promo) : promo));
            else moves.Add(new(from, to, '\0'));
        }
        var one = Step(from, 0, dir);
        if (one >= 0 && _sq[one] == '\0')
        {
            Add(one);
            var two = Step(from, 0, 2 * dir);
            if (from / 8 == startRank && _sq[two] == '\0') moves.Add(new(from, two, '\0'));
        }
        foreach (var df in new[] { -1, 1 })
        {
            var to = Step(from, df, dir);
            if (to < 0) continue;
            if (Enemy(_sq[to])) Add(to);
            else if (to == _ep) moves.Add(new(from, to, '\0'));
        }
    }

    private void Jumps(int from, (int, int)[] dirs, List<Move> moves)
    {
        foreach (var (df, dr) in dirs)
            if (Step(from, df, dr) is var to and >= 0 && !Own(_sq[to])) moves.Add(new(from, to, '\0'));
    }

    private void Slides(int from, (int, int)[] dirs, List<Move> moves)
    {
        foreach (var (df, dr) in dirs)
            for (var to = Step(from, df, dr); to >= 0; to = Step(to, df, dr))
            {
                if (Own(_sq[to])) break;
                moves.Add(new(from, to, '\0'));
                if (_sq[to] != '\0') break;
            }
    }

    private void Castles(int from, List<Move> moves)
    {
        var (home, k, q, rook) = WhiteToMove ? (4, 'K', 'Q', 'R') : (60, 'k', 'q', 'r');
        if (from != home || Attacked(home, !WhiteToMove)) return;
        if (_castling.Contains(k) && _sq[home + 3] == rook && _sq[home + 1] == '\0' && _sq[home + 2] == '\0'
            && !Attacked(home + 1, !WhiteToMove) && !Attacked(home + 2, !WhiteToMove))
            moves.Add(new(home, home + 2, '\0'));
        if (_castling.Contains(q) && _sq[home - 4] == rook && _sq[home - 1] == '\0' && _sq[home - 2] == '\0' && _sq[home - 3] == '\0'
            && !Attacked(home - 1, !WhiteToMove) && !Attacked(home - 2, !WhiteToMove))
            moves.Add(new(home, home - 2, '\0'));
    }

    private ChessBoard Clone()
    {
        var b = new ChessBoard { WhiteToMove = WhiteToMove, _castling = _castling, _ep = _ep, _half = _half, _full = _full };
        Array.Copy(_sq, b._sq, 64);
        return b;
    }

    /// <summary>Moves the pieces (without changing the side to move: see <see cref="Play"/>).</summary>
    private void Apply(Move m)
    {
        var p = _sq[m.From];
        var lower = char.ToLowerInvariant(p);
        var capture = _sq[m.To] != '\0';
        if (lower == 'p' && m.To == _ep && !capture) _sq[m.To + (WhiteToMove ? -8 : 8)] = '\0'; // en passant
        if (lower == 'k' && Math.Abs(m.To - m.From) == 2)
        {
            var (rf, rt) = m.To > m.From ? (m.From + 3, m.From + 1) : (m.From - 4, m.From - 1);
            _sq[rt] = _sq[rf]; _sq[rf] = '\0';
        }
        _sq[m.To] = m.Promo != '\0' ? m.Promo : p;
        _sq[m.From] = '\0';
        _ep = lower == 'p' && Math.Abs(m.To - m.From) == 16 ? (m.From + m.To) / 2 : -1;
        _half = lower == 'p' || capture ? 0 : _half + 1;
        foreach (var (sq, right) in new[] { (4, "KQ"), (0, "Q"), (7, "K"), (60, "kq"), (56, "q"), (63, "k") })
            if (m.From == sq || m.To == sq)
                foreach (var c in right) _castling = _castling.Replace(c.ToString(), "");
    }

    public void Play(Move m)
    {
        Apply(m);
        if (!WhiteToMove) _full++;
        WhiteToMove = !WhiteToMove;
    }

    /// <summary>The legal move a SAN string means (e.g. "Nbd7", "exd5", "O-O", "e8=Q+"); null when it is not legal.</summary>
    public Move? ParseSan(string san)
    {
        var s = SanClean().Replace(san, "");
        var legal = LegalMoves();
        if (s is "O-O" or "0-0" or "O-O-O" or "0-0-0")
        {
            var home = WhiteToMove ? 4 : 60;
            var to = s.Length > 3 ? home - 2 : home + 2;
            return legal.Where(m => m.From == home && m.To == to && char.ToLowerInvariant(_sq[home]) == 'k').Select(m => (Move?)m).FirstOrDefault();
        }
        var match = SanMove().Match(s);
        if (!match.Success) return null;
        var piece = match.Groups[1].Value is { Length: 1 } pg ? char.ToLowerInvariant(pg[0]) : 'p';
        var fromFile = match.Groups[2].Value;
        var fromRank = match.Groups[3].Value;
        var target = Square(match.Groups[4].Value);
        var promo = match.Groups[5].Value is { Length: 1 } pr ? char.ToLowerInvariant(pr[0]) : '\0';
        var found = legal.Where(m => m.To == target && char.ToLowerInvariant(_sq[m.From]) == piece
            && (fromFile.Length == 0 || Name(m.From)[0] == fromFile[0])
            && (fromRank.Length == 0 || Name(m.From)[1] == fromRank[0])
            && char.ToLowerInvariant(m.Promo) == promo).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    public Move? ParseUci(string uci)
    {
        if (uci.Length < 4) return null;
        var from = Square(uci[..2]);
        var to = Square(uci[2..4]);
        var promo = uci.Length > 4 ? uci[4] : '\0';
        foreach (var m in LegalMoves())
            if (m.From == from && m.To == to && char.ToLowerInvariant(m.Promo) == promo) return m;
        return null;
    }

    /// <summary>The SAN of a legal move in this position (for engine suggestions).</summary>
    public string San(Move m)
    {
        var p = char.ToLowerInvariant(_sq[m.From]);
        string san;
        if (p == 'k' && Math.Abs(m.To - m.From) == 2) san = m.To > m.From ? "O-O" : "O-O-O";
        else
        {
            var capture = _sq[m.To] != '\0' || (p == 'p' && m.To == _ep);
            var sb = new StringBuilder();
            if (p == 'p') { if (capture) sb.Append(Name(m.From)[0]); }
            else
            {
                sb.Append(char.ToUpperInvariant(p));
                var others = LegalMoves().Where(o => o.To == m.To && o.From != m.From && char.ToLowerInvariant(_sq[o.From]) == p).ToList();
                if (others.Count > 0)
                {
                    if (others.All(o => o.From % 8 != m.From % 8)) sb.Append(Name(m.From)[0]);
                    else if (others.All(o => o.From / 8 != m.From / 8)) sb.Append(Name(m.From)[1]);
                    else sb.Append(Name(m.From));
                }
            }
            if (capture) sb.Append('x');
            sb.Append(Name(m.To));
            if (m.Promo != '\0') sb.Append('=').Append(char.ToUpperInvariant(m.Promo));
            san = sb.ToString();
        }
        var next = Clone();
        next.Play(m);
        if (next.InCheck()) san += next.LegalMoves().Count == 0 ? "#" : "+";
        return san;
    }

    /// <summary>The SAN moves of a PGN's move text (headers, comments, variations, numbers and results removed).</summary>
    public static List<string> PgnMoves(string pgn)
    {
        var text = PgnHeader().Replace(pgn, " ");
        text = PgnComment().Replace(text, " ");
        string before;
        do { before = text; text = PgnVariation().Replace(text, " "); } while (text != before);
        return text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => PgnNumber().Replace(t, ""))
            .Where(t => t.Length > 0 && !t.StartsWith('$') && t is not ("1-0" or "0-1" or "1/2-1/2" or "*"))
            .ToList();
    }

    [GeneratedRegex(@"[+#!?]+$")] private static partial Regex SanClean();
    [GeneratedRegex(@"^([NBRQK])?([a-h])?([1-8])?x?([a-h][1-8])(?:=?([NBRQnbrq]))?$")] private static partial Regex SanMove();
    [GeneratedRegex(@"\[[^\]]*\]")] private static partial Regex PgnHeader();
    [GeneratedRegex(@"\{[^}]*\}|;[^\n]*")] private static partial Regex PgnComment();
    [GeneratedRegex(@"\([^()]*\)")] private static partial Regex PgnVariation();
    [GeneratedRegex(@"^\d+\.+")] private static partial Regex PgnNumber();
}
