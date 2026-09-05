using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §7 の信頼順を固定する。</summary>
public sealed class EvidenceTests
{
    private static Evidence At(EvidenceSource source, DateTimeOffset observedAt) => new(
        source, observedAt, null, null, new AgentRef("設計", AgentKind.ClaudeCode), null, null, "test");

    [Fact]
    public void 古いドキュメントは新しい画面解析より強い()
    {
        // 「見えている画面」は信頼順4位。.company/ の状態遷移が第一根拠（設計 §7）。
        var now = DateTimeOffset.UnixEpoch;
        var document = At(EvidenceSource.Document, now);
        var screen = At(EvidenceSource.RenderedScreen, now.AddHours(1));

        Assert.Same(document, Evidence.Stronger(document, screen));
        Assert.Same(document, Evidence.Stronger(screen, document));
    }

    [Fact]
    public void 同順なら新しい方が勝つ()
    {
        var older = At(EvidenceSource.StructuredEvent, DateTimeOffset.UnixEpoch);
        var newer = At(EvidenceSource.StructuredEvent, DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Same(newer, Evidence.Stronger(older, newer));
    }

    [Fact]
    public void 古い根拠は現在の証拠ではない()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(2);
        var stale = At(EvidenceSource.StructuredEvent, DateTimeOffset.UnixEpoch);

        Assert.False(stale.IsFreshAt(now, TimeSpan.FromMinutes(5)));
    }
}
