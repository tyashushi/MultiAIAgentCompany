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
    public void 既定の5部門は能力の既定モードを使う()
    {
        var departments = DepartmentStore.CreateDefaultDepartments();
        Assert.Equal(5, departments.Count);
        Assert.All(departments, d => Assert.Equal(AgentCapabilities.For(d.Agent).DefaultDriveMode, d.Mode));
    }

    private static IReadOnlyList<DepartmentDefinition> Departments(string id = "design") =>
        [new(id, "設計", "設計する", AgentKind.ClaudeCode, DriveMode.Structured)];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    [Fact]
    public void 既定の5部門はすべて構造化になる()
    {
        // 2026-09-06 に Antigravity の既定を Structured へ変えた（§5 / §13-3 追記2）。
        // これで v1 の部門はすべて構造化で動き、部門を動かすために PTY は要らない。
        // TUI セッションが入るまで、ここが Tui を含むと起動できない部門ができる。
        var departments = DepartmentStore.CreateDefaultDepartments();

        Assert.Equal(5, departments.Count);
        Assert.All(departments, department => Assert.Equal(DriveMode.Structured, department.Mode));
    }

}
