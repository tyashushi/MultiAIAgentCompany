using System.Diagnostics;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// <c>.company/</c> を git に入れないよう、人間に聞く（設計 §53）。
/// </summary>
public sealed class CompanyGitIgnoreTests
{
    [Fact]
    public void 無いファイルには注釈と行だけを書く()
    {
        Assert.Equal(
            "# MultiAIAgentCompany の作業記録（秘書との会話・指示書・報告書）\n.company/\n",
            CompanyGitIgnore.AppendEntry(null));
    }

    [Fact]
    public void 既存の行とは1行空けて区切る()
    {
        var updated = CompanyGitIgnore.AppendEntry("bin/\nobj/\n");

        Assert.StartsWith("bin/\nobj/\n\n# MultiAIAgentCompany", updated);
        Assert.EndsWith("\n.company/\n", updated);
    }

    [Fact]
    public void 改行で終わらないファイルでは最後の行にくっつけない()
    {
        // **くっつくと `obj/# MultiAIAgentCompany…` という1行になり、どちらも効かなくなる。**
        var updated = CompanyGitIgnore.AppendEntry("bin/\nobj/");

        Assert.StartsWith("bin/\nobj/\n\n# MultiAIAgentCompany", updated);
    }

    [Fact]
    public async Task git_の管理下でなければ聞かない()
    {
        using var temp = new TemporaryWorkspace();

        Assert.IsType<CompanyGitIgnoreCheck.NotApplicable>(
            await CompanyGitIgnore.CheckAsync(new WorkspaceRef(temp.Path), CancellationToken.None));
    }

    [Fact]
    public async Task 無視されていなければ聞き_足したら聞かない()
    {
        using var temp = new TemporaryWorkspace();
        Git(temp.Path, "init", "-q");
        var workspace = new WorkspaceRef(temp.Path);

        var before = Assert.IsType<CompanyGitIgnoreCheck.ShouldAsk>(await CompanyGitIgnore.CheckAsync(workspace, CancellationToken.None));
        Assert.False(before.AlreadyTracked);

        Assert.True(await CompanyGitIgnore.AddAsync(workspace, CancellationToken.None));

        Assert.IsType<CompanyGitIgnoreCheck.AlreadyIgnored>(await CompanyGitIgnore.CheckAsync(workspace, CancellationToken.None));
    }

    [Theory]
    [InlineData("  .company/\n")]
    [InlineData(".company/\n!.company/\n")]
    public async Task 書いてあっても効いていなければ足して効かせる(string existing)
    {
        // **行頭の空白は git に効かず、後ろの `!` は前の行を打ち消す**（Codex の指摘）。
        // 文字で「もう書いてある」と判定すると、何も足さずに「足した」と言っていた。
        using var temp = new TemporaryWorkspace();
        Git(temp.Path, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), existing);
        var workspace = new WorkspaceRef(temp.Path);
        Assert.IsType<CompanyGitIgnoreCheck.ShouldAsk>(await CompanyGitIgnore.CheckAsync(workspace, CancellationToken.None));

        Assert.True(await CompanyGitIgnore.AddAsync(workspace, CancellationToken.None));
        Assert.IsType<CompanyGitIgnoreCheck.AlreadyIgnored>(await CompanyGitIgnore.CheckAsync(workspace, CancellationToken.None));
    }

    [Fact]
    public async Task 断ったフォルダでは聞かない()
    {
        using var temp = new TemporaryWorkspace();
        Git(temp.Path, "init", "-q");
        var workspace = new WorkspaceRef(temp.Path);

        await CompanyGitIgnore.DeclineAsync(workspace, CancellationToken.None);

        Assert.IsType<CompanyGitIgnoreCheck.Declined>(await CompanyGitIgnore.CheckAsync(workspace, CancellationToken.None));
    }

    [Fact]
    public async Task 既に_commit_されていればそう伝える()
    {
        // **`.gitignore` に足しても、commit 済みのものは外れない。**
        using var temp = new TemporaryWorkspace();
        Git(temp.Path, "init", "-q");
        Directory.CreateDirectory(Path.Combine(temp.Path, ".company"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".company", "departments.json"), "{}");
        Git(temp.Path, "add", ".company");

        var check = Assert.IsType<CompanyGitIgnoreCheck.ShouldAsk>(
            await CompanyGitIgnore.CheckAsync(new WorkspaceRef(temp.Path), CancellationToken.None));

        Assert.True(check.AlreadyTracked);
    }

    private static void Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
