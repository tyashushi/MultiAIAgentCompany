using System.Text.RegularExpressions;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Workspace.Trust;

/// <summary><c>~/.codex/config.toml</c> の projects セクションだけを読む trust 検査。</summary>
public sealed partial class CodexCliTrustProbe : IWorkspaceTrustProbe
{
    private readonly string _settingsPath;

    public CodexCliTrustProbe() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public CodexCliTrustProbe(string homeDirectory) =>
        _settingsPath = Path.Combine(Path.GetFullPath(homeDirectory), ".codex", "config.toml");

    public AgentKind Kind => AgentKind.CodexCli;

    public async Task<bool?> IsTrustedAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_settingsPath)) return null;
        try
        {
            var lines = await File.ReadAllLinesAsync(_settingsPath, ct);
            var inTarget = false;
            var foundTarget = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var section = ProjectSection().Match(line);
                if (section.Success)
                {
                    var path = UnescapeTomlBasicString(section.Groups["path"].Value);
                    if (path is null) return null;
                    inTarget = WorkspacePathNormalizer.Equals(path, workspace.Root);
                    foundTarget |= inTarget;
                    continue;
                }
                if (line.StartsWith('['))
                {
                    if (!Section().IsMatch(line)) return null;
                    inTarget = false;
                    continue;
                }
                if (!KeyValue().IsMatch(line)) return null;
                if (inTarget && line.StartsWith("trust_level", StringComparison.Ordinal))
                {
                    var match = TrustLevel().Match(line);
                    if (!match.Success) return null;
                    return string.Equals(match.Groups["value"].Value, "trusted", StringComparison.Ordinal);
                }
            }
            return foundTarget ? null : false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? UnescapeTomlBasicString(string value)
    {
        // trust の設定が出す通常の quoted key に限定する。未知のエスケープは解釈しない。
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                result.Append(value[index]);
                continue;
            }
            if (++index == value.Length) return null;
            result.Append(value[index] switch
            {
                '\\' => '\\', '"' => '"', 'b' => '\b', 't' => '\t',
                'n' => '\n', 'f' => '\f', 'r' => '\r', _ => '\0',
            });
            if (result[^1] == '\0') return null;
        }
        return result.ToString();
    }

    [GeneratedRegex("^\\[projects\\.\\\"(?<path>(?:\\\\.|[^\\\"\\\\])*)\\\"\\]\\s*(?:#.*)?$")]
    private static partial Regex ProjectSection();
    [GeneratedRegex("^\\[[A-Za-z0-9_.-]+\\]\\s*(?:#.*)?$")]
    private static partial Regex Section();
    [GeneratedRegex("^[A-Za-z0-9_.-]+\\s*=.*$")]
    private static partial Regex KeyValue();
    [GeneratedRegex("^trust_level\\s*=\\s*\\\"(?<value>[^\\\"]*)\\\"\\s*(?:#.*)?$")]
    private static partial Regex TrustLevel();
}
