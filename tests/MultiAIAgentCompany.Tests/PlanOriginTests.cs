using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §37。<b>計画は受理できるが、終端からは戻せない</b>ことを固定する。
/// </summary>
public sealed class PlanOriginTests
{
    [Theory]
    [InlineData(CoreTaskStatus.Accepted)]
    [InlineData(CoreTaskStatus.Rejected)]
    public void 計画は報告を受理も差し戻しもできる(CoreTaskStatus to)
    {
        Assert.True(TaskTransitions.Check(CoreTaskStatus.Reported, to, TransitionOrigin.Plan).Allowed);
    }

    [Theory]
    [InlineData(CoreTaskStatus.Accepted)]
    [InlineData(CoreTaskStatus.Rejected)]
    public void 走査は報告を受理も差し戻しもできない(CoreTaskStatus to)
    {
        // **走査と計画を同じ主体に畳まない**（§7 の「軸を潰さない」）。
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Reported, to, TransitionOrigin.Automation).Allowed);
    }

    [Theory]
    [InlineData(CoreTaskStatus.Accepted)]
    [InlineData(CoreTaskStatus.Failed)]
    [InlineData(CoreTaskStatus.Cancelled)]
    public void 計画は終端から戻せない(CoreTaskStatus from)
    {
        // 終わったものを蒸し返すのは人間の仕事。**計画は先に承認された順序でしかない。**
        Assert.False(TaskTransitions.Check(from, CoreTaskStatus.Dispatched, TransitionOrigin.Plan).Allowed);
        Assert.True(TaskTransitions.Check(from, CoreTaskStatus.Dispatched, TransitionOrigin.Human).Allowed);
    }

    [Fact]
    public void 計画も未定義の遷移は起こせない()
    {
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Drafted, CoreTaskStatus.Reported, TransitionOrigin.Plan).Allowed);
    }
}
