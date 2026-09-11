using System.Text.Json;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Agents.Antigravity;

/// <summary>Antigravity stream-json の常駐プロセスを扱う構造化セッション。</summary>
public sealed class AntigravitySession : IStructuredSession
{
    private readonly IAgentProcessChannel _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DiagnosticsLog _diagnostics = new();
    private readonly Task _readLoop;
    private readonly object _stopLock = new();
    private Task? _stopTask;
    private bool _sawStepError;
    private int _disposed;

    public AntigravitySession(IAgentProcessChannel channel, string departmentId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        DepartmentId = departmentId ?? throw new ArgumentNullException(nameof(departmentId));
        _channel.Exited += ChannelExited;
        _channel.StandardErrorLine += StandardErrorLine;

        // **stdin に書く口をここ1つに閉じる**（設計 §32-12）。
        _turns = new TurnGate((line, token) => WriteAsync(
            JsonSerializer.Serialize(new { @event = "user", message = new { role = "user", content = line } }), token));
        _readLoop = ReadLoopAsync();
    }

    public string DepartmentId { get; }
    public ProcessIdentity Identity => _channel.Identity;
    public DriveMode Mode => DriveMode.Structured;
    /// <summary>init に CLI 版が無いため、版はストリームからは検出しない。</summary>
    public string? DetectedVersion => null;

    /// <inheritdoc />
    /// <remarks>
    /// <b>Antigravity は init に model を含めない</b>（実測 §27）。
    /// こちらが <c>--model</c> で渡した値で埋めない —— それは要求であって観測ではない。
    /// </remarks>
    public AgentModel? ObservedModel => null;

    public event EventHandler<Evidence>? Observed;
    public event EventHandler<int>? Exited;

    /// <inheritdoc />
    public event EventHandler<LiveDiagnostic>? Diagnosed;
    // Antigravity にはこの種の通知が存在しない。IStructuredSession の契約を満たすだけで発火しない。
    public event EventHandler<ApprovalRequest>? ApprovalRequested { add { } remove { } }
    public event EventHandler<OutcomeVerdict>? TurnFinished;

    /// <inheritdoc />
    public event EventHandler<LiveAgentMessage>? Spoke;

    private readonly TurnGate _turns;

    /// <inheritdoc />
    public TurnActivity Activity => _turns.Activity;

    /// <inheritdoc />
    /// <remarks><b>書き込み口は <see cref="TurnGate"/> ひとつ</b>（設計 §32-12）。</remarks>
    public Task<SendOutcome> SendUserMessageAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _turns.SendAsync(text, ct);
    }

    public Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, string? reason, CancellationToken ct) =>
        throw new NotSupportedException("Antigravity にはランタイム承認の往復が無いため、承認応答は送れません。");

    private async Task WriteAsync(string line, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _channel.WriteLineAsync(line, ct).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var line in _channel.ReadLinesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                switch (AntigravityStreamReader.ReadLine(line))
                {
                    case AntigravityEvent.Init init:
                        Observe($"Antigravity 初期化。cwd={init.Cwd ?? "不明"}; permission_mode={init.PermissionMode ?? "不明"}");
                        break;
                    case AntigravityEvent.StepUpdate { State: "ERROR" }:
                        _sawStepError = true;
                        break;
                    case AntigravityEvent.Finished finished:
                        // **Antigravity の protocol に delta イベントは存在しない**
                        // （§13-3。読めるのは init / step_update / result だけ）。
                        // だから `result.response` をそのまま出してよい。
                        //
                        // **delta を足すときは、ここも同時に塞ぐこと** ——
                        // 受けた delta を出したうえでここも出すと二重表示になる。
                        // 以前は「delta を見たか」の bool を置いてあったが、
                        // **一度も true にならないので嘘の番人だった**（CS0649）。
                        // 番人は、実際に見張れるようになった時に戻す（2026-09-09）。
                        if (finished.Response.Length > 0)
                        {
                            SafeInvoke(() => Spoke?.Invoke(this, new LiveAgentMessage(finished.Response)), "Spoke");
                        }

                        var sawStepError = _sawStepError;
                        _sawStepError = false;
                        var verdict = AntigravityTurnOutcome.ToSignals(finished, sawStepError)
                            .Judge(OutcomeRequirement.For(AgentKind.AntigravityCli));
                        SafeInvoke(() => TurnFinished?.Invoke(this, verdict), "TurnFinished");

                        // **失敗した turn でも開ける**（§32-12）。開けないと以後が永久に積まれる。
                        await _turns.OnTurnFinishedAsync(CancellationToken.None).ConfigureAwait(false);
                        break;
                    case AntigravityEvent.Unknown unknown:
                        Observe($"Antigravity イベントを解釈できない: {unknown.Reason}");
                        break;
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void StandardErrorLine(object? sender, string line)
    {
        // CLI は未知の input event を stdout に何も返さず stderr にだけ警告する（実測 §13-3 追記2）。
        // その事実は人間に見せたいが、**行をそのまま Evidence へ流さない** ——
        // stderr には作業パス・コマンド・スタックトレース・秘密値が混ざり得るのに、
        // RedactedSummary は「秘密値を含まない、永続しうる要約」として定義されている（§10）。
        // だから中身をコピーせず、**分類だけ**を出す。
        Observe(ClassifyStandardError(line));

        // 分類は永続してよい要約、こちらは中身（設計 §22）。**両方を出す** ——
        // 分類だけでは「送ったのに何も起きない」の原因に辿り着けない。
        // 取り置くのは、購読より前（起動の失敗）を取りこぼさないため（§22-2）。
        var diagnostic = new LiveDiagnostic(DiagnosticStream.StandardError, line);
        _diagnostics.Add(diagnostic);
        SafeInvoke(() => Diagnosed?.Invoke(this, diagnostic), "Diagnosed");
    }

    /// <inheritdoc />
    public IReadOnlyList<LiveDiagnostic> RecentDiagnostics(int count) => _diagnostics.Recent(count);

    /// <summary>
    /// stderr の1行を、人間に見せてよい分類に落とす。<b>行の中身を含めない。</b>
    /// </summary>
    public static string ClassifyStandardError(string line)
    {
        if (line.Contains("auto-denied", StringComparison.Ordinal))
        {
            return "承認できないツールが自動拒否された（何が拒否されたかは denied_actions を見る）";
        }

        if (line.Contains("unsupported stream input message event", StringComparison.Ordinal))
        {
            // event 名はこちらが送った値なので、含めても外から来た秘密ではない。
            var name = ExtractQuoted(line);
            return name is null
                ? "送ったメッセージが未知の event として捨てられた"
                : $"送ったメッセージが未知の event として捨てられた: {name}";
        }

        // 分類できないものは、あったことだけを言う。中身は言わない。
        return $"Antigravity の stderr に未分類の出力（{line.Length} 文字）";
    }

    private static string? ExtractQuoted(string line)
    {
        var start = line.IndexOf('"');
        if (start < 0)
        {
            return null;
        }

        var end = line.IndexOf('"', start + 1);
        return end > start ? line[(start + 1)..end] : null;
    }

    private void SafeInvoke(Action invoke, string eventName)
    {
        try { invoke(); }
        catch (Exception exception) { Observe($"{eventName} の購読側が例外を投げた: {exception.GetType().Name}（読み取りは継続する）"); }
    }

    private void Observe(string summary)
    {
        try
        {
            Observed?.Invoke(this, new Evidence(EvidenceSource.StructuredEvent, DateTimeOffset.UtcNow, null, null,
                new AgentRef(DepartmentId, AgentKind.AntigravityCli), DetectedVersion, null, summary));
        }
        catch (Exception) { }
    }

    private void ChannelExited(object? sender, int exitCode) => SafeInvoke(() => Exited?.Invoke(this, exitCode), "Exited");

    public Task StopAsync(CancellationToken ct)
    {
        lock (_stopLock) _stopTask ??= _channel.StopAsync(CancellationToken.None);
        return _stopTask.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _channel.Exited -= ChannelExited;
            _channel.StandardErrorLine -= StandardErrorLine;
            await _channel.DisposeAsync().ConfigureAwait(false);
            _writeGate.Dispose();
        }
        await _readLoop.ConfigureAwait(false);
    }
}
