using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §16-4 —— 復旧を人間が終わらせられること。</summary>
public sealed class RecoveryResolutionTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void 読めない仕事を隔離しても中身は消えない()
    {
        // state.json を勝手に補完して直さない（§14-1）。仕事一覧から外すだけ。
        var paths = _workspace.Paths;
        Directory.CreateDirectory(paths.TaskDirectory("broken"));
        File.WriteAllText(paths.State("broken"), "{ 壊れている");
        File.WriteAllText(paths.Report("broken"), "残しておきたい報告");

        var moved = UnreadableTaskQuarantine.Isolate(paths, "broken", DateTimeOffset.UnixEpoch);

        Assert.NotNull(moved);
        Assert.False(Directory.Exists(paths.TaskDirectory("broken")));
        Assert.Equal("残しておきたい報告", File.ReadAllText(Path.Combine(moved!, "report.md")));
    }

    [Fact]
    public void 隔離した仕事は走査に出てこない()
    {
        var paths = _workspace.Paths;
        Directory.CreateDirectory(paths.TaskDirectory("broken"));
        File.WriteAllText(paths.State("broken"), "{ 壊れている");

        UnreadableTaskQuarantine.Isolate(paths, "broken", DateTimeOffset.UnixEpoch);

        Assert.False(Directory.Exists(paths.TaskDirectory("broken")));
    }

    [Fact]
    public void 存在しない仕事の隔離はnullを返す()
    {
        Assert.Null(UnreadableTaskQuarantine.Isolate(_workspace.Paths, "missing", DateTimeOffset.UnixEpoch));
    }
}
