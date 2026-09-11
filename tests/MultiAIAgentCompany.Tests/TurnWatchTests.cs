using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §36。<b>境界はここで固定する</b>（§31-2 と同じ形）。</summary>
public sealed class TurnWatchTests
{
    private static readonly DateTimeOffset Since = new(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    [Fact]
    public void 走っている_turn_が無ければ見ない()
    {
        Assert.Null(TurnWatch.Of(new TurnActivity(false, null, 0), false, Deadline, Since.AddDays(3)));
    }

    [Fact]
    public void 積まれていても走っていなければ見ない()
    {
        // 走っていないのに積まれている状態は、次の周で流れる。**沈黙ではない。**
        Assert.Null(TurnWatch.Of(new TurnActivity(false, null, 5), false, Deadline, Since.AddDays(3)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 期限が無いか_0_以下なら見ない(int minutes)
    {
        var deadline = minutes is 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
        Assert.Null(TurnWatch.Of(Running(), false, deadline, Since.AddDays(3)));
        Assert.Null(TurnWatch.Of(Running(), false, null, Since.AddDays(3)));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void 期限内と期限ちょうどでは出さない(int minutes)
    {
        Assert.Null(TurnWatch.Of(Running(), false, Deadline, Since.AddMinutes(minutes)));
    }

    [Fact]
    public void 時計が巻き戻っても出さない()
    {
        Assert.Null(TurnWatch.Of(Running(), false, Deadline, Since.AddMinutes(-1)));
    }

    [Fact]
    public void 期限を超えたら書いた時刻と経過と待ち件数を返す()
    {
        var now = Since.Add(Deadline).AddTicks(1);
        var silence = Assert.IsType<TurnSilence>(TurnWatch.Of(Running(queued: 2), false, Deadline, now));

        Assert.Equal(Since, silence.Since);
        Assert.Equal(Deadline.Add(TimeSpan.FromTicks(1)), silence.Elapsed);
        Assert.Equal(2, silence.Queued);
    }

    [Fact]
    public void 積まれていなくても出す()
    {
        // **「送ったのに何も起きない」は、次が積まれていなくても成立する**（§7）。
        var silence = Assert.IsType<TurnSilence>(
            TurnWatch.Of(Running(queued: 0), false, Deadline, Since.AddHours(1)));

        Assert.Equal(0, silence.Queued);
    }

    [Fact]
    public void 人間の答えを待っている_turn_は沈黙ではない()
    {
        // **待たせているのはこちら**（§31-2 の `AwaitingAnswer` と同じ扱い）——
        // 承認を出したまま人間が押していないだけで、相手は止まっていない。
        Assert.Null(TurnWatch.Of(Running(), awaitingHuman: true, Deadline, Since.AddDays(3)));
    }

    private static TurnActivity Running(int queued = 0) => new(true, Since, queued);
}
