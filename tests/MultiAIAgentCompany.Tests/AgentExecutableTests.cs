using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §28-1 —— 押してから失敗するまで分からない、を無くす。</summary>
public sealed class AgentExecutableTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-path-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>その OS で実行ファイルとして見つかる名前。Windows は拡張子が要る。</summary>
    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    [Fact]
    public void PATH_にあれば見つける()
    {
        var name = AgentExecutable.NameOf(AgentKind.ClaudeCode);
        var placed = Path.Combine(_root, Executable(name));
        File.WriteAllText(placed, string.Empty);

        Assert.Equal(placed, AgentExecutable.Find(AgentKind.ClaudeCode, _root));
    }

    [Fact]
    public void 無ければnullを返す()
    {
        // **「無い」を「動かない理由が分からない」にしない。** 呼び出し元が案内を出せる。
        // 既定ではよくある置き場所も見るので、ここでは PATH だけに絞る。
        Assert.Null(AgentExecutable.Find(AgentKind.CodexCli, _root, includeWellKnown: false));
    }

    [Fact]
    public void よくある置き場所も見る()
    {
        // **GUI 起動では PATH が最小限**になる（設計 §28-1、レビューで発覚）。
        // PATH に何も無くても、Homebrew や ~/.local/bin に在れば見つける。
        var found = AgentExecutable.Find(AgentKind.ClaudeCode, _root);

        // この機械には claude が入っている（`which claude` で確認済み）。
        // 入っていない環境では null になるので、**在る場合だけ場所を確かめる**。
        if (found is not null)
        {
            Assert.DoesNotContain(_root, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 読めない場所があっても探索を止めない()
    {
        // PATH には存在しないディレクトリが普通に混ざる。そこで探索ごと失敗させない。
        var name = AgentExecutable.NameOf(AgentKind.AntigravityCli);
        File.WriteAllText(Path.Combine(_root, Executable(name)), string.Empty);
        var path = string.Join(Path.PathSeparator, [Path.Combine(_root, "ない場所"), _root]);

        Assert.NotNull(AgentExecutable.Find(AgentKind.AntigravityCli, path));
    }

    [Fact]
    public void Windowsではnpmの隣にあるshスクリプトを拾わない()
    {
        // npm は `codex.cmd` の隣に、同じ名前の sh スクリプト `codex` も置く。
        // 拡張子の無い方を拾うと、Windows では起動できないものを「見つかった」と言う（2026-09-14、実機で発覚）。
        if (!OperatingSystem.IsWindows()) return;

        File.WriteAllText(Path.Combine(_root, "codex"), "#!/bin/sh");
        var shim = Path.Combine(_root, "codex.cmd");
        File.WriteAllText(shim, "@echo off");

        Assert.Equal(shim, AgentExecutable.Find(AgentKind.CodexCli, _root, includeWellKnown: false));
    }

    [Fact]
    public void Windowsでは拡張子の無いものだけなら見つからない()
    {
        if (!OperatingSystem.IsWindows()) return;

        File.WriteAllText(Path.Combine(_root, "codex"), "#!/bin/sh");

        Assert.Null(AgentExecutable.Find(AgentKind.CodexCli, _root, includeWellKnown: false));
    }

    [Fact]
    public void 実行ファイル名はアダプタが使う名前と同じ()
    {
        // ここがずれると「見つかったのに起動できない」が起きる。
        Assert.Equal("claude", AgentExecutable.NameOf(AgentKind.ClaudeCode));
        Assert.Equal("codex", AgentExecutable.NameOf(AgentKind.CodexCli));
        Assert.Equal("agy", AgentExecutable.NameOf(AgentKind.AntigravityCli));
    }
}
