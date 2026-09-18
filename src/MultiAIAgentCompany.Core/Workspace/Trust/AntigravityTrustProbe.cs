using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Workspace.Trust;

/// <summary><c>~/.gemini/antigravity-cli/settings.json</c> を読むだけの trust 検査。</summary>
public sealed class AntigravityTrustProbe : IWorkspaceTrustProbe
{
    private readonly string _settingsPath;

    public AntigravityTrustProbe() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public AntigravityTrustProbe(string homeDirectory) => _settingsPath = Path.Combine(
        Path.GetFullPath(homeDirectory), ".gemini", "antigravity-cli", "settings.json");

    public AgentKind Kind => AgentKind.AntigravityCli;

    /// <summary>ファイルが無いか、<c>trustedWorkspaces</c> の欄が無い（一度も信頼を与えていないと <c>{}</c> だった。§55-5）。</summary>
    public async Task<bool> HasNoRecordAsync(CancellationToken ct)
    {
        if (!File.Exists(_settingsPath)) return true;
        try
        {
            await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && !document.RootElement.TryGetProperty("trustedWorkspaces", out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

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
                || !document.RootElement.TryGetProperty("trustedWorkspaces", out var workspaces)
                || workspaces.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in workspaces.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                if (WorkspacePathNormalizer.Equals(item.GetString()!, workspace.Root)) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
