using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §13-9 —— 「信頼されていない」と「分からない」を同じにしない。</summary>
public sealed class WorkspaceTrustReportTests
{
    private static readonly WorkspaceRef Workspace = new(Path.GetTempPath());

    [Fact]
    public async Task 三値がそのまま出る()
    {
        var rows = await WorkspaceTrustReport.BuildAsync(Workspace,
            [Probe(AgentKind.ClaudeCode, true), Probe(AgentKind.CodexCli, false), Probe(AgentKind.AntigravityCli, null)],
            CancellationToken.None);

        Assert.Equal(WorkspaceTrustState.Trusted, rows[0].State);
        Assert.Equal(WorkspaceTrustState.NotTrusted, rows[1].State);
        Assert.Equal(WorkspaceTrustState.Unknown, rows[2].State);
    }

    [Fact]
    public async Task 聞けなかったものは信頼されていないと言わない()
    {
        var rows = await WorkspaceTrustReport.BuildAsync(Workspace,
            [Throwing(AgentKind.ClaudeCode)], CancellationToken.None);

        Assert.Equal(WorkspaceTrustState.Unknown, Assert.Single(rows).State);
    }

    [Fact]
    public async Task 一つが壊れても他は止まらない()
    {
        // 1つ読めないことは、他が読めないことを意味しない。
        var rows = await WorkspaceTrustReport.BuildAsync(Workspace,
            [Throwing(AgentKind.ClaudeCode), Probe(AgentKind.CodexCli, true)], CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(WorkspaceTrustState.Trusted, rows[1].State);
    }

    private static IWorkspaceTrustProbe Probe(AgentKind kind, bool? answer) => new FakeProbe(kind, answer, throws: false);

    private static IWorkspaceTrustProbe Throwing(AgentKind kind) => new FakeProbe(kind, null, throws: true);

    private sealed class FakeProbe(AgentKind kind, bool? answer, bool throws) : IWorkspaceTrustProbe
    {
        public AgentKind Kind => kind;

        public Task<bool?> IsTrustedAsync(WorkspaceRef workspace, CancellationToken ct) =>
            throws ? throw new IOException("読めない") : Task.FromResult(answer);
    }
}
