using System.Diagnostics;

namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>シェルを介さずに起動した子プロセスの、行単位パイプ実装。</summary>
public sealed class ChildProcessChannel : IAgentProcessChannel
{
    private readonly Process _process;
    private readonly TimeSpan _gracePeriod;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private Task? _stopTask;
    private int _disposed;

    private ChildProcessChannel(Process process, TimeSpan gracePeriod)
    {
        _process = process;
        _gracePeriod = gracePeriod;
        // パイプ経路には PTY の SID / PGID は無い。嘘の値を作らず 0 を入れる。
        Identity = new ProcessIdentity(process.Id, 0, 0, process.StartTime.ToUniversalTime());
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, _process.ExitCode);
        _ = DrainStandardErrorAsync();
    }

    public ProcessIdentity Identity { get; }

    /// <summary>stderr の行。stdout の構造化ストリームとは混ぜず、観測材料として公開する。</summary>
    public event EventHandler<string>? StandardErrorLine;

    public event EventHandler<int>? Exited;

    public static Task<IAgentProcessChannel> StartAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan? gracePeriod = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            // **見つけた実体で起動する**（設計 §28-1）。名前のままだと、GUI 起動の
            // 最小限の PATH では OS が見つけられない。見つからなければ名前のまま渡す。
            FileName = Agents.AgentExecutable.ResolveCommand(fileName),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = ProcessEncoding.Utf8,
            StandardOutputEncoding = ProcessEncoding.Utf8,
            StandardErrorEncoding = ProcessEncoding.Utf8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("子プロセスを開始できませんでした。");
        }

        return Task.FromResult<IAgentProcessChannel>(new ChildProcessChannel(process, gracePeriod ?? TimeSpan.FromSeconds(5)));
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

    public Task WriteLineAsync(string line, CancellationToken ct) => WriteLineCoreAsync(line, ct);

    private async Task WriteLineCoreAsync(string line, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task stopTask;
        await _stopGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            stopTask = _stopTask ??= StopCoreAsync();
        }
        finally
        {
            _stopGate.Release();
        }
        await stopTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        try { _process.StandardInput.Close(); } catch (InvalidOperationException) { }
        if (!_process.HasExited)
        {
            try
            {
                await _process.WaitForExitAsync().WaitAsync(_gracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (_process.HasExited)
                    {
                        // 猶予が切れた直後、Kill を呼ぶ前に子が自分で終わった。
                        // 期待どおりに終わっているので、これは失敗ではない。
                    }

                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                StandardErrorLine?.Invoke(this, line);
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// <b>破棄は段階的な停止を含む。</b> Process オブジェクトを捨てるだけでは子は生き残る。
    /// </summary>
    /// <remarks>
    /// 起動直後の失敗（握手のエラー、キャンセル）でここに来ることがあり、
    /// そこで殺し損ねると <c>claude</c> や <c>codex app-server</c> が孤児として残る（設計 §9）。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 破棄の途中で投げない。落とせなかったことより、後始末を続ける方が大事。
        }

        _process.Dispose();
        _stopGate.Dispose();
    }
}
