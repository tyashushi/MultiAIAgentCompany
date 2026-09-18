using System.Diagnostics;
using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §62-6。読むだけの部門の書き換えに<b>事後に気付く</b>ことを固定する。</summary>
public sealed class WorktreeSnapshotTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string At(string relative) => Path.Combine(_workspace.Path, relative);

    private async Task CommittedRepositoryAsync()
    {
        Git(_workspace.Path, "init", "-q");
        await File.WriteAllTextAsync(At("tracked.txt"), "もと");
        await File.WriteAllTextAsync(At("gone.txt"), "消える");
        Git(_workspace.Path, "add", "tracked.txt", "gone.txt");
        Git(_workspace.Path, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "init");

        // 送る前から変わっているもの（人間の書きかけ）。これ自体は違いにならない。
        await File.WriteAllTextAsync(At("draft.txt"), "書きかけ");
    }

    private Task<WorktreeCheck> CheckAsync() =>
        WorktreeSnapshot.CheckAsync(_workspace.Paths, "task", 0, CancellationToken.None);

    private Task SaveAsync() =>
        WorktreeSnapshot.SaveBeforeAsync(_workspace.Paths, "task", 0, CancellationToken.None);

    [Fact]
    public async Task 変わっていなければ_違いは無い()
    {
        await CommittedRepositoryAsync();
        await SaveAsync();

        Assert.IsType<WorktreeCheck.Unchanged>(await CheckAsync());
    }

    [Fact]
    public async Task 足した_変えた_消したファイルを_違いとして出す()
    {
        await CommittedRepositoryAsync();
        await SaveAsync();

        await File.WriteAllTextAsync(At("new.txt"), "足した");
        await File.WriteAllTextAsync(At("tracked.txt"), "変えた");
        await File.WriteAllTextAsync(At("draft.txt"), "書きかけを上書きした");
        File.Delete(At("gone.txt"));

        var changed = Assert.IsType<WorktreeCheck.Changed>(await CheckAsync());
        Assert.Equal(["draft.txt", "gone.txt", "new.txt", "tracked.txt"], changed.Paths);
        Assert.False(changed.AlreadyReported);
    }

    [Fact]
    public async Task company_の下だけの変化は_違いにしない()
    {
        await CommittedRepositoryAsync();
        await SaveAsync();

        // 報告・質問・デザイナーの画像は .company/ の下に書くのが約束（§62-6）。
        Directory.CreateDirectory(Path.Combine(_workspace.Paths.TaskDirectory("task"), "images"));
        await File.WriteAllTextAsync(_workspace.Paths.Report("task"), "報告");
        await File.WriteAllTextAsync(Path.Combine(_workspace.Paths.TaskDirectory("task"), "images", "a.png"), "画像");

        Assert.IsType<WorktreeCheck.Unchanged>(await CheckAsync());
    }

    [Fact]
    public async Task 比べるのは1回の報告につき1回だけ()
    {
        await CommittedRepositoryAsync();
        await SaveAsync();
        await File.WriteAllTextAsync(At("new.txt"), "足した");
        Assert.False(Assert.IsType<WorktreeCheck.Changed>(await CheckAsync()).AlreadyReported);

        // **2回目は git を走らせず、置いた結果を返す。** そのあとの変化は、この報告の話ではない。
        await File.WriteAllTextAsync(At("later.txt"), "あとから");
        var again = Assert.IsType<WorktreeCheck.Changed>(await CheckAsync());
        Assert.True(again.AlreadyReported);
        Assert.Equal(["new.txt"], again.Paths);
    }

    [Fact]
    public async Task 同じ試行の控えは_取り直さない()
    {
        await CommittedRepositoryAsync();
        await SaveAsync();
        await File.WriteAllTextAsync(At("new.txt"), "窓を開き直すまでに書いた");

        // 窓を開き直しても、最初の控えのまま（その間の書き換えを隠さない）。
        await SaveAsync();

        Assert.IsType<WorktreeCheck.Changed>(await CheckAsync());
    }

    [Fact]
    public async Task リポジトリの下のフォルダでも_ワークスペースからのパスで比べる()
    {
        Git(_workspace.Path, "init", "-q");
        var inner = Path.Combine(_workspace.Path, "app");
        Directory.CreateDirectory(inner);
        var paths = new CompanyPaths(inner);

        await WorktreeSnapshot.SaveBeforeAsync(paths, "task", 0, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(inner, "x.txt"), "中");
        await File.WriteAllTextAsync(At("outside.txt"), "ワークスペースの外");
        await File.WriteAllTextAsync(paths.Report("task"), "報告");

        var changed = Assert.IsType<WorktreeCheck.Changed>(
            await WorktreeSnapshot.CheckAsync(paths, "task", 0, CancellationToken.None));
        Assert.Equal(["x.txt"], changed.Paths);
    }

    [Fact]
    public async Task 先頭に空白のあるフォルダでも_接頭辞を外して比べる()
    {
        // レビューで発覚。接頭辞を Trim() すると ` app/` の空白まで消え、別の場所を読んでいた。
        Git(_workspace.Path, "init", "-q");
        var inner = Path.Combine(_workspace.Path, " app");
        Directory.CreateDirectory(inner);
        await File.WriteAllTextAsync(Path.Combine(inner, "a.txt"), "前から変わっている");
        var paths = new CompanyPaths(inner);

        await WorktreeSnapshot.SaveBeforeAsync(paths, "task", 0, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(inner, "a.txt"), "さらに変えた");

        var changed = Assert.IsType<WorktreeCheck.Changed>(
            await WorktreeSnapshot.CheckAsync(paths, "task", 0, CancellationToken.None));
        Assert.Equal(["a.txt"], changed.Paths);
    }

    [Fact]
    public async Task git_でなければ_確かめられなかったと言う()
    {
        await SaveAsync();

        var unavailable = Assert.IsType<WorktreeCheck.Unavailable>(await CheckAsync());
        Assert.Contains("git", unavailable.Reason);
    }

    [Fact]
    public async Task 控えが無ければ_比べない()
    {
        await CommittedRepositoryAsync();

        Assert.IsType<WorktreeCheck.NoBaseline>(await CheckAsync());
    }

    [Fact]
    public void 一覧は10件までにして件数を言う()
    {
        var many = Enumerable.Range(1, 12).Select(i => $"f{i:00}.txt").ToArray();

        Assert.Equal("a.txt（1 件）", WorktreeSnapshot.Describe(["a.txt"]));
        Assert.EndsWith("f10.txt ほか（全 12 件）", WorktreeSnapshot.Describe(many));
    }

    internal static void Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
