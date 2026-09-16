using System.Security.Cryptography;
using System.Text;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Activity;

/// <summary>永続化せず、起動要求とセッションだけで持つ記録の場所（設計 §61-2）。</summary>
public sealed record ActivityLaunch(string DataRoot, string DirectoryPath, string DepartmentId)
{
    public string EventsPath => Path.Combine(DirectoryPath, "events");
    public string AgyLogPath => Path.Combine(DirectoryPath, "agy.log");
    public string AgyHooksDirectory => Path.Combine(DataRoot, "agy-hooks");
    public IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>
    {
        ["MAAC_DEPARTMENT"] = DepartmentId,
        ["MAAC_ACTIVITY_EVENTS"] = EventsPath,
    };

    public static string WorkspaceDirectory(string dataRoot, string workspaceRoot) => Path.Combine(dataRoot, "activity",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspaceRoot))))[..16]);

    public static ActivityLaunch Create(string dataRoot, string workspaceRoot, string departmentId, DateTimeOffset now) =>
        new(Path.GetFullPath(dataRoot), Path.Combine(WorkspaceDirectory(Path.GetFullPath(dataRoot), workspaceRoot),
            departmentId, $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}"), departmentId);

    public void Prepare(AgentKind kind)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (kind is not AgentKind.AntigravityCli) return;
        var path = Path.Combine(AgyHooksDirectory, ".agents", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != ActivityHooks.AgyHooksJson)
            File.WriteAllText(path, ActivityHooks.AgyHooksJson, new UTF8Encoding(false));
    }

    public void Delete() => TryDelete(DirectoryPath);

    /// <summary>別ワークスペースと、更新から7日以内の起動は触らない（設計 §61-2）。</summary>
    public static void Cleanup(string dataRoot, string workspaceRoot, DateTimeOffset now)
    {
        try
        {
            var root = WorkspaceDirectory(dataRoot, workspaceRoot);
            if (!Directory.Exists(root)) return;
            foreach (var department in Directory.GetDirectories(root))
            foreach (var launch in Directory.GetDirectories(department))
            {
                var modified = Directory.GetLastWriteTimeUtc(launch);
                foreach (var file in Directory.GetFiles(launch, "*", SearchOption.AllDirectories))
                    modified = new[] { modified, File.GetLastWriteTimeUtc(file) }.Max();
                if (modified < now.UtcDateTime.AddDays(-7)) TryDelete(launch);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 片付けられなくてもワークスペースを開く（設計 §61-2）。
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 外部プロセスが触っていてもアプリを落とさない（設計 §61-2）。
        }
    }
}
