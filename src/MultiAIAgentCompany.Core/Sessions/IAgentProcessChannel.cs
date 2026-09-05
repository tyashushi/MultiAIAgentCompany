namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>行単位の双方向チャネル。実プロセスとテスト用の偽物の共通境界。</summary>
public interface IAgentProcessChannel : IAsyncDisposable
{
    ProcessIdentity Identity { get; }

    /// <summary>stdout を最後まで1行ずつ読む。止めると子プロセスのパイプが詰まる。</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>入力停止、正常終了待機、プロセスツリー停止の順に終了する。</summary>
    Task StopAsync(CancellationToken ct);

    event EventHandler<int>? Exited;
}
