using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §62-16。rename できなかった部門の一時ファイルを、書き終わってから引き取る。</summary>
public sealed class StagedPublishTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _workspace.Dispose();

    private string Final => _workspace.Paths.Report("t");

    private async Task<string> StageAsync(string suffix, string content, TimeSpan age)
    {
        Directory.CreateDirectory(_workspace.Paths.TaskDirectory("t"));
        var path = $"{Final}.tmp.{suffix}";
        await File.WriteAllTextAsync(path, content);
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    [Fact]
    public async Task 落ち着いた一時ファイルは最終名にする()
    {
        var staged = await StageAsync("a", "報告", TimeSpan.FromMinutes(1));

        Assert.True(StagedPublish.TryPromote(Final, Now));
        Assert.Equal("報告", await File.ReadAllTextAsync(Final));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task 書いたばかりの一時ファイルは待つ()
    {
        await StageAsync("a", "報告", TimeSpan.FromSeconds(5));

        Assert.False(StagedPublish.TryPromote(Final, Now));
        Assert.False(File.Exists(Final));
    }

    [Fact]
    public async Task いちばん新しい一時ファイルが落ち着くまで_古い方も置かない()
    {
        await StageAsync("old", "古い版", TimeSpan.FromMinutes(5));
        await StageAsync("new", "新しい版", TimeSpan.FromSeconds(5));
        Assert.False(StagedPublish.TryPromote(Final, Now));

        Assert.True(StagedPublish.TryPromote(Final, Now + TimeSpan.FromMinutes(1)));
        Assert.Equal("新しい版", await File.ReadAllTextAsync(Final));
    }

    [Fact]
    public async Task 空の一時ファイルと_最終名がある場合は引き取らない()
    {
        await StageAsync("empty", "", TimeSpan.FromMinutes(1));
        Assert.False(StagedPublish.TryPromote(Final, Now));

        await File.WriteAllTextAsync(Final, "部門が置いた報告");
        await StageAsync("a", "別の版", TimeSpan.FromMinutes(1));
        Assert.False(StagedPublish.TryPromote(Final, Now));
        Assert.Equal("部門が置いた報告", await File.ReadAllTextAsync(Final));
    }

    [Fact]
    public async Task 最終名と同じ中身の一時ファイルだけ片付ける()
    {
        // 実機で発覚。rename の代わりに写し、一時ファイルが残っていた。
        Directory.CreateDirectory(_workspace.Paths.TaskDirectory("t"));
        await File.WriteAllTextAsync(Final, "報告");
        var copy = await StageAsync("copy", "報告", TimeSpan.Zero);
        var other = await StageAsync("other", "違う中身", TimeSpan.Zero);

        StagedPublish.RemoveCopies(Final);

        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task 走査が落ち着いた一時ファイルの報告を引き取って_Reported_にする()
    {
        var tasks = new TaskStore(_workspace.Paths, TimeProvider.System);
        var state = ((TaskWriteResult.Written)await tasks.CreateAsync("t", "audit", CancellationToken.None)).State;
        await tasks.TransitionAsync(state, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None);
        var staged = $"{Final}.tmp.13bedcec";
        await File.WriteAllTextAsync(staged, "監査の報告");
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        await new CompanyScanner(_workspace.Paths, tasks).SyncAsync(CompanyScanKind.Periodic, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Reported, ((TaskReadResult.Found)await tasks.ReadAsync("t", CancellationToken.None)).State.Status);
        Assert.Equal("監査の報告", await File.ReadAllTextAsync(Final));
    }
}
