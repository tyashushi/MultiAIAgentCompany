using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class TurnGateTests
{
    [Fact]
    public async Task 走っているturnが無ければその場で書く()
    {
        var written = new List<string>();
        var gate = new TurnGate((text, _) => { written.Add(text); return Task.CompletedTask; });

        var outcome = await gate.SendAsync("1通目", CancellationToken.None);

        Assert.True(outcome.Written);
        Assert.Equal(["1通目"], written);
        Assert.True(gate.InFlight);
    }

    [Fact]
    public async Task 走っている間は書かずに積む()
    {
        // **これが §32-12 の本体。** 前の turn がツールを実行している最中に
        // 次を書くと、会話の並びが壊れる。
        var written = new List<string>();
        var gate = new TurnGate((text, _) => { written.Add(text); return Task.CompletedTask; });

        await gate.SendAsync("1通目", CancellationToken.None);
        var second = await gate.SendAsync("2通目", CancellationToken.None);

        Assert.False(second.Written);
        Assert.Equal(1, second.Queued);
        Assert.Equal(["1通目"], written);
    }

    [Fact]
    public async Task turnが終わったら次の1つだけを書く()
    {
        var written = new List<string>();
        var gate = new TurnGate((text, _) => { written.Add(text); return Task.CompletedTask; });

        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.SendAsync("2通目", CancellationToken.None);
        await gate.SendAsync("3通目", CancellationToken.None);

        await gate.OnTurnFinishedAsync(CancellationToken.None);

        // **1つずつ。** まとめて流すと、また割り込みになる。
        Assert.Equal(["1通目", "2通目"], written);
        Assert.Equal(1, gate.Queued);
    }

    [Fact]
    public async Task 積むものが無ければturnの終わりで空く()
    {
        var gate = new TurnGate((_, _) => Task.CompletedTask);

        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.OnTurnFinishedAsync(CancellationToken.None);

        Assert.False(gate.InFlight);
    }

    [Fact]
    public async Task 書けなかったら走っていることにしない()
    {
        // ここを開けておかないと、以後の送信が永久に積まれるだけになる。
        var gate = new TurnGate((_, _) => throw new IOException("pipe failed"));

        await Assert.ThrowsAsync<IOException>(
            () => gate.SendAsync("1通目", CancellationToken.None));

        Assert.False(gate.InFlight);
    }

    [Fact]
    public async Task 閉じたら積んであったものを返す()
    {
        // **黙って捨てない**（§25-2）。人間に「送られなかった」と言えるようにする。
        var gate = new TurnGate((_, _) => Task.CompletedTask);

        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.SendAsync("2通目", CancellationToken.None);
        await gate.SendAsync("3通目", CancellationToken.None);

        Assert.Equal(["2通目", "3通目"], gate.Close());
        Assert.Equal(0, gate.Queued);
    }

    [Fact]
    public async Task 閉じたあとは送れない()
    {
        var gate = new TurnGate((_, _) => Task.CompletedTask);
        gate.Close();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => gate.SendAsync("1通目", CancellationToken.None));
    }

    [Fact]
    public async Task 沈黙では開かない()
    {
        // **§7 の「沈黙から推定しない」。** 無音で開けると、走っている turn の
        // 最中に次を書くことになる —— それは直そうとしている形そのもの。
        var gate = new TurnGate((_, _) => Task.CompletedTask);
        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.SendAsync("2通目", CancellationToken.None);

        await Task.Delay(50);

        // 時間が経っても、turn の終わりを観測するまでは開かない。
        Assert.True(gate.InFlight);
        Assert.Equal(1, gate.Queued);
    }

    [Fact]
    public async Task 積んであるものを追い越さない()
    {
        // **直列化のためにある型が、順番を壊してはいけない**（レビューで発覚）。
        var written = new List<string>();
        var gate = new TurnGate((text, _) => { written.Add(text); return Task.CompletedTask; });

        await gate.SendAsync("1通目", CancellationToken.None);
        await gate.SendAsync("2通目", CancellationToken.None);
        await gate.OnTurnFinishedAsync(CancellationToken.None);   // 2通目が出る

        // ここで空いているが、まだ何も積まれていない。次は素直に出てよい。
        Assert.Equal(["1通目", "2通目"], written);
    }

    [Fact]
    public async Task 書けなかったあとも積んであるものを流す()
    {
        // **失敗した turn は OnTurnFinishedAsync を呼ばれない**ので、
        // ここで流さないと待ち行列が永久に動かない（レビューで発覚）。
        var written = new List<string>();
        var fail = true;
        var gate = new TurnGate((text, _) =>
        {
            if (fail) { fail = false; throw new IOException("pipe failed"); }
            written.Add(text);
            return Task.CompletedTask;
        });

        var first = gate.SendAsync("1通目", CancellationToken.None);

        // 1通目が失敗する前に2通目を積む余地が無いので、順に確かめる。
        await Assert.ThrowsAsync<IOException>(() => first);

        var second = await gate.SendAsync("2通目", CancellationToken.None);

        Assert.True(second.Written);
        Assert.Equal(["2通目"], written);
    }
}
