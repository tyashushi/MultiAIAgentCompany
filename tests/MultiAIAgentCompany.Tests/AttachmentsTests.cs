using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class AttachmentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "attachments-" + Guid.NewGuid().ToString("N"));
    private readonly string _sources;
    private readonly CompanyPaths _paths;

    public AttachmentsTests()
    {
        _sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(_sources);
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        _paths = new CompanyPaths(workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Source(string name, int bytes)
    {
        var path = Path.Combine(_sources, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void 越えたものだけ理由を付けて弾き残りは受け付ける()
    {
        var small = Source("a.txt", 10);
        var big = Path.Combine(_sources, "big.bin");
        using (var stream = File.Create(big)) stream.SetLength(Attachments.MaxFileBytes + 1);

        var admission = Attachments.Admit([], [small, big, _sources, Path.Combine(_sources, "missing.png")]);

        Assert.Equal("a.txt", Assert.Single(admission.Accepted).Name);
        Assert.Equal(3, admission.Rejections.Count);
        Assert.Contains(admission.Rejections, reason => reason.StartsWith("big.bin: 1ファイル", StringComparison.Ordinal));
        Assert.Contains(admission.Rejections, reason => reason.Contains("フォルダ", StringComparison.Ordinal));
        Assert.Contains(admission.Rejections, reason => reason.StartsWith("missing.png: ファイルが見つからない", StringComparison.Ordinal));
    }

    [Fact]
    public void 合計と個数の上限を今ある添付と合わせて数える()
    {
        var half = (int)(Attachments.MaxTotalBytes / 2);
        var first = Attachments.Admit([], [Source("1.bin", half)]).Accepted;
        var admission = Attachments.Admit(first, [Source("2.bin", half), Source("3.bin", 1)]);
        Assert.Equal("2.bin", Assert.Single(admission.Accepted).Name);
        Assert.Contains("合計", Assert.Single(admission.Rejections), StringComparison.Ordinal);

        var many = Enumerable.Range(0, Attachments.MaxCount + 1).Select(i => Source($"{i}.txt", 1)).ToArray();
        var counted = Attachments.Admit([], many);
        Assert.Equal(Attachments.MaxCount, counted.Accepted.Count);
        Assert.Contains("個まで", Assert.Single(counted.Rejections), StringComparison.Ordinal);
    }

    [Fact]
    public void 同じファイルは2回足さない()
    {
        var path = Source("a.txt", 1);
        var first = Attachments.Admit([], [path, path]).Accepted;
        Assert.Single(first);
        Assert.Empty(Attachments.Admit(first, [path]).Accepted);
    }

    [Fact]
    public async Task 送るときに1フォルダへ複製し空白を置き換え重なりに番号を付ける()
    {
        var one = Source("my shot.png", 3);
        Directory.CreateDirectory(Path.Combine(_sources, "other"));
        var two = Path.Combine(_sources, "other", "my shot.png");
        File.WriteAllBytes(two, [1, 2]);
        var drafts = Attachments.Admit([], [one, two]).Accepted;

        var result = Assert.IsType<AttachmentCopyResult.Copied>(await Attachments.CopyAsync(
            _paths, drafts, new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.Zero), CancellationToken.None));

        Assert.Equal(2, result.Links.Count);
        Assert.Matches(@"^\.company/attachments/20260916-213000-[0-9a-f]{4}/my_shot\.png$", result.Links[0]);
        Assert.EndsWith("/my_shot-2.png", result.Links[1], StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(result.Links[0]), Path.GetDirectoryName(result.Links[1]));
        Assert.Equal([1, 2], File.ReadAllBytes(Path.Combine(_paths.WorkspaceRoot, result.Links[1])));
        // 複製したパスはそのままリンクになる（§58-5）。
        Assert.All(result.Links, link => Assert.Equal(link, Assert.Single(TranscriptImageLinks.Find(link)).Link));
    }

    [Fact]
    public async Task 複製に失敗したら途中のフォルダを残さない()
    {
        var one = Source("a.txt", 1);
        var gone = Source("b.txt", 1);
        var drafts = Attachments.Admit([], [one, gone]).Accepted;
        File.Delete(gone);

        var result = await Attachments.CopyAsync(_paths, drafts, DateTimeOffset.Now, CancellationToken.None);

        Assert.IsType<AttachmentCopyResult.Failed>(result);
        Assert.True(!Directory.Exists(_paths.AttachmentsRoot) || Directory.GetFileSystemEntries(_paths.AttachmentsRoot).Length == 0);
    }

    [Theory]
    [InlineData("本文", "本文\n\n添付:\n- .company/attachments/x/a.png")]
    [InlineData("本文\n\n", "本文\n\n添付:\n- .company/attachments/x/a.png")]
    [InlineData("", "添付:\n- .company/attachments/x/a.png")]
    [InlineData(null, "添付:\n- .company/attachments/x/a.png")]
    public void 本文のあとに添付の塊を足す(string? body, string expected) =>
        Assert.Equal(expected, Attachments.Compose(body, [".company/attachments/x/a.png"]));

    [Fact]
    public void 添付が無ければ本文だけ() => Assert.Equal("本文", Attachments.Compose("本文", []));

    [Theory]
    [InlineData("a b\tc.txt", "a_b_c.txt")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("...", "attachment")]
    [InlineData("絵.png", "絵.png")]
    [InlineData(".env", ".env")]
    public void 名前の空白と使えない文字を置き換える(string name, string expected) =>
        Assert.Equal(expected, Attachments.SafeName(name));
}
