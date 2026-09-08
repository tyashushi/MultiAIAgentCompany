using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class DepartmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"multi-ai-agent-company-departments-{Guid.NewGuid():N}");
    private readonly CompanyPaths _paths;
    private readonly DepartmentStore _store;

    public DepartmentStoreTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new CompanyPaths(_root);
        _store = new DepartmentStore(_paths);
    }

    [Fact]
    public async Task 既定の部門集合は保存して読み直せる()
    {
        // **既定が検証を通らない、を作らない**（レビューで発覚、設計 §13-3 追記2）。
        // Antigravity は承認の往復が無いが、握りつぶしは denied_actions で検出できる。
        var defaults = DepartmentStore.CreateDefaultDepartments();

        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), defaults, CancellationToken.None));
        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));

        Assert.Equal(defaults, read.Definition.Departments);
    }

    [Fact]
    public async Task 保存して読むと往復する()
    {
        var saved = Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(new(0, []), Departments(), CancellationToken.None));
        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        // record の等値比較はコレクションを要素で見ない（配列と List は別物になる）。
        // 中身を比べる。
        Assert.Equal(saved.Definition.Revision, read.Definition.Revision);
        Assert.Equal(saved.Definition.Departments, read.Definition.Departments);
        Assert.Equal(1, read.Definition.Revision);
    }

    [Fact]
    public async Task 古いexpectedは拒否されファイルを変えない()
    {
        var first = Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(new(0, []), Departments(), CancellationToken.None));
        var second = Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(first.Definition, Departments("review"), CancellationToken.None));
        var before = await File.ReadAllTextAsync(_paths.Departments);
        Assert.IsType<DefinitionWriteResult.Rejected>(await _store.SaveAsync(first.Definition, Departments("testing"), CancellationToken.None));
        Assert.Equal(before, await File.ReadAllTextAsync(_paths.Departments));
        Assert.Equal(2, second.Definition.Revision);
    }

    [Fact]
    public async Task 無いファイルはMissingで壊れたJSONはUnreadable()
    {
        Assert.IsType<DefinitionReadResult.Missing>(await _store.ReadAsync(CancellationToken.None));
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.Departments, "{");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 重複IdはUnreadable()
    {
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.Departments,
            "{\"revision\":1,\"departments\":[{\"id\":\"same\",\"displayName\":\"設計\",\"responsibility\":\"x\",\"agent\":\"ClaudeCode\",\"mode\":\"Structured\"},{\"id\":\"same\",\"displayName\":\"実装\",\"responsibility\":\"x\",\"agent\":\"CodexCli\",\"mode\":\"Structured\"}]}");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Antigravityの_Structured_は握りつぶしを検出できるので保存できる()
    {
        // **2026-09-06 に規則を変えた**（設計 §13-3 追記2 / §22-4）。
        // 承認の往復は無いが、握りつぶしは `result.denied_actions` で検出できる ——
        // `status:"SUCCESS"` が嘘をつくときの唯一の手がかりがそれ（§14-4 の3層判定）。
        // 往復の有無だけで弾いていたので、既定を Structured に変えたあと
        // **既定の部門集合が保存できなくなっていた。**
        var departments = new[] { new DepartmentDefinition("research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.Structured) };

        Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(new(0, []), departments, CancellationToken.None));
    }

    [Fact]
    public async Task 日本語はエスケープされず欠けたフィールドは既定値で埋めない()
    {
        await _store.SaveAsync(new(0, []), Departments(), CancellationToken.None);
        var json = await File.ReadAllTextAsync(_paths.Departments);
        Assert.Contains("設計", json); Assert.DoesNotContain("\\u", json);
        await File.WriteAllTextAsync(_paths.Departments, "{\"revision\":1,\"departments\":[{\"id\":\"design\",\"displayName\":\"設計\",\"agent\":\"ClaudeCode\",\"mode\":\"Structured\"}]}");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void 既定の部門は能力の既定モードを使う()
    {
        var departments = DepartmentStore.CreateDefaultDepartments();
        Assert.Equal(7, departments.Count);
        Assert.All(departments, d => Assert.Equal(AgentCapabilities.For(d.Agent).DefaultDriveMode, d.Mode));
    }

    private static IReadOnlyList<DepartmentDefinition> Departments(string id = "design") =>
        [new(id, "設計", "設計する", AgentKind.ClaudeCode, DriveMode.Structured)];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    [Fact]
    public void 既定の部門はすべて構造化になる()
    {
        // 2026-09-06 に Antigravity の既定を Structured へ変えた（§5 / §13-3 追記2）。
        // これで v1 の部門はすべて構造化で動き、部門を動かすために PTY は要らない。
        // TUI セッションが入るまで、ここが Tui を含むと起動できない部門ができる。
        var departments = DepartmentStore.CreateDefaultDepartments();

        Assert.Equal(7, departments.Count);
        Assert.All(departments, department => Assert.Equal(DriveMode.Structured, department.Mode));
    }

    [Fact]
    public void 設計レビューだけが読むだけの部門()
    {
        // **書き込み権を取るかどうかが変わる**（設計 §29-1）。
        // 安全側の既定は「取る」なので、読むだけと宣言したものだけがここに出る。
        var readsOnly = DepartmentStore.CreateDefaultDepartments()
            .Where(department => department.ReadsOnly)
            .Select(department => department.Id)
            .ToArray();

        Assert.Equal(["design-review-consistency", "design-review-outside"], readsOnly);
    }

    [Fact]
    public void 設計レビューの2部門は違う問いを持つ()
    {
        // 同じ問いを2人に投げると、費用は2倍で発見はほとんど増えない（§29-2）。
        var paths = new CompanyPaths(_root);
        var consistency = CompanyInstruction.ComposeDesignReview(
            "docs/x.md", DesignReviewLens.Consistency, paths, "task-1");
        var outside = CompanyInstruction.ComposeDesignReview(
            "docs/x.md", DesignReviewLens.Outside, paths, "task-1");

        Assert.Contains("矛盾している", consistency, StringComparison.Ordinal);
        Assert.Contains("使う人が、何に困るか", outside, StringComparison.Ordinal);
        Assert.NotEqual(consistency, outside);

        // どちらも「作業ツリーは書き換えない」と言う（読むだけの部門なので）。
        Assert.All([consistency, outside],
            text => Assert.Contains("作業ツリーのファイルを書き換えない", text, StringComparison.Ordinal));

        // **報告まで禁じない**（レビューで発覚）。禁じると、忠実な CLI は report.md も
        // 書かずに正常終了し、仕事が Dispatched のまま止まる。
        Assert.All([consistency, outside],
            text => Assert.Contains("report.md", text, StringComparison.Ordinal));
    }

    [Fact]
    public void 危険モードは承認の往復を持たないCLIにだけ適用される()
    {
        // **聞ける相手には聞く**（設計 §3 / §30-4）。Claude Code と Codex CLI は
        // can_use_tool の往復を持つので、危険モードはそちらでは意味を持たない。
        var claude = new DepartmentDefinition(
            "design", "設計", "設計する", AgentKind.ClaudeCode, DriveMode.Structured,
            AutoApproveAllTools: true);
        var codex = new DepartmentDefinition(
            "implementation", "実装", "実装する", AgentKind.CodexCli, DriveMode.Structured,
            AutoApproveAllTools: true);
        var antigravity = new DepartmentDefinition(
            "research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.Structured,
            AutoApproveAllTools: true);

        // **宣言は残る**（人間が書いたものを消さない）が、**適用されない**。
        Assert.True(claude.AutoApproveAllTools);
        Assert.False(claude.RunsWithAllToolsApproved);
        Assert.False(codex.RunsWithAllToolsApproved);
        Assert.True(antigravity.RunsWithAllToolsApproved);
    }

    [Fact]
    public void 危険モードの既定はオフ()
    {
        var antigravity = new DepartmentDefinition(
            "research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.Structured);

        Assert.False(antigravity.AutoApproveAllTools);
        Assert.False(antigravity.RunsWithAllToolsApproved);

        // 既定部門にも仕込まない（設計 §30-4）。
        Assert.All(DepartmentStore.CreateDefaultDepartments(), d => Assert.False(d.AutoApproveAllTools));
    }

    [Fact]
    public async Task 危険モードはdepartments_jsonに残って読み戻せる()
    {
        // 人間が手で書く場所（設計 §15-8）。**読み書きで落ちると「設定したのに効かない」になる。**
        var dangerous = new DepartmentDefinition(
            "research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.Structured,
            AutoApproveAllTools: true);

        var written = Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), [dangerous], CancellationToken.None));
        Assert.True(Assert.Single(written.Definition.Departments).AutoApproveAllTools);

        var back = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        var department = Assert.Single(back.Definition.Departments);
        Assert.True(department.AutoApproveAllTools);
        Assert.True(department.RunsWithAllToolsApproved);

        var json = await File.ReadAllTextAsync(_paths.Departments);

        // 画面のログでこの名前を人間に案内しているので、綴りが変わったら気付けること。
        Assert.Contains("autoApproveAllTools", json);

        // **計算値を書き出さない**（実機で発覚、2026-09-08）。読み戻されないので、
        // 人間がそちらを true にすると「設定したのに黙って無視される」になる。
        Assert.DoesNotContain("runsWithAllToolsApproved", json);
        Assert.DoesNotContain("dangerousModeApplies", json);
    }
}
