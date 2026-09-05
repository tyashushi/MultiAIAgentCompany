using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 実プロセスの後始末。設計 §9 —— <b>アプリ終了後に孤児と zombie を残さない。</b>
/// </summary>
/// <remarks>
/// CLI は使わない（<c>/bin/sh</c> だけ）ので、普段のテスト実行に混ぜてよい。
/// </remarks>
public sealed class ChildProcessChannelTests
{
    [Fact]
    public async Task 破棄すると子プロセスが残らない()
    {
        // Process オブジェクトを捨てるだけでは子は生き残る。
        // 起動直後の失敗（握手のエラー、キャンセル）でこの経路に来るので、ここで殺せないと孤児になる。
        var channel = await ChildProcessChannel.StartAsync(
            "/bin/sh", ["-c", "sleep 120"], Path.GetTempPath(), ct: CancellationToken.None);
        var pid = channel.Identity.Pid;

        await channel.DisposeAsync();

        Assert.False(IsAlive(pid));
    }

    [Fact]
    public async Task 破棄を二回呼んでも壊れない()
    {
        var channel = await ChildProcessChannel.StartAsync(
            "/bin/sh", ["-c", "sleep 120"], Path.GetTempPath(), ct: CancellationToken.None);

        await channel.DisposeAsync();
        await channel.DisposeAsync();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
