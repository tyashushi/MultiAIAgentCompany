using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §27-2 —— 表示補助であって正規化ではない。</summary>
public sealed class AgentModelTextTests
{
    [Fact]
    public void 知っているIDは読みやすくする()
    {
        Assert.Equal("Opus 5", AgentModelText.Of(new AgentModel("claude-opus-5", null)));
    }

    [Fact]
    public void 知らないIDはそのまま出す()
    {
        // **対応表は古くなる。** 知らないものを隠すより、生の ID を出す方が正直（§27-2）。
        Assert.Equal("gpt-9-unknown", AgentModelText.Of(new AgentModel("gpt-9-unknown", null)));
    }

    [Fact]
    public void 思考の強さは日本語にする()
    {
        Assert.Equal("gpt-5.6-terra 高", AgentModelText.Of(new AgentModel("gpt-5.6-terra", "high")));
    }

    [Fact]
    public void 知らない強さも隠さない()
    {
        // 空欄にすると「無い」と読まれる（§7 の「沈黙を正常にしない」と同じ）。
        Assert.Equal("gpt-5.6-terra xhigh", AgentModelText.Of(new AgentModel("gpt-5.6-terra", "xhigh")));
    }

    [Fact]
    public void 強さが無い場合はモデル名だけ()
    {
        // Claude は思考の強さを返してこない（実測 §27）。
        Assert.Equal("Sonnet 5", AgentModelText.Of(new AgentModel("claude-sonnet-5", null)));
    }
}
