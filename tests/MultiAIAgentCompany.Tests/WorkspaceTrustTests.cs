using MultiAIAgentCompany.Core.Workspace;
using MultiAIAgentCompany.Core.Workspace.Trust;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class WorkspaceTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"multi-ai-agent-company-trust-{Guid.NewGuid():N}");
    private readonly WorkspaceRef _workspace;

    public WorkspaceTrustTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = new WorkspaceRef(Path.Combine(_root, "workspace"));
        Directory.CreateDirectory(_workspace.Root);
    }

    [Fact]
    public async Task Claudeはtrust済みをtrueと返す()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".claude.json"),
            $"{{\"projects\":{{\"{_workspace.Root}\":{{\"hasTrustDialogAccepted\":true}}}}}}");
        Assert.True(await new ClaudeCodeTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Claudeは対象が無ければfalseで壊れた形はnullを返す()
    {
        var path = Path.Combine(_root, ".claude.json");
        await File.WriteAllTextAsync(path, "{\"projects\":{\"/other\":{\"hasTrustDialogAccepted\":false}}}");
        Assert.False(await new ClaudeCodeTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "{");
        Assert.Null(await new ClaudeCodeTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "{\"projects\":{\"/other\":true}}");
        Assert.Null(await new ClaudeCodeTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Codexはtrust済みと未記録と壊れた値を区別する()
    {
        var directory = Path.Combine(_root, ".codex"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.toml");
        await File.WriteAllTextAsync(path, $"[projects.\"{_workspace.Root}\"]\ntrust_level = \"trusted\"");
        Assert.True(await new CodexCliTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "[projects.\"/other\"]\ntrust_level = \"trusted\"");
        Assert.False(await new CodexCliTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "[projects.\"x\"]\ntrust_level = 42");
        Assert.Null(await new CodexCliTrustProbe(_root).IsTrustedAsync(new WorkspaceRef("x"), CancellationToken.None));
        await File.WriteAllTextAsync(path, "[projects.\"unterminated]");
        Assert.Null(await new CodexCliTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Antigravityはtrust済みと未記録と壊れた形を区別する()
    {
        var directory = Path.Combine(_root, ".gemini", "antigravity-cli"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, $"{{\"trustedWorkspaces\":[\"{_workspace.Root}\"]}}");
        Assert.True(await new AntigravityTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "{\"trustedWorkspaces\":[\"/other\"]}");
        Assert.False(await new AntigravityTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "{");
        Assert.Null(await new AntigravityTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        await File.WriteAllTextAsync(path, "{\"trustedWorkspaces\":[1]}");
        Assert.Null(await new AntigravityTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
    }

    [Fact]
    public async Task 設定ファイルが無いときは全てnullを返す()
    {
        Assert.Null(await new ClaudeCodeTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        Assert.Null(await new CodexCliTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
        Assert.Null(await new AntigravityTrustProbe(_root).IsTrustedAsync(_workspace, CancellationToken.None));
    }

    [Fact]
    public async Task 実体パスでtmpの表記揺れを比較する()
    {
        if (!Directory.Exists("/private/tmp")) return;
        var path = Path.Combine(_root, ".claude.json");
        await File.WriteAllTextAsync(path, "{\"projects\":{\"/tmp\":{\"hasTrustDialogAccepted\":true}}}");
        Assert.True(await new ClaudeCodeTrustProbe(_root).IsTrustedAsync(new WorkspaceRef("/private/tmp"), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    [Fact]
    public async Task 親がシンボリックリンクでも同じワークスペースと分かる()
    {
        // ResolveLinkTarget は最後の要素しか解決しない。macOS の /tmp -> /private/tmp のように
        // 「親がリンク」だと、trust 済みなのに false と答えてしまう（§13-9 規則3）。
        var real = Path.Combine(_root, "real-parent");
        var link = Path.Combine(_root, "link-parent");
        Directory.CreateDirectory(Path.Combine(real, "repo"));
        Directory.CreateSymbolicLink(link, real);

        var settings = Path.Combine(_root, ".gemini", "antigravity-cli");
        Directory.CreateDirectory(settings);
        await File.WriteAllTextAsync(Path.Combine(settings, "settings.json"),
            "{\"trustedWorkspaces\":[" + System.Text.Json.JsonSerializer.Serialize(Path.Combine(real, "repo")) + "]}");

        // 記録は実体パス、問い合わせはリンク越しのパス。
        var probe = new AntigravityTrustProbe(_root);
        Assert.True(await probe.IsTrustedAsync(new WorkspaceRef(Path.Combine(link, "repo")), CancellationToken.None));
    }

}
