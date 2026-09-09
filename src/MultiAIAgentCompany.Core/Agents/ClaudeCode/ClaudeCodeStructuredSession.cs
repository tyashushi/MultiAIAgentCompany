using System.Text.Json;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Agents.ClaudeCode;

/// <summary>Claude Code の stream-json を、実プロセスから独立したチャネル上で動かすセッション。</summary>
public sealed class ClaudeCodeStructuredSession : IStructuredSession
{
    private readonly IAgentProcessChannel _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DiagnosticsLog _diagnostics = new();
    private readonly Dictionary<string, ClaudeApprovalRequest> _approvalRequests = new(StringComparer.Ordinal);
    private readonly Task _readLoop;
    private readonly object _stopLock = new();
    private Task? _stopTask;
    private int _disposed;

    public ClaudeCodeStructuredSession(IAgentProcessChannel channel, string departmentId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        DepartmentId = departmentId ?? throw new ArgumentNullException(nameof(departmentId));
        _channel.Exited += ChannelExited;
        _channel.StandardErrorLine += StandardErrorLine;
        _readLoop = ReadLoopAsync();
    }

    public string DepartmentId { get; }
    public ProcessIdentity Identity => _channel.Identity;
    public DriveMode Mode => DriveMode.Structured;
    public string? DetectedVersion { get; private set; }

    /// <inheritdoc />
    /// <remarks>Claude は思考の強さを返してこないので、そこは常に null（実測 §27）。</remarks>
    public AgentModel? ObservedModel { get; private set; }

    public event EventHandler<Evidence>? Observed;
    public event EventHandler<int>? Exited;

    /// <inheritdoc />
    public event EventHandler<LiveDiagnostic>? Diagnosed;
    public event EventHandler<ApprovalRequest>? ApprovalRequested;
    public event EventHandler<OutcomeVerdict>? TurnFinished;

    /// <inheritdoc />
    public event EventHandler<LiveAgentMessage>? Spoke;

    public Task SendUserMessageAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } }), ct);
    }

    public async Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        if (!request.Offers(decision.Id))
        {
            throw new ArgumentException("要求が提示していない決定です。", nameof(decision));
        }
        if (!_approvalRequests.TryGetValue(request.RequestId, out var claudeRequest))
        {
            throw new InvalidOperationException("対応する Claude の承認要求が見つかりません。");
        }

        await WriteAsync(ClaudeControlResponse.Build(claudeRequest, decision.Id, reason), ct).ConfigureAwait(false);
    }

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
                switch (ClaudeStreamReader.ReadLine(line))
                {
                    case ClaudeEvent.Init init:
                        DetectedVersion = init.Version;
                        ObservedModel = init.Model is { } model ? new AgentModel(model, null) : null;
                        var mcp = init.McpServers.Count == 0
                            ? "MCP サーバーなし"
                            : $"MCP サーバー: {string.Join(", ", init.McpServers.Select(server => $"{server.Name} ({server.Status ?? "状態不明"})"))}";
                        Observe($"Claude Code 初期化。版={init.Version ?? "不明"}; {mcp}");
                        break;
                    case ClaudeEvent.ApprovalAsked approval:
                        _approvalRequests[approval.Request.RequestId] = approval.Request;
                        var request = approval.Request.ToApprovalRequest();
                        SafeInvoke(() => ApprovalRequested?.Invoke(this, request), "ApprovalRequested");
                        break;
                    case ClaudeEvent.TurnFinished finished:
                        var verdict = ClaudeTurnOutcome.ToSignals(finished)
                            .Judge(OutcomeRequirement.For(AgentKind.ClaudeCode));

                        // **分かっている手がかりを、人間に届ける**（設計 §13、2026-09-09）。
                        // 判定（どの層が落ちたか）だけでは動けない —— API エラーの番号は
                        // ここでしか分からないのに、捨てていた。
                        if (!verdict.Succeeded && ClaudeTurnOutcome.DescribeFailure(finished) is { } detail)
                        {
                            verdict = verdict with { Reason = $"{verdict.Reason}（{detail}）" };

                            // 診断にも出す。**永続させない**（§10 / §22）。
                            Diagnose(DiagnosticStream.Protocol, $"turn が失敗した: {detail}");
                        }

                        SafeInvoke(() => TurnFinished?.Invoke(this, verdict), "TurnFinished");
                        break;
                    case ClaudeEvent.AssistantSpoke spoke:
                        // **ライブ表示専用**（設計 §17-5）。Observed には出さない ——
                        // Evidence は「秘密値を入れない要約」で、こちらは中身そのもの。
                        foreach (var text in spoke.TextBlocks)
                        {
                            SafeInvoke(() => Spoke?.Invoke(this, new LiveAgentMessage(text)), "Spoke");
                        }

                        break;
                    case ClaudeEvent.Passthrough passthrough:
                        Observe($"Claude Code イベント: type={passthrough.Type}, subtype={passthrough.Subtype ?? "なし"}");
                        break;
                    case ClaudeEvent.Unknown unknown:
                        // RawFirst200 は秘密を含み得るので Evidence に絶対に流さない。
                        Observe($"Claude Code イベントを解釈できない: {unknown.Reason}");
                        break;
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 購読側の例外で<b>読み取りループを止めない</b>。
    /// </summary>
    /// <remarks>
    /// 止まると stdout が排出されなくなり、パイプが詰まって子プロセスが停止する
    /// （設計 §5「読み取りを止めると子プロセスが停止する」はパイプにも当てはまる）。
    /// しかも<b>誰も何も言わないまま止まる</b>ので、このアプリが繰り返し踏んでいる形になる。
    /// <para>
    /// ただし握りつぶさない。例外が出たことは観測として残す。
    /// <b>例外のメッセージは入れない</b>（何が入っているか分からない。設計 §10）。型名だけ。
    /// </para>
    /// </remarks>
    private void SafeInvoke(Action invoke, string eventName)
    {
        try
        {
            invoke();
        }
        catch (Exception exception)
        {
            Observe($"{eventName} の購読側が例外を投げた: {exception.GetType().Name}（読み取りは継続する）");
        }
    }

    private void Observe(string summary)
    {
        try
        {
            Observed?.Invoke(this, new Evidence(
                EvidenceSource.StructuredEvent, DateTimeOffset.UtcNow, null, null,
                new AgentRef(DepartmentId, AgentKind.ClaudeCode), DetectedVersion, null, summary));
        }
        catch (Exception)
        {
            // Observed の購読側が壊れている。ここで報告する先が無いので、読み取りだけは守る。
        }
    }

    /// <summary>
    /// stderr を診断ビューへ流す（設計 §22）。
    /// </summary>
    /// <remarks>
    /// <b>ここでは分類しない。</b> 分類（永続してよい要約）は <c>Observed</c> の仕事で、
    /// こちらは<b>中身</b>を運ぶ。画面にだけ出し、保存しない（§10）。
    /// </remarks>
    /// <summary>
    /// アプリ側で分かった経路の異常を、診断へ出す（設計 §22）。
    /// </summary>
    /// <remarks><b>永続させない</b>（§10）—— <c>Evidence</c> には渡さない。</remarks>
    private void Diagnose(DiagnosticStream stream, string text)
    {
        var diagnostic = new LiveDiagnostic(stream, text);
        _diagnostics.Add(diagnostic);
        SafeInvoke(() => Diagnosed?.Invoke(this, diagnostic), "Diagnosed");
    }

    private void StandardErrorLine(object? sender, string line)
    {
        // **購読より前の分も残す**（設計 §22-2）—— 起動の失敗はここに出る。
        var diagnostic = new LiveDiagnostic(DiagnosticStream.StandardError, line);
        _diagnostics.Add(diagnostic);

        // **購読側の例外で stderr の読み出しを止めない。** 止まるとパイプが詰まって
        // 子プロセスが停止する（stdout 側が SafeInvoke を使っているのと同じ理由）。
        SafeInvoke(() => Diagnosed?.Invoke(this, diagnostic), "Diagnosed");
    }

    /// <inheritdoc />
    public IReadOnlyList<LiveDiagnostic> RecentDiagnostics(int count) => _diagnostics.Recent(count);

    private void ChannelExited(object? sender, int exitCode) => Exited?.Invoke(this, exitCode);

    public Task StopAsync(CancellationToken ct)
    {
        lock (_stopLock)
        {
            _stopTask ??= _channel.StopAsync(CancellationToken.None);
        }
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
