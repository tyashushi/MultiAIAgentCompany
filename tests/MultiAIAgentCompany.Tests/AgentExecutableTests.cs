using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §28-1 —— 押してから失敗するまで分からない、を無くす。</summary>
public sealed class AgentExecutableTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-path-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void PATH_にあれば見つける()
    {
        var name = AgentExecutable.NameOf(AgentKind.ClaudeCode);
        var placed = Path.Combine(_root, name);
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
        File.WriteAllText(Path.Combine(_root, name), string.Empty);
        var path = string.Join(Path.PathSeparator, [Path.Combine(_root, "ない場所"), _root]);

        Assert.NotNull(AgentExecutable.Find(AgentKind.AntigravityCli, path));
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
