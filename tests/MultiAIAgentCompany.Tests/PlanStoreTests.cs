using System.Text.Json;
using System.Text.Json.Nodes;
using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class PlanStoreTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TestClock _clock = new();
    private readonly PlanStore _store;

    public PlanStoreTests()
    {
        _store = new PlanStore(_workspace.Paths, _clock);
    }

    private Task<PlanWriteResult> CreateAsync(string id = "login") =>
        _store.CreateAsync(id, "ログイン画面を作る",
            [new PlanStep("design", "設計する"), new PlanStep("review", "設計を見る", 0)], CancellationToken.None);

    [Fact]
    public async Task 作って読むと往復する()
    {
        var created = Assert.IsType<PlanWriteResult.Written>(await CreateAsync()).Plan;
        var read = Assert.IsType<PlanReadResult.Found>(await _store.ReadAsync("login", CancellationToken.None)).Plan;

        Assert.Equal(created with { Steps = read.Steps }, read);
        Assert.Equal(created.Steps, read.Steps);
        Assert.Equal(1, read.Revision);
        Assert.Equal(0, read.Revisions);
        Assert.False(read.StoppedByHuman);
        Assert.Equal(_clock.Now, read.CreatedAt);
        Assert.Equal(_clock.Now, read.UpdatedAt);
        Assert.All(read.Steps, step => { Assert.Null(step.TaskSlug); Assert.Null(step.Verdict); });
    }

    [Fact]
    public async Task 片付けるとarchiveへ移り_一覧から消える()
    {
        await CreateAsync();

        var archived = Assert.IsType<PlanArchiveResult.Archived>(
            await _store.ArchiveAsync("login", CancellationToken.None));

        // **消さずに移す**（§16-4）。中身は読み返せる。
        Assert.True(Directory.Exists(archived.Path));
        Assert.True(File.Exists(Path.Combine(archived.Path, "plan.json")));
        Assert.Empty(await _store.ListIdsAsync(CancellationToken.None));
        Assert.IsType<PlanReadResult.Missing>(await _store.ReadAsync("login", CancellationToken.None));
    }

    [Fact]
    public async Task 無い計画を片付けても_黙って成功しない()
    {
        Assert.IsType<PlanArchiveResult.Missing>(await _store.ArchiveAsync("login", CancellationToken.None));
    }

    [Fact]
    public async Task 片付け先に同じ名前があれば_上書きしない()
    {
        await CreateAsync();
        Assert.IsType<PlanArchiveResult.Archived>(await _store.ArchiveAsync("login", CancellationToken.None));

        // 同じ ID の計画をもう一度作って片付けると、前に片付けた記録を消してしまう。
        await CreateAsync();
        var failed = Assert.IsType<PlanArchiveResult.Failed>(
            await _store.ArchiveAsync("login", CancellationToken.None));

        Assert.Contains("同じ名前", failed.Reason);
        Assert.Single(await _store.ListIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 更新はRevisionと時刻を保存口で入れる()
    {
        var created = Assert.IsType<PlanWriteResult.Written>(await CreateAsync()).Plan;
        _clock.Now = _clock.Now.AddMinutes(1);
        var next = created with
        {
            Revision = 99,
            Revisions = 1,
            StoppedByHuman = true,
            Steps = [created.Steps[0] with { TaskSlug = "design-1" },
                created.Steps[1] with { TaskSlug = "review-1", Verdict = ReviewVerdict.NeedsRevision }],
        };
        var written = Assert.IsType<PlanWriteResult.Written>(await _store.WriteAsync(created, next, CancellationToken.None)).Plan;
        var read = Assert.IsType<PlanReadResult.Found>(await _store.ReadAsync("login", CancellationToken.None)).Plan;

        Assert.Equal(next with { Revision = 2, UpdatedAt = _clock.Now }, written);
        Assert.Equal(written with { Steps = read.Steps }, read);
        Assert.Equal(written.Steps, read.Steps);
        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.PlanDirectory("login"), ".plan.json.*.tmp"));
        var json = await File.ReadAllTextAsync(_workspace.Paths.PlanFile("login"));
        Assert.Contains("ログイン画面を作る", json);
        Assert.Contains("\"NeedsRevision\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("steps")[1].TryGetProperty("isReview", out _));
        Assert.Equal(8, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task 既にある計画の作成はConflictedで上書きしない()
    {
        await CreateAsync();
        var before = await File.ReadAllTextAsync(_workspace.Paths.PlanFile("login"));

        Assert.IsType<PlanWriteResult.Conflicted>(await CreateAsync());
        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.PlanFile("login")));
    }

    [Fact]
    public async Task 古いexpectedはConflictedでマージしない()
    {
        var created = Assert.IsType<PlanWriteResult.Written>(await CreateAsync()).Plan;
        Assert.IsType<PlanWriteResult.Written>(await _store.WriteAsync(created, created with { Revisions = 1 }, CancellationToken.None));
        var before = await File.ReadAllTextAsync(_workspace.Paths.PlanFile("login"));

        Assert.IsType<PlanWriteResult.Conflicted>(await _store.WriteAsync(created, created with { StoppedByHuman = true }, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.PlanFile("login")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task 読めないJSONは無いことにせず更新も拒む(string content)
    {
        var created = Assert.IsType<PlanWriteResult.Written>(await CreateAsync()).Plan;
        await File.WriteAllTextAsync(_workspace.Paths.PlanFile("login"), content);

        Assert.IsType<PlanReadResult.Unreadable>(await _store.ReadAsync("login", CancellationToken.None));
        Assert.IsType<PlanWriteResult.Conflicted>(await _store.WriteAsync(created, created, CancellationToken.None));
        Assert.Equal(content, await File.ReadAllTextAsync(_workspace.Paths.PlanFile("login")));
    }

    [Theory]
    [InlineData("id", "other")]
    [InlineData("steps", null)]
    public async Task 保存内容の同一性や必須項目を検証する(string property, string? value)
    {
        await CreateAsync();
        var path = _workspace.Paths.PlanFile("login");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json[property] = value;
        await File.WriteAllTextAsync(path, json.ToJsonString());

        Assert.IsType<PlanReadResult.Unreadable>(await _store.ReadAsync("login", CancellationToken.None));
    }

    [Fact]
    public async Task plan_jsonがディレクトリでもMissingにしない()
    {
        Directory.CreateDirectory(_workspace.Paths.PlanFile("login"));

        Assert.IsType<PlanReadResult.Unreadable>(await _store.ReadAsync("login", CancellationToken.None));
    }

    [Fact]
    public async Task 存在しない計画はMissingになる()
    {
        Assert.IsType<PlanReadResult.Missing>(await _store.ReadAsync("login", CancellationToken.None));
        Assert.Empty(await _store.ListIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 不正なディレクトリを無視してID順に返す()
    {
        await CreateAsync("z-plan");
        await CreateAsync("a_plan");
        Directory.CreateDirectory(Path.Combine(_workspace.Paths.PlansRoot, "foo bar"));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Paths.PlansRoot, "not-a-directory"), "");

        Assert.Equal(["a_plan", "z-plan"], await _store.ListIdsAsync(CancellationToken.None));
    }

    [Fact]
    public void 計画のパスもslug検査を通す()
    {
        Assert.Equal(Path.Combine(_workspace.Path, ".company", "plans", "login", "plan.json"), _workspace.Paths.PlanFile("login"));
        Assert.Throws<ArgumentException>(() => _workspace.Paths.PlanDirectory("../outside"));
        Assert.Throws<ArgumentException>(() => _workspace.Paths.PlanFile("foo bar"));
    }

    public void Dispose() => _workspace.Dispose();

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
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
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public async Task 秘書の前提を共有文書に入れ_既にある文書は上書きしない()
    {
        // 設計 §62-4。
        var steps = new[] { new PlanStep("design", "設計する"), new PlanStep("review", "見る", 0) };
        Assert.IsType<PlanWriteResult.Written>(
            await _store.CreateAsync("plan-b", "目的", steps, CancellationToken.None, "- 対象外: 再設定"));

        var path = _workspace.Paths.Brief("plan-b");
        var brief = await File.ReadAllTextAsync(path);
        Assert.Contains("- 対象外: 再設定", brief);
        Assert.Contains("2. `review`（工程 1 を見る） —— 見る", brief);

        await File.AppendAllTextAsync(path, "追記");
        await PlanBrief.WriteInitialAsync(_workspace.Paths, "plan-b", "別の目的", steps, null, CancellationToken.None);
        Assert.EndsWith("追記", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("## 結果\n\nできた", null)]
    [InlineData("## 共有文書への追記\n\n\n## 次", null)]
    [InlineData("## 共有文書への追記\n- A\n### 小見出し\n- B\n## 次\n- C", "- A\n### 小見出し\n- B")]
    [InlineData("## 共有文書への追記\r\n\r\n- A\r\n", "- A")]
    public void 追記節は次の見出しまで(string report, string? expected) =>
        Assert.Equal(expected, PlanBrief.ExtractAddition(report));


    [Fact]
    public async Task 共有文書を書けなければ_計画も作らない()
    {
        // レビューで発覚。作ってしまうと、秘書が書いた前提がどこにも残らないまま工程が進む。
        Directory.CreateDirectory(_workspace.Paths.Brief("plan-c"));

        var result = await _store.CreateAsync(
            "plan-c", "目的", [new PlanStep("design", "設計する")], CancellationToken.None, "- 対象外: 再設定");

        Assert.IsType<PlanWriteResult.Rejected>(result);
        Assert.False(File.Exists(_workspace.Paths.PlanFile("plan-c")));
    }

}
