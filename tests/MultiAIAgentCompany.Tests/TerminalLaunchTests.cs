using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Terminal;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class TerminalLaunchTests
{
    private static TerminalLaunchRequest Request(params string[] arguments) =>
        new("MultiAI-research", "/tmp/work", "/usr/local/bin/agy", arguments);

    [Fact]
    public void 起動スクリプトはPIDを書いてからCLIをexecする()
    {
        var script = MacTerminalScript.Build(Request("-i", "README を読んで"), "/tmp/x.pid");

        Assert.Contains("echo $$ > '/tmp/x.pid'", script);
        Assert.Contains("cd '/tmp/work'", script);

        // **exec で置き換える。** PID ファイルが指すのが CLI 自身になり、
        // プロセスグループはシェルのものを引き継ぐので kill -TERM -<pgid> が効く。
        Assert.Contains("exec '/usr/local/bin/agy' '-i' 'README を読んで'", script);
    }

    [Fact]
    public void 起動スクリプトはexit_codeを書かない()
    {
        // **§32-2e の実測。** CLI は turn が終わってもセッションを終了しないので、
        // 終了コードは人間が窓を閉じたときにしか現れない。
        // 書くと「完了の信号」に見えてしまう —— 完了の信号は report.md だけ（§16-1）。
        var script = MacTerminalScript.Build(Request("-i", "x"), "/tmp/x.pid");

        Assert.DoesNotContain("exit_code", script);
        Assert.DoesNotContain("trap", script);
    }

    [Theory]
    [InlineData("it's here")]
    [InlineData("a'b'c")]
    public void 引数にシングルクォートが混ざっても閉じない(string argument)
    {
        var script = MacTerminalScript.Build(Request(argument), "/tmp/x.pid");

        // 閉じて足して開き直す形になっていること（'\'' の並び）。
        Assert.Contains("'\\''", script);
        Assert.DoesNotContain($"exec '/usr/local/bin/agy' '{argument}'", script);
    }

    [Fact]
    public void 窓のidを読み取る()
    {
        var handle = MacTerminalScript.ParseHandle("tab 1 of window id 43990\n", "/tmp/x.pid");

        Assert.NotNull(handle);
        Assert.Equal("43990", handle.WindowId);
        Assert.Equal(1, handle.TabIndex);
    }

    [Fact]
    public void 窓のidが読めなければnullにする()
    {
        // **推測しない**（§7）。読めないなら「開いた」と言わない。
        Assert.Null(MacTerminalScript.ParseHandle("何か別の出力", "/tmp/x.pid"));
    }

    [Fact]
    public async Task 対応していないOSでは理由を返す()
    {
        // **黙って何もしない実装にしない**（§32-7 / §28-1）。
        var launcher = new UnsupportedTerminalLauncher();

        var result = Assert.IsType<TerminalLaunchResult.Failed>(
            await launcher.LaunchAsync(Request("-i", "x"), CancellationToken.None));
        Assert.Contains("§32-7", result.Reason);
        Assert.False(await launcher.FocusAsync(new TerminalHandle("1", 1, "/tmp/x.pid"), CancellationToken.None));
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode, 1)]
    [InlineData(AgentKind.CodexCli, 1)]
    [InlineData(AgentKind.AntigravityCli, 2)]
    public void 対話起動の引数はCLIごとに違う(AgentKind kind, int count)
    {
        // **実測（§32-2）。** agy だけ -i が要る —— -p は --print の別名で非対話なので、
        // それを使うと §30-1 の auto-deny に戻る。
        var arguments = AgentExecutable.InteractiveArguments(kind, "README を読んで");

        Assert.Equal(count, arguments.Count);
        Assert.Equal("README を読んで", arguments[^1]);
        if (kind is AgentKind.AntigravityCli)
        {
            Assert.Equal("-i", arguments[0]);
            Assert.DoesNotContain("-p", arguments);
        }
    }

    [Fact]
    public void 起動時に渡すのは指示書の在り処だけで本文ではない()
    {
        // **§32-2f の実測。** argv は ps に出るので、指示に秘密が入り得る以上そこへ流さない。
        var paths = new CompanyPaths("/tmp/ws");
        var prompt = DepartmentReadme.LaunchPrompt(paths, "feature");

        Assert.Contains(paths.Instruction("feature"), prompt);
        Assert.Contains(DepartmentReadme.PathIn(paths), prompt);

        // 本文を読みに行っていないこと（ファイルすら無いのに作れている）。
        Assert.False(File.Exists(paths.Instruction("feature")));
    }

    [Fact]
    public async Task 部門protocolは聞き方を指示しない()
    {
        // **§32-5 の実測。** 「ターミナルで人間に直接聞け」と書いたら、
        // Codex はシェルの read を起動して、誰も打ち込めない場所で止まった。
        using var workspace = new TemporaryWorkspace();
        await DepartmentReadme.WriteAsync(workspace.Paths, CancellationToken.None);

        var text = await File.ReadAllTextAsync(DepartmentReadme.PathIn(workspace.Paths));

        Assert.Contains("question.md", text);
        Assert.Contains("turn を終え", text);
        Assert.Contains("read", text);          // シェルで待つな、と名指ししている
        Assert.Contains("report.md", text);
    }
}
