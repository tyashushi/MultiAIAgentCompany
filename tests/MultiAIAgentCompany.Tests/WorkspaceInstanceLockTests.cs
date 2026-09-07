using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §26 —— 同じワークスペースを2つのアプリが開かない。</summary>
public sealed class WorkspaceInstanceLockTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-lock-").FullName;
    private readonly string _runtime;
    private readonly string _workspace;

    public WorkspaceInstanceLockTests()
    {
        _runtime = Path.Combine(_root, "runtime");
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WorkspaceInstanceLockResult Acquire(string? workspace = null) =>
        WorkspaceInstanceLock.Acquire(workspace ?? _workspace, _runtime, DateTimeOffset.UnixEpoch);

    [Fact]
    public void 誰も開いていなければ取れる()
    {
        using var held = Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire()).Lock;
        Assert.True(Directory.Exists(held.Directory));
    }

    [Fact]
    public void 握られている間は取れない()
    {
        using var first = Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire()).Lock;

        var second = Assert.IsType<WorkspaceInstanceLockResult.Held>(Acquire());

        // **説明は出せる。** 判定の根拠ではないが、人間が誰か分かるように（§26-2）。
        var holder = Assert.IsType<WorkspaceInstanceHolder>(second.Holder);
        Assert.Equal(Environment.ProcessId, holder.Pid);
    }

    [Fact]
    public void 返せばまた取れる()
    {
        // **クラッシュしても OS が返す**ので、「外す」操作は要らない（§26-1）。
        Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire()).Lock.Dispose();

        using var again = Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire()).Lock;
    }

    [Fact]
    public void 別のワークスペースは同時に開ける()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);

        using var first = Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire()).Lock;
        using var second = Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire(other)).Lock;
    }

    [Fact]
    public void 綴りが違っても同じ実体なら1つだけ()
    {
        // symlink の綴り違いで「別のワークスペース」に見えると、
        // 同じフォルダを2つのアプリが開ける（§21-2 と同じ理由）。
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, _workspace);

        using var first = Assert.IsType<WorkspaceInstanceLockResult.Acquired>(Acquire()).Lock;

        Assert.IsType<WorkspaceInstanceLockResult.Held>(Acquire(link));
    }
}
