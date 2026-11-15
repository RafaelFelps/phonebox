using PhoneBox.Agent;
using Xunit;

/// <summary>The chess review's move engine: legal moves (perft), SAN in and out, castling, en passant, promotion, PGN.</summary>
public sealed class ChessBoardTests
{
    private static long Perft(ChessBoard b, int depth)
    {
        if (depth == 0) return 1;
        long n = 0;
        foreach (var m in b.LegalMoves())
        {
            var c = ChessBoard.FromFen(b.Fen());
            c.Play(m);
            n += Perft(c, depth - 1);
        }
        return n;
    }

    [Theory]
    [InlineData(ChessBoard.StartFen, 1, 20)]
    [InlineData(ChessBoard.StartFen, 2, 400)]
    [InlineData(ChessBoard.StartFen, 3, 8902)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 1, 48)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2, 2039)]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 3, 2812)]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 2, 264)]
    public void Perft_matches_known_counts(string fen, int depth, long expected) =>
        Assert.Equal(expected, Perft(ChessBoard.FromFen(fen), depth));

    [Fact]
    public void Fen_round_trips()
    {
        const string fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        Assert.Equal(fen, ChessBoard.FromFen(fen).Fen());
    }

    [Fact]
    public void San_parses_and_prints_castling_en_passant_and_promotion()
    {
        var b = ChessBoard.FromFen("r3k2r/8/8/3pP3/8/8/1p6/R3K2R w KQkq d6 0 1");
        var ep = b.ParseSan("exd6");
        Assert.NotNull(ep);
        Assert.Equal("e5d6", ep!.Value.Uci);
        Assert.Null(b.ParseSan("O-O-O")); // the b2 pawn attacks c1
        var castle = b.ParseSan("O-O");
        Assert.Equal("e1g1", castle!.Value.Uci);
        Assert.Equal("O-O", b.San(castle.Value));
        b.Play(castle.Value);
        Assert.StartsWith("r3k2r/8/8/3pP3/8/8/1p6/R4RK1 b kq", b.Fen());
        var promo = b.ParseSan("b1=Q+");
        Assert.NotNull(promo);
        Assert.Equal("b2b1q", promo!.Value.Uci);
        Assert.Equal("b1=Q", b.San(promo.Value));
    }

    [Fact]
    public void San_disambiguates_and_marks_mate()
    {
        var b = ChessBoard.FromFen("6k1/5ppp/8/8/8/8/8/R5RK w - - 0 1");
        var m = b.ParseSan("Rgb1");
        Assert.Equal("g1b1", m!.Value.Uci);
        Assert.Equal("Rgb1", b.San(m.Value));
        var mate = ChessBoard.FromFen("6k1/5ppp/8/8/8/8/8/R6K w - - 0 1");
        Assert.Equal("Ra8#", mate.San(mate.ParseSan("Ra8")!.Value));
        Assert.Null(b.ParseSan("Qd4"));
    }

    [Fact]
    public void Pgn_moves_skip_headers_comments_variations_and_results()
    {
        const string pgn = "[Event \"Live Chess\"]\n[White \"a\"]\n\n1. f3 {[%clk 0:03:00]} e5 (1... e6 2. g4) 2. g4?? Qh4# 0-1";
        var moves = ChessBoard.PgnMoves(pgn);
        Assert.Equal(["f3", "e5", "g4??", "Qh4#"], moves);
        var b = ChessBoard.FromFen(ChessBoard.StartFen);
        foreach (var san in moves) b.Play(b.ParseSan(san)!.Value);
        Assert.True(b.InCheck());
        Assert.Empty(b.LegalMoves());
    }
}
