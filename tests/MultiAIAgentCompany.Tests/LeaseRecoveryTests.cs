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

    [Fact]
    public async Task 失効した保持者は起動時の未解決項目になる()
    {
        // **待っても空かない**（§24）。dispatch のたびに弾かれるだけの状態が永久に続く。
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        Assert.IsType<LeaseWriteResult.Written>(await _leases.AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(1), LeaseTakeover.Deny, CancellationToken.None));

        var clock = new StepClock(DateTimeOffset.UtcNow.AddMinutes(5));
        var scanner = new CompanyScanner(
            _workspace.Paths, new TaskStore(_workspace.Paths, clock), new LeaseStore(_workspace.Paths, clock), clock);

        var result = await scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal("implementation", Assert.IsType<LeaseHolder>(result.ExpiredWriteLease).Holder.Id);
    }

    [Fact]
    public async Task 失効した保持者は人間が外せる()
    {
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        var held = Assert.IsType<LeaseWriteResult.Written>(await _leases.AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(1), LeaseTakeover.Deny, CancellationToken.None));

        var later = new LeaseStore(_workspace.Paths, new StepClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var released = Assert.IsType<LeaseWriteResult.Written>(
            await later.ReleaseExpiredAsync(
                held.Leases, LeaseKind.Write, held.Leases.Holders[LeaseKind.Write], CancellationToken.None));

        Assert.Empty(released.Leases.Holders);
    }

    [Fact]
    public async Task 有効な保持者は外さない()
    {
        // 外すのは失効したものだけ。有効なものを外すのは奪取であって、この操作ではない（§14-2）。
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        var held = Assert.IsType<LeaseWriteResult.Written>(await _leases.AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(30), LeaseTakeover.Deny, CancellationToken.None));

        var result = Assert.IsType<LeaseWriteResult.Denied>(
            await _leases.ReleaseExpiredAsync(
                held.Leases, LeaseKind.Write, held.Leases.Holders[LeaseKind.Write], CancellationToken.None));

        Assert.Contains("まだ有効", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 見せた保持者と違うものは外さない()
    {
        // **人間が判断したのは画面に出ていた保持者**（§24-2）。
        // 出してから押すまでに lease.json が変わっていたら、別の保持者を外すことになる。
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        var held = Assert.IsType<LeaseWriteResult.Written>(await _leases.AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(1), LeaseTakeover.Deny, CancellationToken.None));
        var judged = held.Leases.Holders[LeaseKind.Write] with { Holder = Actor.OfDepartment("review") };

        var later = new LeaseStore(_workspace.Paths, new StepClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var result = Assert.IsType<LeaseWriteResult.Conflicted>(
            await later.ReleaseExpiredAsync(held.Leases, LeaseKind.Write, judged, CancellationToken.None));

        Assert.Contains("表示していた保持者と違います", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取得時刻が未来のleaseは未解決項目にしない()
    {
        // **出せば押せる、を守る**（§15-6）。外せないものを「外してください」と出さない。
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        var future = new StepClock(DateTimeOffset.UtcNow.AddHours(1));
        await new LeaseStore(_workspace.Paths, future).AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(30), LeaseTakeover.Deny, CancellationToken.None);

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Null(result.ExpiredWriteLease);
    }

    [Fact]
    public async Task 取得時刻が未来のleaseは外さない()
    {
        // **「有効でない」と「失効した」は違う**（§24-2）。時計のずれや手編集で
        // 取得時刻が未来になっていると `IsValidAt` は false になるが、まだ始まっていない。
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        var future = new StepClock(DateTimeOffset.UtcNow.AddHours(1));
        var held = Assert.IsType<LeaseWriteResult.Written>(await new LeaseStore(_workspace.Paths, future).AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(30), LeaseTakeover.Deny, CancellationToken.None));

        var result = Assert.IsType<LeaseWriteResult.Denied>(await _leases.ReleaseExpiredAsync(
            held.Leases, LeaseKind.Write, held.Leases.Holders[LeaseKind.Write], CancellationToken.None));

        Assert.Contains("まだ失効していません", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未来のleaseはAllowExpiredでも奪えない()
    {
        // AllowExpired は「失効したものを置き換えてよい」であって、
        // まだ始まっていないものを奪ってよい、ではない（§24-2）。
        var leases = Assert.IsType<LeaseReadResult.Found>(
            await _leases.ReadAsync(CancellationToken.None)).Leases;
        var future = new StepClock(DateTimeOffset.UtcNow.AddHours(1));
        var held = Assert.IsType<LeaseWriteResult.Written>(await new LeaseStore(_workspace.Paths, future).AcquireAsync(
            leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "task-1",
            TimeSpan.FromMinutes(30), LeaseTakeover.Deny, CancellationToken.None));

        var result = Assert.IsType<LeaseWriteResult.Denied>(await _leases.AcquireAsync(
            held.Leases, LeaseKind.Write, Actor.OfDepartment("review"), "task-2",
            TimeSpan.FromMinutes(30), LeaseTakeover.AllowExpired, CancellationToken.None));

        Assert.Contains("未来", result.Reason, StringComparison.Ordinal);
    }

    private sealed class StepClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
