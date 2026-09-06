using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §16-1 —— publish 契約は部門に守らせるものなので、指示書に書く。
/// </summary>
public sealed class CompanyInstructionTests
{
    private static readonly CompanyPaths Paths = new("/tmp/ws");

    [Fact]
    public void 人間の指示が先頭に残る()
    {
        var text = CompanyInstruction.Compose("README を読んで報告して", Paths, "add-login");

        Assert.StartsWith("README を読んで報告して", text, StringComparison.Ordinal);
    }

    [Fact]
    public void publish手順を具体的な道で示す()
    {
        // 「report.md に書け」だけでは、書きかけを最終名で置かれる。
        var text = CompanyInstruction.Compose("やること", Paths, "add-login");

        Assert.Contains(Paths.Report("add-login"), text, StringComparison.Ordinal);
        Assert.Contains(".tmp.", text, StringComparison.Ordinal);
        Assert.Contains("rename", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 迷ったら止まる約束を含む()
    {
        // Codex への指示書に毎回入れている停止条件と同じ形（§3 の (b)）。
        var text = CompanyInstruction.Compose("やること", Paths, "add-login");

        Assert.Contains(Paths.Question("add-login"), text, StringComparison.Ordinal);
        Assert.Contains(Paths.Answer("add-login"), text, StringComparison.Ordinal);
        Assert.Contains("勝手に決めない", text, StringComparison.Ordinal);
    }

    [Fact]
    public void git操作をしないことと秘密値を書かないことを伝える()
    {
        var text = CompanyInstruction.Compose("やること", Paths, "add-login");

        Assert.Contains("git", text, StringComparison.Ordinal);   // 設計 §8
        Assert.Contains("トークン", text, StringComparison.Ordinal); // 設計 §10 / §14-5
    }
}
