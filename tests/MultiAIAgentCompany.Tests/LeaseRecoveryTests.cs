using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §23 —— 読めない lease.json から人間が復帰できる。</summary>
public sealed class LeaseRecoveryTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly LeaseStore _leases;
    private readonly CompanyScanner _scanner;

    public LeaseRecoveryTests()
    {
        _leases = new LeaseStore(_workspace.Paths, TimeProvider.System);
        _scanner = new CompanyScanner(
            _workspace.Paths, new TaskStore(_workspace.Paths, TimeProvider.System), _leases);
    }

    public void Dispose() => _workspace.Dispose();

    /// <summary>Actor 導入前の実物（2026-09-06 に実機で踏んだもの）。</summary>
    private const string OldFormat = """
        {
          "revision": 3,
          "holders": [
            {
              "kind": "Write",
              "departmentId": "implementation",
              "taskSlug": "task-1",
              "acquiredAt": "2026-09-06T07:47:03.138761+00:00",
              "expiresAt": "2026-09-06T08:31:23.772165+00:00"
            }
          ]
        }
        """;

    private async Task WriteLeaseAsync(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_workspace.Paths.Lease)!);
        await File.WriteAllTextAsync(_workspace.Paths.Lease, content);
    }

    [Fact]
    public async Task 起動時の走査が読めないleaseを未解決項目として返す()
    {
        // **一時エラーにしない**（§23-1）。dispatch のたびに失敗するだけだと、
        // 人間は次も押して次も失敗する。
        await WriteLeaseAsync(OldFormat);

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.NotNull(result.UnreadableLease);
        Assert.Contains("古い lease 形式", result.UnreadableLease!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 定期走査では出さない()
    {
        // 復旧の一覧を作るのは起動時だけ（§14-1 / §16-1）。
        await WriteLeaseAsync(OldFormat);

        var result = await _scanner.SyncAsync(CompanyScanKind.Periodic, CancellationToken.None);

        Assert.Null(result.UnreadableLease);
    }

    [Fact]
    public async Task 壊れたJSONと古い形式を区別して言う()
    {
        await WriteLeaseAsync("{");
        var broken = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Contains("JSON として壊れています", broken.UnreadableLease!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"holders\":[\"x\"]}")]
    [InlineData("\"ただの文字列\"")]
    public async Task JSONとしては読めるが形が違っても走査は落ちない(string content)
    {
        // **診断が復旧画面ごと落とさない**（レビューで発覚）。
        // TryGetProperty はオブジェクト以外で例外を投げる。
        await WriteLeaseAsync(content);

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.NotNull(result.UnreadableLease);
    }

    [Fact]
    public async Task 隔離すると元は残り空のleaseになる()
    {
        // **上書きも削除もしない**（§23-1）。
        await WriteLeaseAsync(OldFormat);

        var moved = await LeaseRecovery.IsolateAsync(
            _workspace.Paths, _leases, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.NotNull(moved);
        Assert.Equal(OldFormat, await File.ReadAllTextAsync(moved!));
        var read = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None));
        Assert.Empty(read.Leases.Holders);
    }

    [Fact]
    public async Task 読めるようになっていたら隔離しない()
    {
        // 押した時点の事実で動く（§14-2）。人間が手で直した直後かもしれない。
        Assert.Null(await LeaseRecovery.IsolateAsync(
            _workspace.Paths, _leases, DateTimeOffset.UnixEpoch, CancellationToken.None));
    }

    [Fact]
    public async Task 隔離のあとは仕事を渡せる()
    {
        // **これが直したかったこと。** 読めない lease で全部の dispatch が止まっていた。
        await WriteLeaseAsync(OldFormat);
        await LeaseRecovery.IsolateAsync(
            _workspace.Paths, _leases, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var read = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None));
        Assert.True(read.Leases.CanAcquire(LeaseKind.Write, DateTimeOffset.UtcNow));
    }
}
