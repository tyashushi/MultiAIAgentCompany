using System.Text.Json;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Agents.CodexCli;

/// <summary>Codex app-server の JSON-RPC を実プロセスのチャネル上で扱う構造化セッション。</summary>
public sealed class CodexAppServerSession : IStructuredSession
{
    private readonly IAgentProcessChannel _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, CodexApprovalRequest> _approvalRequests = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readLoop;
    private readonly Task _initialization;
    private readonly object _stopLock = new();
    private Task? _stopTask;
    private long _nextRpcId;
    private string? _threadId;
    /// <summary>
    /// 直近の turn で観測したコマンド実行 item の状態。
    /// <b>turn ごとに捨てる。</b> 残すと、拒否された turn の "declined" が
    /// 次の turn の判定を汚し、成功した仕事が失敗として表示される。
    /// </summary>
    private string? _commandItemStatus;
    private int _disposed;

    public CodexAppServerSession(IAgentProcessChannel channel, string departmentId, string workspaceRoot, string model)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        DepartmentId = departmentId ?? throw new ArgumentNullException(nameof(departmentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _channel.Exited += ChannelExited;
        _readLoop = ReadLoopAsync();
        _initialization = InitializeAsync(workspaceRoot, model);
    }

    public string DepartmentId { get; }
    public ProcessIdentity Identity => _channel.Identity;
    public DriveMode Mode => DriveMode.Structured;
    public string? DetectedVersion { get; private set; }

    public event EventHandler<Evidence>? Observed;
    public event EventHandler<int>? Exited;
    public event EventHandler<ApprovalRequest>? ApprovalRequested;
    public event EventHandler<OutcomeVerdict>? TurnFinished;

    /// <inheritdoc />
    public event EventHandler<LiveAgentMessage>? Spoke;

    /// <summary>initialize / initialized / thread/start が終わるまで待つ。</summary>
    public async Task CompleteHandshakeAsync(CancellationToken ct)
    {
        await _initialization.WaitAsync(ct).ConfigureAwait(false);
        await _threadStarted.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task SendUserMessageAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        await CompleteHandshakeAsync(ct).ConfigureAwait(false);
        await WriteRequestAsync("turn/start", new
        {
            threadId = _threadId!,
            input = new[] { new { type = "text", text } },
        }, ct).ConfigureAwait(false);
    }

    public async Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        if (!request.Offers(decision.Id))
        {
            throw new ArgumentException("要求が提示していない決定です。", nameof(decision));
        }
        if (!_approvalRequests.TryGetValue(request.RequestId, out var codexRequest))
        {
            throw new InvalidOperationException("対応する Codex の承認要求が見つかりません。");
        }

        await WriteAsync(CodexApprovalResponse.Build(codexRequest, decision.Id), ct).ConfigureAwait(false);
        if (reason is not null)
        {
            Observe($"Codex の承認応答の理由（Codex には送信先がない）: {reason}");
        }
    }

    private async Task InitializeAsync(string workspaceRoot, string model)
    {
        try
        {
            await WriteRequestAsync("initialize", new
            {
                clientInfo = new { name = "MultiAIAgentCompany", version = "0.1" },
                capabilities = new { experimentalApi = true },
            }, CancellationToken.None).ConfigureAwait(false);
            await _initialized.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _threadStarted.TrySetException(exception);
            return;
        }

        // initialized は JSON-RPC notification なので id を消費しない。
        try
        {
            await WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "initialized" }), CancellationToken.None).ConfigureAwait(false);
            await WriteRequestAsync("thread/start", new
            {
                cwd = workspaceRoot,
                model,
                approvalPolicy = "on-request",
                approvalsReviewer = "user",
                config = new { sandbox_mode = "workspace-write" },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _threadStarted.TrySetException(exception);
        }
    }

    private async Task WriteRequestAsync(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRpcId);
        await WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }), ct).ConfigureAwait(false);
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
                var message = CodexMessageReader.ReadLine(line);
                switch (message)
                {
                    case CodexMessage.Response response:
                        HandleResponse(response, line);
                        break;
                    case CodexMessage.ApprovalAsked approval:
                        _approvalRequests[approval.Request.RpcId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = approval.Request;
                        SafeInvoke(() => ApprovalRequested?.Invoke(this, approval.Request.ToApprovalRequest()), "ApprovalRequested");
                        break;
                    case CodexMessage.UnknownServerRequest request:
                        try { await WriteAsync(CodexApprovalResponse.BuildMethodNotFound(request.Id, request.Method), CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) { Observe($"未知のサーバ要求への応答送信に失敗: {exception.GetType().Name}"); }
                        Observe($"知らないサーバ要求が来た: method={request.Method}");
                        break;
                    case CodexMessage.ThreadStatusChanged { WaitingOnApproval: true }:
                        Observe("Codex は承認待ちであると通知した。");
                        break;
                    case CodexMessage.ItemCompleted { Status: "completed" or "declined" } item:
                        _commandItemStatus = item.Status;
                        break;
                    case CodexMessage.TurnFinished turn:
                        var commandStatus = _commandItemStatus;
                        _commandItemStatus = null;   // turn 境界で捨てる
                        var verdict = CodexTurnOutcome.ToSignals(turn.Status, commandStatus)
                            .Judge(OutcomeRequirement.For(AgentKind.CodexCli));
                        SafeInvoke(() => TurnFinished?.Invoke(this, verdict), "TurnFinished");
                        break;
                    case CodexMessage.McpServerStatus mcp:
                        Observe($"Codex MCP サーバー: {mcp.Name ?? "名前不明"} ({mcp.Status ?? "状態不明"})");
                        break;
                    case CodexMessage.Notification notification
                        when TryReadAgentText(line) is { Length: > 0 } spoken:
                        // **ライブ表示専用**（設計 §17-5）。Observed には出さない。
                        SafeInvoke(() => Spoke?.Invoke(this, new LiveAgentMessage(spoken)), "Spoke");
                        break;
                    case CodexMessage.Notification notification:
                        Observe($"Codex 通知: method={notification.Method}");
                        break;
                    case CodexMessage.Unknown unknown:
                        // RawFirst200 は秘密を含み得るので Evidence に絶対に流さない。
                        Observe($"Codex イベントを解釈できない: {unknown.Reason}");
                        break;
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void HandleResponse(CodexMessage.Response response, string line)
    {
        if (response.ErrorJson is not null)
        {
            Observe($"Codex JSON-RPC 応答エラー: id={response.Id}");
            if (response.Id is 1 or 2)
            {
                var exception = new InvalidOperationException("Codex 握手が JSON-RPC エラーで失敗しました。");
                _initialized.TrySetException(exception);
                _threadStarted.TrySetException(exception);
            }
            return;
        }

        if (response.Id == 1)
        {
            _initialized.TrySetResult();
            return;
        }

        if (response.Id == 2 && TryGetThreadId(line, out var threadId))
        {
            _threadId = threadId;

            // 版はここでしか取れない。検出器は版依存（設計 §7）なので、
            // 取れるのに取らないと「どの版のプロトコルを読んでいるか」が分からないまま動く。
            DetectedVersion = TryGetThreadString(line, "cliVersion");

            Observe($"Codex app-server 起動。版={DetectedVersion ?? "不明"}");
            _threadStarted.TrySetResult();
        }
    }

    /// <summary>`result.thread.&lt;name&gt;` の文字列を取り出す。無ければ null。</summary>
    private static string? TryGetThreadString(string line, string name)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("thread", out var thread)
                && thread.TryGetProperty(name, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // 解釈層は Response の成否だけを表す。握手を進めるため thread.id だけをここで取り出す。
    /// <summary>
    /// <c>item/completed</c> の <c>agentMessage</c> から本文を取る（設計 §17-5）。
    /// <b>ライブ表示専用。</b> 見つからなければ null。
    /// </summary>
    private static string? TryReadAgentText(string line)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("item", out var item)
                && item.TryGetProperty("type", out var type)
                && type.ValueKind == System.Text.Json.JsonValueKind.String
                && type.GetString() == "agentMessage"
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == System.Text.Json.JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool TryGetThreadId(string line, out string? threadId)
    {
        threadId = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("thread", out var thread)
                && thread.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                threadId = id.GetString();
                return !string.IsNullOrWhiteSpace(threadId);
            }
        }
        catch (JsonException) { }
        return false;
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
            Observed?.Invoke(this, new Evidence(EvidenceSource.StructuredEvent, DateTimeOffset.UtcNow, _threadId, null,
                new AgentRef(DepartmentId, AgentKind.CodexCli), DetectedVersion, null, summary));
        }
        catch (Exception) { }
    }

    private void ChannelExited(object? sender, int exitCode) => SafeInvoke(() => Exited?.Invoke(this, exitCode), "Exited");

    public Task StopAsync(CancellationToken ct)
    {
        lock (_stopLock) { _stopTask ??= _channel.StopAsync(CancellationToken.None); }
        return _stopTask.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _channel.Exited -= ChannelExited;
            await _channel.DisposeAsync().ConfigureAwait(false);
            _writeGate.Dispose();
        }
        await _readLoop.ConfigureAwait(false);
    }
}
