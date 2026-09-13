using System.Diagnostics;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// テストでフォルダへのリンクを作る。
/// </summary>
/// <remarks>
/// <b>Windows ではシンボリックリンクに特権が要る</b>（開発者モードか管理者）。
/// 無いときは<b>ジャンクションに落とす</b> —— 特権が要らず、
/// <c>ResolveLinkTarget</c> も同じように辿るので、確かめたいこと（綴りが違っても実体で比べる）は変わらない。
/// </remarks>
internal static class DirectoryLinks
{
    public static void Create(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            CreateJunction(link, target);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            CreateJunction(link, target);
        }
    }

    /// <summary>
    /// リンクを含むフォルダを消す。
    /// </summary>
    /// <remarks>
    /// <b>ジャンクションを含んだまま再帰で消すと、.NET 10 は Access denied で止まる</b>（実測）。
    /// リンクだけ先に外せば、残りはふつうに消える。
    /// </remarks>
    public static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = 0,
                 }).ToList())
        {
            if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(directory);
            }
        }

        Directory.Delete(root, recursive: true);
    }

    private static void CreateJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])["/c", "mklink", "/J", link, target])
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"ジャンクションを作れません: {error}");
        }
    }
}
