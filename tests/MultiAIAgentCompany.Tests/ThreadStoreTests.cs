using System.Text.Json;
using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class ThreadStoreTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(2026, 9, 9, 1, 2, 3, TimeSpan.Zero));
    private readonly ThreadStore _store;

    public ThreadStoreTests() => _store = new ThreadStore(_workspace.Paths, _clock);

    [Fact]
    public async Task 作成して別の保存口で読み直すとタイトルと時刻が往復する()
    {
        var meta = await CreateAsync("日本語の会話 / 相談");
        var reopened = new ThreadStore(_workspace.Paths, _clock);
        var read = Assert.IsType<ThreadReadResult.Found>(await reopened.ReadAsync(meta.Id, CancellationToken.None));

        Assert.Equal(meta, read.Meta);
        Assert.Equal("日本語の会話 / 相談", read.Meta.Title);
        Assert.Equal(_clock.Now, meta.CreatedAt);
        Assert.Equal(_clock.Now, meta.UpdatedAt);
        Assert.Empty(read.Entries);
        Assert.Equal(0, read.SkippedLines);
        Assert.True(CompanyPaths.IsValidSlug(meta.Id));
        Assert.Equal(Path.Combine(_workspace.Path, ".company", "secretary", "threads", meta.Id, "meta.json"),
            _workspace.Paths.ThreadMeta(meta.Id));
        Assert.False(File.Exists(_workspace.Paths.ThreadTranscript(meta.Id)));
    }

    [Fact]
    public async Task 日本語のタイトルをエスケープせず同時刻の作成も別のidにする()
    {
        var first = await CreateAsync("秘書と設計の相談");
        var second = await CreateAsync("秘書と設計の相談");
        var json = await File.ReadAllTextAsync(_workspace.Paths.ThreadMeta(first.Id));

        Assert.Contains("秘書と設計の相談", json);
        Assert.DoesNotContain("\\u", json);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(CompanyPaths.IsValidSlug(second.Id));
        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.ThreadDirectory(first.Id), "*.tmp"));
    }

    [Fact]
    public async Task 追記は一発言一行で増えWrittenと保存したUpdatedAtが進む()
    {
        var meta = await CreateAsync("相談");
        var human = new ThreadEntry("human", "日本語の質問\n二行目と\"引用\"", _clock.Now);
        _clock.Now += TimeSpan.FromMinutes(1);
        var first = Assert.IsType<ThreadWriteResult.Written>(await _store.AppendAsync(meta.Id, human, CancellationToken.None));
        var original = await File.ReadAllBytesAsync(_workspace.Paths.ThreadTranscript(meta.Id));
        var secretary = new ThreadEntry("secretary", "回答", _clock.Now);
        _clock.Now += TimeSpan.FromMinutes(1);
        var second = Assert.IsType<ThreadWriteResult.Written>(await _store.AppendAsync(meta.Id, secretary, CancellationToken.None));
        var read = Assert.IsType<ThreadReadResult.Found>(await _store.ReadAsync(meta.Id, CancellationToken.None));
        var bytes = await File.ReadAllBytesAsync(_workspace.Paths.ThreadTranscript(meta.Id));
        var lines = await File.ReadAllLinesAsync(_workspace.Paths.ThreadTranscript(meta.Id));

        Assert.True(first.Meta.UpdatedAt > meta.UpdatedAt);
        Assert.Equal(_clock.Now, second.Meta.UpdatedAt);
        Assert.Equal(second.Meta, read.Meta);
        Assert.Equal(meta.CreatedAt, read.Meta.CreatedAt);
        Assert.Equal(new[] { human, secretary }, read.Entries);
        Assert.Equal(0, read.SkippedLines);
        Assert.Equal(original, bytes[..original.Length]);
        Assert.Equal(2, lines.Length);
        Assert.Equal(human, JsonSerializer.Deserialize<ThreadEntry>(lines[0], TaskStateJson.Options));
        Assert.Contains("日本語の質問", lines[0]);
        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.ThreadDirectory(meta.Id), "*.tmp"));
    }

    [Fact]
    public async Task 一覧は最後に追記した順に並ぶ()
    {
        var first = await CreateAsync("最初");
        _clock.Now += TimeSpan.FromMinutes(1);
        var second = await CreateAsync("次");
        var before = await _store.ListAsync(CancellationToken.None);
        Assert.Equal(new[] { second.Id, first.Id }, before.Threads.Select(meta => meta.Id));
        _clock.Now += TimeSpan.FromMinutes(1);
        await _store.AppendAsync(first.Id, new ThreadEntry("human", "続き", _clock.Now), CancellationToken.None);

        var after = await _store.ListAsync(CancellationToken.None);

        Assert.Equal(new[] { first.Id, second.Id }, after.Threads.Select(meta => meta.Id));
        Assert.Equal(0, after.Unreadable);
    }

    [Fact]
    public async Task 壊れた行だけを飛ばして件数を返す()
    {
        var meta = await CreateAsync("相談");
        var human = new ThreadEntry("human", "質問", _clock.Now);
        var secretary = new ThreadEntry("secretary", "回答", _clock.Now);
        await _store.AppendAsync(meta.Id, human, CancellationToken.None);
        await File.AppendAllTextAsync(_workspace.Paths.ThreadTranscript(meta.Id),
            "{ broken\nnull\n{}\n\n{\"role\":\"unknown\",\"text\":\"?\",\"at\":\"2026-09-09T00:00:00Z\"}\n");
        await _store.AppendAsync(meta.Id, secretary, CancellationToken.None);

        var read = Assert.IsType<ThreadReadResult.Found>(await _store.ReadAsync(meta.Id, CancellationToken.None));

        Assert.Equal(new[] { human, secretary }, read.Entries);
        Assert.Equal(5, read.SkippedLines);
        Assert.Equal(0, (await _store.ListAsync(CancellationToken.None)).Unreadable);
    }

    [Theory]
    [InlineData("{ broken", 1)]
    [InlineData("{\"role\":\"human\",\"text\":\"手編集\",\"at\":\"2026-09-09T00:00:00Z\"}", 0)]
    public async Task 末尾の改行がなくても追記した発言を巻き込まない(string lastLine, int skipped)
    {
        var meta = await CreateAsync("相談");
        await File.WriteAllTextAsync(_workspace.Paths.ThreadTranscript(meta.Id), lastLine);
        var entry = new ThreadEntry("secretary", "続き", _clock.Now);

        Assert.IsType<ThreadWriteResult.Written>(await _store.AppendAsync(meta.Id, entry, CancellationToken.None));
        var read = Assert.IsType<ThreadReadResult.Found>(await _store.ReadAsync(meta.Id, CancellationToken.None));

        Assert.Equal(entry, read.Entries.Last());
        Assert.Equal(skipped, read.SkippedLines);
        Assert.Equal(2 - skipped, read.Entries.Count);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"wrong-id\",\"title\":\"相談\",\"createdAt\":\"2026-09-09T00:00:00Z\",\"updatedAt\":\"2026-09-09T00:00:00Z\"}")]
    public async Task 壊れたmetaはUnreadableで一覧の除外数に載り書き換えない(string json)
    {
        var good = await CreateAsync("正常");
        var broken = await CreateAsync("壊れた会話");
        await File.WriteAllTextAsync(_workspace.Paths.ThreadMeta(broken.Id), json);

        var read = Assert.IsType<ThreadReadResult.Unreadable>(await _store.ReadAsync(broken.Id, CancellationToken.None));
        var list = await _store.ListAsync(CancellationToken.None);

        Assert.NotEmpty(read.Reason);
        Assert.Equal(good, Assert.Single(list.Threads));
        Assert.Equal(1, list.Unreadable);
        Assert.IsType<ThreadWriteResult.Failed>(await _store.AppendAsync(broken.Id,
            new ThreadEntry("human", "追記しない", _clock.Now), CancellationToken.None));
        Assert.IsType<ThreadWriteResult.Failed>(await _store.RenameAsync(broken.Id, "変更しない", CancellationToken.None));
        Assert.Equal(json, await File.ReadAllTextAsync(_workspace.Paths.ThreadMeta(broken.Id)));
        Assert.False(File.Exists(_workspace.Paths.ThreadTranscript(broken.Id)));
    }

    [Fact]
    public async Task 無いスレッドの読み取り追記改名はMissingでファイルを作らない()
    {
        const string id = "missing";

        Assert.IsType<ThreadReadResult.Missing>(await _store.ReadAsync(id, CancellationToken.None));
        Assert.IsType<ThreadWriteResult.Missing>(await _store.AppendAsync(id,
            new ThreadEntry("human", "発言", _clock.Now), CancellationToken.None));
        Assert.IsType<ThreadWriteResult.Missing>(await _store.RenameAsync(id, "変更", CancellationToken.None));
        Assert.False(File.Exists(_workspace.Paths.ThreadTranscript(id)));
        Assert.False(File.Exists(_workspace.Paths.ThreadMeta(id)));
        Assert.False(Directory.Exists(_workspace.Paths.SecretaryThreads));
        var list = await _store.ListAsync(CancellationToken.None);
        Assert.Empty(list.Threads);
        Assert.Equal(0, list.Unreadable);
    }

    [Fact]
    public async Task metaが無いディレクトリも追記で作り直さず一覧の除外数に載せる()
    {
        Directory.CreateDirectory(_workspace.Paths.ThreadDirectory("missing"));

        Assert.IsType<ThreadWriteResult.Missing>(await _store.AppendAsync("missing",
            new ThreadEntry("human", "発言", _clock.Now), CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.ThreadDirectory("missing")));
        Assert.Equal(1, (await _store.ListAsync(CancellationToken.None)).Unreadable);
    }

    [Fact]
    public async Task 追記先が書けなければFailedでmetaの更新時刻は進まない()
    {
        var meta = await CreateAsync("相談");
        var before = await File.ReadAllTextAsync(_workspace.Paths.ThreadMeta(meta.Id));
        Directory.CreateDirectory(_workspace.Paths.ThreadTranscript(meta.Id));
        _clock.Now += TimeSpan.FromMinutes(1);

        var result = Assert.IsType<ThreadWriteResult.Failed>(await _store.AppendAsync(meta.Id,
            new ThreadEntry("human", "発言", _clock.Now), CancellationToken.None));

        Assert.NotEmpty(result.Reason);
        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.ThreadMeta(meta.Id)));
        Assert.IsType<ThreadReadResult.Unreadable>(await _store.ReadAsync(meta.Id, CancellationToken.None));
        Assert.Equal(1, (await _store.ListAsync(CancellationToken.None)).Unreadable);
    }

    [Fact]
    public async Task 改名はタイトルだけを書き換え発言と時刻とidを保つ()
    {
        var meta = await CreateAsync("変更前");
        await _store.AppendAsync(meta.Id, new ThreadEntry("human", "発言", _clock.Now), CancellationToken.None);
        var before = await File.ReadAllTextAsync(_workspace.Paths.ThreadTranscript(meta.Id));
        _clock.Now += TimeSpan.FromMinutes(1);

        var renamed = Assert.IsType<ThreadWriteResult.Written>(await _store.RenameAsync(meta.Id, "変更後の日本語", CancellationToken.None));
        var read = Assert.IsType<ThreadReadResult.Found>(await _store.ReadAsync(meta.Id, CancellationToken.None));

        Assert.Equal(meta with { Title = "変更後の日本語" }, renamed.Meta);
        Assert.Equal(renamed.Meta, read.Meta);
        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.ThreadTranscript(meta.Id)));
        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.ThreadDirectory(meta.Id), "*.tmp"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("日本語")]
    [InlineData("")]
    public async Task 不正なidは結果で拒否しパスにも使わせない(string id)
    {
        Assert.Throws<ArgumentException>(() => _workspace.Paths.ThreadDirectory(id));
        Assert.Throws<ArgumentException>(() => _workspace.Paths.ThreadMeta(id));
        Assert.Throws<ArgumentException>(() => _workspace.Paths.ThreadTranscript(id));
        Assert.IsType<ThreadReadResult.Unreadable>(await _store.ReadAsync(id, CancellationToken.None));
        Assert.IsType<ThreadWriteResult.Failed>(await _store.AppendAsync(id,
            new ThreadEntry("human", "発言", _clock.Now), CancellationToken.None));
        Assert.IsType<ThreadWriteResult.Failed>(await _store.RenameAsync(id, "名前", CancellationToken.None));
        Assert.False(Directory.Exists(_workspace.Paths.SecretaryThreads));
    }

    [Fact]
    public async Task 保存場所がファイルなら作成失敗と一覧の読めない件数を返す()
    {
        Directory.CreateDirectory(_workspace.Paths.SecretaryRoot);
        await File.WriteAllTextAsync(_workspace.Paths.SecretaryThreads, "塞がれている");

        Assert.IsType<ThreadCreateResult.Failed>(await _store.CreateAsync("相談", CancellationToken.None));
        Assert.Equal(1, (await _store.ListAsync(CancellationToken.None)).Unreadable);
    }

    [Fact]
    public async Task キャンセルは伝播しファイルを作らない()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ct = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.CreateAsync("相談", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.ReadAsync("missing", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.ListAsync(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.AppendAsync("missing", new ThreadEntry("human", "発言", _clock.Now), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.RenameAsync("missing", "名前", ct));
        Assert.False(Directory.Exists(_workspace.Paths.SecretaryThreads));
    }

    [Fact]
    public async Task 片付けても消えない_archive_へ移る()
    {
        var meta = await CreateAsync("要らなくなった相談");
        await _store.AppendAsync(meta.Id, new ThreadEntry("human", "こんにちは", _clock.Now), CancellationToken.None);

        Assert.IsType<ThreadWriteResult.Written>(await _store.ArchiveAsync(meta.Id, CancellationToken.None));

        // 一覧からは消える。
        Assert.Empty((await _store.ListAsync(CancellationToken.None)).Threads);
        Assert.IsType<ThreadReadResult.Missing>(await _store.ReadAsync(meta.Id, CancellationToken.None));

        // **中身は残っている**（§16-4）。消えるのは人間の目の前からだけ。
        var moved = Directory.GetDirectories(_workspace.Paths.ArchivedThreads).Single();
        Assert.Contains(meta.Id, Path.GetFileName(moved), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(moved, "transcript.jsonl")));
    }

    [Fact]
    public async Task 無い相談は片付けられない()
    {
        Assert.IsType<ThreadWriteResult.Missing>(await _store.ArchiveAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task 不正な_id_は片付けの入口で弾く()
    {
        // **パスに使う前に弾く**（`RequireSlug` が投げる）。
        Assert.IsType<ThreadWriteResult.Failed>(await _store.ArchiveAsync("../外", CancellationToken.None));
    }

    private async Task<ThreadMeta> CreateAsync(string title) =>
        Assert.IsType<ThreadCreateResult.Created>(await _store.CreateAsync(title, CancellationToken.None)).Meta;

    public void Dispose() => _workspace.Dispose();

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
