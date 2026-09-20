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
    public void ttyも一緒に読み取る()
    {
        // 起動のスクリプトは "tab 1 of window id 43990 tty /dev/ttys013" を返す（設計 §62-24）。
        var handle = MacTerminalScript.ParseHandle(
            "tab 2 of window id 43990 tty /dev/ttys013\n", "/tmp/x.pid");

        Assert.NotNull(handle);
        Assert.Equal("43990", handle.WindowId);
        Assert.Equal(2, handle.TabIndex);
        Assert.Equal("/dev/ttys013", handle.Tty);
    }

    [Fact]
    public void ttyが無い出力でも窓とタブは読み取る()
    {
        // **取れなかったものを捏造しない**（§7）。古い形でも窓とタブは取れる。
        var handle = MacTerminalScript.ParseHandle("tab 1 of window id 43990\n", "/tmp/x.pid");

        Assert.NotNull(handle);
        Assert.Null(handle.Tty);
    }

    [Fact]
    public void ttyがあれば位置ではなくttyで探す()
    {
        var script = MacTerminalScript.FocusScript(
            new TerminalHandle("43990", 2, "/tmp/x.pid", "/dev/ttys013"));

        Assert.Contains("tty of t is \"/dev/ttys013\"", script);
        // **位置では選ばない。** タブを並べ替えられていたら別のタブを指す。
        Assert.DoesNotContain("tab 2 of window id 43990", script);
        // 見つからなければ失敗させる（隣のタブを前に出して「前に出した」と言わない）。
        Assert.Contains("error \"tab not found\"", script);
    }

    [Fact]
    public void ttyが無いハンドルはこれまでどおり位置で選ぶ()
    {
        var script = MacTerminalScript.FocusScript(new TerminalHandle("43990", 2, "/tmp/x.pid"));

        Assert.Contains("set selected of tab 2 of window id 43990 to true", script);
        Assert.DoesNotContain("tty", script);
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
        Assert.Contains("そこで turn を終える", text);
        Assert.Contains("人間がこのターミナルの入力欄で答える", text);
        Assert.DoesNotContain("answer.md", text);
        Assert.Contains("自分から人間に聞きに行こうとしない", text);
        Assert.Contains("シェルを使って入力を待つ（`read` など）ことはしない", text);
        Assert.Contains("report.md", text);

        // 設計 §62-3。指示書と同じ範囲を任せ、質問に必要な4項目を揃える。
        Assert.Contains("依頼と既存の規約の範囲での局所的な実装判断は、自分で決めてよい。決めたことは報告に書く。", text);
        Assert.Contains("仕様の変更・範囲の拡大・依頼の対象外に触れること・戻せない操作・依頼どうしの矛盾は、質問を書いて止まること。", text);
        Assert.Contains("質問には「何を決めたいか / 選択肢 / 推奨とその理由 / 決めないと何が止まるか」を書く。", text);
        Assert.DoesNotContain("自分で決めないでください", text);
        Assert.DoesNotContain("何を聞きたいかを1〜2行で", text);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\codex.cmd", "100%")]
    [InlineData(@"C:\x\codex.CMD", "a\nb")]
    [InlineData(@"C:\x\run.bat", "\"x\" & calc")]
    [InlineData(@"C:\x\codex.cmd", "gpt&calc")]  // 空白が無いと引用符で囲まれない
    public void バッチファイルに壊れる引数は渡さない(string command, string argument)
    {
        // **npm の shim は cmd.exe を通る。** `%` は展開され、`"` と `&` が同じ引数にあると
        // 後ろが別の命令として走る（設計 §32-7）。
        Assert.NotNull(WindowsCommandLine.UnsafeForBatch(command, ["-i", argument]));
    }

    [Theory]
    [InlineData(@"C:\x\codex.cmd", "model_reasoning_effort=\"high\"")]            // 実際に渡している形
    [InlineData(@"C:\x\codex.cmd", @"C:\ws & co\.company\README.md と ... を読んで")] // `"` が無ければ & は引用符の中
    [InlineData(@"C:\x\claude.exe", "100% & \"x\"")]                                 // exe は cmd.exe を通らない
    public void ふつうの引数はバッチファイルにも渡す(string command, string argument)
    {
        Assert.Null(WindowsCommandLine.UnsafeForBatch(command, [argument]));
    }
}
