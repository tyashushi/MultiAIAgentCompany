using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 実プロセスの後始末。設計 §9 —— <b>アプリ終了後に孤児と zombie を残さない。</b>
/// </summary>
/// <remarks>
/// CLI は使わない（<c>/bin/sh</c>、Windows では <c>ping</c> だけ）ので、普段のテスト実行に混ぜてよい。
/// </remarks>
public sealed class ChildProcessChannelTests
{
    [Fact]
    public async Task 破棄すると子プロセスが残らない()
    {
        // Process オブジェクトを捨てるだけでは子は生き残る。
        // 起動直後の失敗（握手のエラー、キャンセル）でこの経路に来るので、ここで殺せないと孤児になる。
        var channel = await StartSleepingAsync();
        var pid = channel.Identity.Pid;

        await channel.DisposeAsync();

        Assert.False(IsAlive(pid));
    }

    [Fact]
    public async Task 破棄を二回呼んでも壊れない()
    {
        var channel = await StartSleepingAsync();

        await channel.DisposeAsync();
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task 子のUTF8の出力を化けずに読める()
    {
        // Windows で cp932 のコンソールから起動すると、指定なしでは stdout を cp932 で読んでいた
        // （2026-09-18、秘書の turn の終わりを読めなかった）。子はファイルのバイトをそのまま出す。
        const string line = "{\"text\":\"実装部門に任せて。\\n直下の hello.py\"}";
        var path = Path.Combine(Path.GetTempPath(), $"maac-utf8-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, line + "\n", ProcessEncoding.Utf8);
        try
        {
            await using var channel = OperatingSystem.IsWindows()
                ? await ChildProcessChannel.StartAsync("cmd.exe", ["/c", "type", path], Path.GetTempPath(), ct: CancellationToken.None)
                : await ChildProcessChannel.StartAsync("/bin/cat", [path], Path.GetTempPath(), ct: CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var read in channel.ReadLinesAsync(timeout.Token))
            {
                Assert.Equal(line, read);
                return;
            }
            Assert.Fail("子が何も出さなかった");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>120 秒眠るだけの子。Windows には <c>sleep</c> が無いので <c>ping</c> で待つ。</summary>
    private static Task<IAgentProcessChannel> StartSleepingAsync() => OperatingSystem.IsWindows()
        ? ChildProcessChannel.StartAsync("ping.exe", ["-n", "120", "127.0.0.1"], Path.GetTempPath(), ct: CancellationToken.None)
        : ChildProcessChannel.StartAsync("/bin/sh", ["-c", "sleep 120"], Path.GetTempPath(), ct: CancellationToken.None);

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
