using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §21 —— 前回のワークスペースを覚える。<b>疑わしいときは開かない。</b></summary>
public sealed class WorkspaceMemoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-memory-").FullName;
    private readonly WorkspaceMemory _memory;
    private readonly string _workspace;

    public WorkspaceMemoryTests()
    {
        _memory = new WorkspaceMemory(Path.Combine(_root, "settings", "workspace.json"));
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, ".company"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task RememberAsync(string path) =>
        _memory.RememberAsync(path, DateTimeOffset.UnixEpoch, CancellationToken.None);

    private Task<WorkspaceResume> DecideAsync() => _memory.DecideAsync(CancellationToken.None);

    [Fact]
    public async Task 覚えたフォルダはそのまま開く()
    {
        await RememberAsync(_workspace);

        var open = Assert.IsType<WorkspaceResume.Open>(await DecideAsync());

        Assert.Equal(_workspace, open.Remembered.RawPath);
    }

    [Fact]
    public async Task 一度も選んでいなければ何も言わない()
    {
        // 初回起動。**壊れた記録と同じ扱いにしない**（§21-1）。
        Assert.IsType<WorkspaceResume.Unset>(await DecideAsync());
    }

    [Fact]
    public async Task フォルダが消えていたら開かない()
    {
        await RememberAsync(_workspace);
        Directory.Delete(_workspace, recursive: true);

        var ask = Assert.IsType<WorkspaceResume.Ask>(await DecideAsync());

        Assert.Contains("が無い", ask.Reason, StringComparison.Ordinal);
        Assert.Equal(_workspace, Assert.IsType<RememberedWorkspace>(ask.Remembered).RawPath);
    }

    [Fact]
    public async Task company_が無ければ黙って作らずに聞く()
    {
        // 作ってしまうと、別のフォルダを「前に使っていたワークスペース」に見せかける（§21-1）。
        await RememberAsync(_workspace);
        Directory.Delete(Path.Combine(_workspace, ".company"), recursive: true);

        var ask = Assert.IsType<WorkspaceResume.Ask>(await DecideAsync());

        Assert.Contains(".company/ が無い", ask.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_workspace, ".company")));
    }

    [Fact]
    public async Task symlinkが張り替えられたら開かない()
    {
        // **同じ綴りで中身が別物になる典型**（§21-2）。人間には同じフォルダに見える。
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, _workspace);
        await RememberAsync(link);

        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(other, ".company"));
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, other);

        var ask = Assert.IsType<WorkspaceResume.Ask>(await DecideAsync());

        Assert.Contains("実体が変わっている", ask.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 記録が壊れていても落ちずに聞く()
    {
        var path = Path.Combine(_root, "settings", "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{");

        var ask = Assert.IsType<WorkspaceResume.Ask>(await DecideAsync());

        // **Unset にしない。** 黙って捨てると「まだ選んでいない」と区別が付かない。
        Assert.Null(ask.Remembered);
        Assert.Contains("読めなかった", ask.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ワークスペースの中には書かない()
    {
        // アプリの設定であって、AI たちと共有する調整文書ではない（§21-3）。
        await RememberAsync(_workspace);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_workspace, ".company")));
    }
}
