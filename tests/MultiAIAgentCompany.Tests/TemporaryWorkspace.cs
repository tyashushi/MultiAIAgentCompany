using MultiAIAgentCompany.Core.Coordination;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// テスト用の一時ワークスペース。<b>実物の `~` やリポジトリに触らない</b>。
/// </summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"multi-ai-agent-company-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        Paths = new CompanyPaths(Path);
    }

    public string Path { get; }
    public CompanyPaths Paths { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
