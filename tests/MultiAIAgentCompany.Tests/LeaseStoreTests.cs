using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class LeaseStoreTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
    private readonly LeaseStore _store;

    public LeaseStoreTests()
    {
        _store = new LeaseStore(_workspace.Paths, _clock);
    }

    [Fact]
    public async Task ファイルが無いときは空の権利表として読める()
    {
        var result = Assert.IsType<LeaseReadResult.Found>(await _store.ReadAsync(CancellationToken.None));

        Assert.Equal(0, result.Leases.Revision);
        Assert.Empty(result.Leases.Holders);
    }

    [Fact]
    public async Task 取得するとRevisionが1になり保持している()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");

        Assert.Equal(1, acquired.Leases.Revision);
        Assert.True(acquired.Leases.IsHeldBy(LeaseKind.Write, Actor.OfDepartment("implementation"), _clock.GetUtcNow()));
    }

    [Fact]
    public void 同じIDの部門と秘書は別の行為者である()
    {
        Assert.NotEqual(Actor.OfDepartment("secretary"), Actor.TheSecretary);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void 空白だけの部門IDは行為者にできない(string departmentId)
    {
        Assert.Throws<ArgumentException>(() => Actor.OfDepartment(departmentId));
    }

    [Fact]
    public async Task 有効な保持者がいる権利を別部門が取るとDeniedでファイルを変更しない()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        var before = await File.ReadAllTextAsync(_workspace.Paths.Lease);

        Assert.IsType<LeaseWriteResult.Denied>(await _store.AcquireAsync(acquired.Leases, LeaseKind.Write,
            Actor.OfDepartment("review"), "review", TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.Lease));
    }

    [Fact]
    public async Task 秘書がWriteleaseを持つ間は部門は取れない()
    {
        var acquired = Assert.IsType<LeaseWriteResult.Written>(await _store.AcquireAsync(
            WorkspaceLeases.Empty, LeaseKind.Write, Actor.TheSecretary, "commit", TimeSpan.FromMinutes(10),
            LeaseTakeover.Deny, CancellationToken.None));

        Assert.IsType<LeaseWriteResult.Denied>(await _store.AcquireAsync(
            acquired.Leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "feature",
            TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None));
    }

    [Fact]
    public async Task 部門がWriteleaseを持つ間は秘書は取れない()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");

        Assert.IsType<LeaseWriteResult.Denied>(await _store.AcquireAsync(
            acquired.Leases, LeaseKind.Write, Actor.TheSecretary, "commit", TimeSpan.FromMinutes(10),
            LeaseTakeover.Deny, CancellationToken.None));
    }

    [Fact]
    public async Task WriteとUnityは独立している()
    {
        var write = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        var unity = await AcquireAsync(write.Leases, LeaseKind.Unity, "unity", "scene");

        Assert.True(unity.Leases.IsHeldBy(LeaseKind.Write, Actor.OfDepartment("implementation"), _clock.GetUtcNow()));
        Assert.True(unity.Leases.IsHeldBy(LeaseKind.Unity, Actor.OfDepartment("unity"), _clock.GetUtcNow()));
    }

    [Fact]
    public async Task 失効した保持者がいても既定ではDeniedExpiredでファイルを変更しない()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature", TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));
        var before = await File.ReadAllTextAsync(_workspace.Paths.Lease);

        var denied = Assert.IsType<LeaseWriteResult.DeniedExpired>(await _store.AcquireAsync(acquired.Leases, LeaseKind.Write,
            Actor.OfDepartment("review"), "review", TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None));

        Assert.Contains("失効", denied.Reason);
        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.Lease));
    }

    [Fact]
    public async Task AllowExpiredを渡すと前の保持者を置き換えられる()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature", TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));

        var taken = await AcquireAsync(acquired.Leases, LeaseKind.Write, "review", "review", TimeSpan.FromMinutes(10), LeaseTakeover.AllowExpired);

        Assert.Equal(Actor.OfDepartment("review"), taken.Leases.Holders[LeaseKind.Write].Holder);
        Assert.Equal(2, taken.Leases.Revision);
    }

    [Fact]
    public async Task 保持していない部門のReleaseはNotHeldでファイルを変更しない()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        var before = await File.ReadAllTextAsync(_workspace.Paths.Lease);

        Assert.IsType<LeaseWriteResult.NotHeld>(await _store.ReleaseAsync(acquired.Leases, LeaseKind.Write, Actor.OfDepartment("review"), CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.Lease));
    }

    [Fact]
    public async Task 保持者が解放すると権利が空く()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        var released = Assert.IsType<LeaseWriteResult.Written>(await _store.ReleaseAsync(acquired.Leases, LeaseKind.Write, Actor.OfDepartment("implementation"), CancellationToken.None));

        Assert.False(released.Leases.Holders.ContainsKey(LeaseKind.Write));
    }

    [Fact]
    public async Task RenewでExpiresAtが伸びAcquiredAtと保持者は変わらない()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature", TimeSpan.FromMinutes(5));
        var original = acquired.Leases.Holders[LeaseKind.Write];
        _clock.Advance(TimeSpan.FromMinutes(1));

        var renewed = Assert.IsType<LeaseWriteResult.Written>(await _store.RenewAsync(acquired.Leases, LeaseKind.Write,
            Actor.OfDepartment("implementation"), TimeSpan.FromMinutes(10), CancellationToken.None));
        var holder = renewed.Leases.Holders[LeaseKind.Write];

        Assert.Equal(original.AcquiredAt, holder.AcquiredAt);
        Assert.Equal(original.Holder, holder.Holder);
        Assert.Equal(original.TaskSlug, holder.TaskSlug);
        Assert.True(holder.ExpiresAt > original.ExpiresAt);
    }

    [Fact]
    public async Task 保持していない部門のRenewはNotHeld()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");

        Assert.IsType<LeaseWriteResult.NotHeld>(await _store.RenewAsync(acquired.Leases, LeaseKind.Write,
            Actor.OfDepartment("review"), TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    [Fact]
    public async Task 古いexpectedはConflictedでファイルを変更しない()
    {
        var acquired = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        var before = await File.ReadAllTextAsync(_workspace.Paths.Lease);

        Assert.IsType<LeaseWriteResult.Conflicted>(await _store.AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Unity,
            Actor.OfDepartment("unity"), "scene", TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.Lease));
        Assert.Equal(1, acquired.Leases.Revision);
    }

    [Fact]
    public async Task 壊れたlease_jsonは例外ではなくUnreadableになる()
    {
        Directory.CreateDirectory(_workspace.Paths.Root);
        await File.WriteAllTextAsync(_workspace.Paths.Lease, "{");

        Assert.IsType<LeaseReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 同じkindの保持者が二つあるlease_jsonはUnreadableになる()
    {
        await WriteRawAsync("""
            {"revision":1,"holders":[
            {"kind":"Write","holder":{"kind":"Department","id":"a"},"taskSlug":"a","acquiredAt":"2026-09-06T00:00:00+00:00","expiresAt":"2026-09-06T01:00:00+00:00"},
            {"kind":"Write","holder":{"kind":"Department","id":"b"},"taskSlug":"b","acquiredAt":"2026-09-06T00:00:00+00:00","expiresAt":"2026-09-06T01:00:00+00:00"}]}
            """);

        Assert.IsType<LeaseReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task フィールドが欠けたlease_jsonは既定値で埋めずUnreadableになる()
    {
        await WriteRawAsync("""
            {"revision":1,"holders":[{"kind":"Write","holder":{"kind":"Department","id":"a"},"taskSlug":"a","acquiredAt":"2026-09-06T00:00:00+00:00"}]}
            """);

        Assert.IsType<LeaseReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 古いdepartmentId形式のlease_jsonはUnreadableになる()
    {
        await WriteRawAsync("""
            {"revision":1,"holders":[{"kind":"Write","departmentId":"implementation","taskSlug":"feature","acquiredAt":"2026-09-06T00:00:00+00:00","expiresAt":"2026-09-06T01:00:00+00:00"}]}
            """);

        Assert.IsType<LeaseReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 書いたあと一時ファイルは残らない()
    {
        await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");

        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.Root, ".lease.json.*.tmp"));
    }

    [Fact]
    public async Task 日本語の部門名がエスケープされない()
    {
        // 既定では "実装" が "\u5B9F\u88C5" になる。それでは人間が手で直せるファイルではない。
        var store = new LeaseStore(_workspace.Paths, _clock);
        var empty = Assert.IsType<LeaseReadResult.Found>(await store.ReadAsync(CancellationToken.None)).Leases;
        await store.AcquireAsync(empty, LeaseKind.Write, Actor.OfDepartment("実装"), "add-login", TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        var json = await File.ReadAllTextAsync(_workspace.Paths.Lease);

        Assert.Contains("実装", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public async Task lease_jsonの列挙は数値でなく名前で書かれる()
    {
        var write = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        await AcquireAsync(write.Leases, LeaseKind.Unity, "unity", "scene");

        var json = await File.ReadAllTextAsync(_workspace.Paths.Lease);
        Assert.Contains("\"Write\"", json);
        Assert.Contains("\"Unity\"", json);
    }

    [Fact]
    public async Task lease_jsonの行為者種別は数値でなく名前で書かれる()
    {
        var department = await AcquireAsync(WorkspaceLeases.Empty, LeaseKind.Write, "implementation", "feature");
        await _store.AcquireAsync(department.Leases, LeaseKind.Unity, Actor.TheSecretary, "commit",
            TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        var json = await File.ReadAllTextAsync(_workspace.Paths.Lease);

        Assert.Contains("\"Department\"", json);
        Assert.Contains("\"Secretary\"", json);
    }

    private async Task<LeaseWriteResult.Written> AcquireAsync(WorkspaceLeases expected, LeaseKind kind, string departmentId,
        string slug, TimeSpan? duration = null, LeaseTakeover takeover = LeaseTakeover.Deny) =>
        Assert.IsType<LeaseWriteResult.Written>(await _store.AcquireAsync(expected, kind, Actor.OfDepartment(departmentId), slug,
            duration ?? TimeSpan.FromMinutes(10), takeover, CancellationToken.None));

    private async Task WriteRawAsync(string json)
    {
        Directory.CreateDirectory(_workspace.Paths.Root);
        await File.WriteAllTextAsync(_workspace.Paths.Lease, json);
    }

    public void Dispose() => _workspace.Dispose();

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"multi-ai-agent-company-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Paths = new CompanyPaths(Path);
        }

        public string Path { get; }
        public CompanyPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
    [Fact]
    public void 照合できない行為者を作れない()
    {
        // lease.json に書かれてしまうと、本来の持ち主が更新も解放もできなくなる（§17-2）。
        Assert.Throws<ArgumentException>(() => new Actor(ActorKind.Department, "  "));
        Assert.Throws<ArgumentException>(() => new Actor(ActorKind.Secretary, "other"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Actor((ActorKind)123, "x"));
    }

    [Fact]
    public void 秘書を名乗る部門は秘書ではない()
    {
        // 予約 ID を部門が名乗っても、種類が違えば別物（§17-2）。
        Assert.NotEqual(Actor.TheSecretary, Actor.OfDepartment(Actor.SecretaryId));
    }

}
