using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Workspace.Trust;

/// <summary><c>~/.claude.json</c> を読むだけの Claude Code trust 検査。</summary>
public sealed class ClaudeCodeTrustProbe : IWorkspaceTrustProbe
{
    private readonly string _settingsPath;

    public ClaudeCodeTrustProbe() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    /// <summary>テスト用にホームディレクトリを差し替えられる。</summary>
    public ClaudeCodeTrustProbe(string homeDirectory) =>
        _settingsPath = Path.Combine(Path.GetFullPath(homeDirectory), ".claude.json");

    public AgentKind Kind => AgentKind.ClaudeCode;

    public async Task<bool?> IsTrustedAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_settingsPath)) return null;
        try
        {
            await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Object)
                return null;

            bool? answer = false;
            foreach (var project in projects.EnumerateObject())
            {
                // 自分のパス以外でも、形が想定と違えば「このファイルを読めている」とは言えない。
                // 版が変わった可能性があるので、そこで false（未 trust）と答えない（§13-9 規則1）。
                if (project.Value.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!WorkspacePathNormalizer.Equals(project.Name, workspace.Root))
                {
                    continue;
                }

                if (!project.Value.TryGetProperty("hasTrustDialogAccepted", out var trusted)
                    || (trusted.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
                {
                    return null;
                }

                answer = trusted.GetBoolean();
            }

            return answer;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
