using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §36 —— <see cref="TurnGate"/> が「いつ書いたか」を持つこと。</summary>
public sealed class TurnGateActivityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 書いていなければ走っていない()
    {
        var gate = new TurnGate((_, _) => Task.CompletedTask, new StepClock(Start));

        Assert.Equal(new TurnActivity(false, null, 0), gate.Activity);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 書いた時刻を持ち_積まれた数も返す()
    {
        var clock = new StepClock(Start);
        var gate = new TurnGate((_, _) => Task.CompletedTask, clock);

        await gate.SendAsync("1通目", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(20));
        await gate.SendAsync("2通目", CancellationToken.None);

        var activity = gate.Activity;
        Assert.True(activity.InFlight);

        // **2通目を積んでも、測る起点は1通目を書いた時刻のまま。**
        Assert.Equal(Start, activity.Since);
        Assert.Equal(1, activity.Queued);
    }

    [Fact]
    public async Task 次が流れたら起点も進む()
    {
        var clock = new StepClock(Start);
        var gate = new TurnGate((_, _) => Task.CompletedTask, clock);

        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.SendAsync("2通目", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(20));
        await gate.OnTurnFinishedAsync(CancellationToken.None);

        var activity = gate.Activity;
        Assert.True(activity.InFlight);
        Assert.Equal(Start.AddMinutes(20), activity.Since);
        Assert.Equal(0, activity.Queued);
    }

    [Fact]
    public async Task turn_が終わって空なら走っていない()
    {
        var gate = new TurnGate((_, _) => Task.CompletedTask, new StepClock(Start));

        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.OnTurnFinishedAsync(CancellationToken.None);

        Assert.Equal(new TurnActivity(false, null, 0), gate.Activity);
    }

    /// <summary>進められる時計。<b>他のテストにある形と揃える</b>（新しい依存を足さない）。</summary>
    private sealed class StepClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
