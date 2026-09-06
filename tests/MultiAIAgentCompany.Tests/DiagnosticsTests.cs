using MultiAIAgentCompany.Core.Sessions;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §22 —— ターミナルの代わりの、読むだけの診断。</summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public void 新しい順に返す()
    {
        var log = new DiagnosticsLog();
        log.Add(new LiveDiagnostic(DiagnosticStream.StandardError, "1行目"));
        log.Add(new LiveDiagnostic(DiagnosticStream.StandardError, "2行目"));

        Assert.Equal(["2行目", "1行目"], log.Recent(10).Select(line => line.Text));
    }

    [Fact]
    public void 上限を超えたら古い方から捨てる()
    {
        var log = new DiagnosticsLog(capacity: 2);
        foreach (var index in Enumerable.Range(1, 4))
        {
            log.Add(new LiveDiagnostic(DiagnosticStream.StandardError, $"{index}行目"));
        }

        Assert.Equal(["4行目", "3行目"], log.Recent(10).Select(line => line.Text));
    }

    [Fact]
    public void 捨てたことを黙らない()
    {
        // **画面に出ている範囲が全部だと思わせない**（§22-2）。
        // この案件で繰り返している「静かに壊れて動いているように見える」形。
        var log = new DiagnosticsLog(capacity: 1);
        log.Add(new LiveDiagnostic(DiagnosticStream.StandardError, "古い"));
        log.Add(new LiveDiagnostic(DiagnosticStream.StandardError, "新しい"));

        Assert.Equal(1, log.Dropped);
        Assert.Contains("捨てた", log.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 空でも何か言う()
    {
        // 何も無いことを「正常」と読ませない（§7）。
        Assert.Contains("0 行", new DiagnosticsLog().Summary, StringComparison.Ordinal);
    }
}
