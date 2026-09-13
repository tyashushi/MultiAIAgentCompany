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
            // **git は objects を読み取り専用で書く。** Windows ではその属性が付いたファイルを
            // Directory.Delete が消せないので、先に外す（macOS では属性が削除を妨げない）。
            if (OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
