using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §38。<b>広げた分だけ、境界をここで固定する。</b>
/// </summary>
public sealed class SecretaryBashPolicyTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private ApprovalVerdict Decide(string? command) =>
        SecretaryBashPolicy.Decide(command, _workspace.Paths.WorkspaceRoot);

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff --stat")]
    [InlineData("git log --oneline -20")]
    [InlineData("ls -la src")]
    [InlineData("cat docs/design.md")]
    [InlineData("rg \"TODO\" src")]
    [InlineData("wc -l docs/design.md")]
    public void 読むだけの命令は通す(string command)
    {
        Assert.True(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("git commit -m x")]
    [InlineData("git checkout main")]
    [InlineData("git config --global user.name x")]
    [InlineData("git push")]
    public void gitの書く副命令は通さない(string command)
    {
        // **`git` は commit も checkout も config も同じ名前で入ってくる。**
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("rm -rf src")]
    [InlineData("dotnet build")]
    [InlineData("curl https://example.com")]
    [InlineData("sed -i s/a/b/ x")]
    [InlineData("python3 x.py")]
    public void 名前に無い命令は通さない(string command)
    {
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("ls; rm -rf /")]
    [InlineData("cat a.md > b.md")]
    [InlineData("git status && git push")]
    [InlineData("cat `whoami`")]
    [InlineData("ls $(pwd)")]
    [InlineData("cat a.md | sh")]
    [InlineData("ls\nrm x")]
    [InlineData("cat ..\\/secret.md")]   // bash は ../secret.md として読む
    [InlineData("cat src\\/../../x")]
    public void 繋ぐ_書き出す_展開する字が入っていたら通さない(string command)
    {
        // **中身を解釈しない。** 許すと、読むだけの命令の後ろに何でも書ける ——
        // 許した名前の意味が無くなる。
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("cat ../secret.md")]
    [InlineData("cat /etc/passwd")]
    [InlineData("ls ~/.ssh")]
    [InlineData("git -C /elsewhere status")]
    [InlineData("cat ..")]
    public void 作業フォルダの外は通さない(string command)
    {
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cat \"閉じていない")]
    public void 読み取れない命令は聞く(string? command)
    {
        // **分からないものは聞く**（§7）。
        Assert.False(Decide(command).AutoApprove);
    }

    [Theory]
    [InlineData("git -c core.pager=sh log")]          // 設定で外部を起こす
    [InlineData("git -c alias.x=!sh x")]              // 別名で外部を起こす
    [InlineData("git --exec-path=/tmp status")]       // git 自身の実行場所を差し替える
    [InlineData("git log --ext-diff")]                // 外部 diff を起こす
    [InlineData("/bin/cat /etc/passwd")]              // 絶対パスの命令名
    [InlineData("CAT docs/x.md")]                     // 名前の大文字小文字
    [InlineData("cat src/../../secret.md")]           // / を含みつつ .. で外へ
    public void 抜け道になりそうな書き方は通さない(string command)
    {
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("ls  -la   src")]
    [InlineData("ls	src")]
    public void 空白の書き方は通す(string command)
    {
        Assert.True(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("ls --color=always src")]     // 許していない旗
    [InlineData("rg --pre sh pattern")]       // rg の前処理コマンド
    [InlineData("grep --devices=read x")]     // 許していない旗
    [InlineData("tail --follow=name docs/x")] // 止まらない形
    public void 許していない旗は通さない(string command)
    {
        // **旗を読み飛ばすと、読むだけの命令の顔をしたまま別のプログラムが走る。**
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("git log -20 --oneline")]
    [InlineData("git log --pretty=format:%h")]
    [InlineData("git diff --stat --cached")]
    [InlineData("grep -n -i pattern src")]
    public void 許した旗は通す(string command)
    {
        Assert.True(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("git tag v1")]                  // 作る
    [InlineData("git branch -D feature")]       // 消す
    [InlineData("git remote remove origin")]    // 消す
    [InlineData("git branch feature")]          // 作る
    public void 引数で書き換わる副命令は通さない(string command)
    {
        // **副命令の名前だけ見ると、一覧と同じ顔で通る**（レビューで発覚）。
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("git branch")]
    [InlineData("git tag")]
    [InlineData("git remote -v")]
    public void 一覧にしかならない形は通す(string command)
    {
        Assert.True(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("git --git-dir=/tmp/repo log")]  // 外のリポジトリを読む
    [InlineData("tree -o out.txt")]              // 書き出す旗
    public void 外を読む旗や書き出す旗は通さない(string command)
    {
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Fact]
    public void Windowsでは別のドライブを指す語を通さない()
    {
        // `D:secret.txt` は `/` を持たないのに、別のドライブを読む（2026-09-14）。
        if (!OperatingSystem.IsWindows()) return;

        Assert.False(Decide("cat D:secret.txt").AutoApprove);
        Assert.False(Decide("cat C:/Windows/win.ini").AutoApprove);
    }

    [Fact]
    public void Windowsでは作業フォルダの大文字小文字の違いで聞かない()
    {
        // CLI は `c:\` と `C:\` を混ぜて渡してくる。NTFS の既定は区別しないので、同じ場所である。
        if (!OperatingSystem.IsWindows()) return;

        var inside = Path.Combine(_workspace.Paths.WorkspaceRoot, "docs", "design.md").ToUpperInvariant().Replace('\\', '/');
        Assert.True(Decide($"cat {inside}").AutoApprove);
    }

    [Fact]
    public void 通したときも理由を言う()
    {
        // **黙って強い権限で動くものを作らない**（§35-4）。
        var verdict = Decide("git status");
        Assert.True(verdict.AutoApprove);
        Assert.Contains("git", verdict.Reason);
    }

    [Theory]
    [InlineData("mv .company/secretary/outbox/calc-div.md.tmp .company/secretary/outbox/calc-div.md")]
    [InlineData("mv .company/secretary/outbox/p.md.tmp.1 .company/secretary/outbox/p.md")]
    [InlineData("cp .company/tasks/t/report.md .company/secretary/outbox/copy.md")]
    [InlineData("mkdir -p .company/secretary/outbox")]
    [InlineData("touch .company/secretary/outbox/x.md")]
    [InlineData("find .company -name \"*.md\" -maxdepth 3")]
    [InlineData("echo -n hello")]
    [InlineData("basename .company/tasks/t/report.md")]
    public void company_の中だけで書く命令と_単純な命令は通す(string command)
    {
        // 設計 §62-15（人間が広げた）。publish の rename が毎回承認で止まっていた。
        Assert.True(Decide(command).AutoApprove, command);
    }

    [Theory]
    [InlineData("mv calc.py .company/x.py")]
    [InlineData("mv .company/x.md calc.py")]
    [InlineData("cp .company/x.md ../outside.md")]
    [InlineData("mkdir -p src/new")]
    [InlineData("touch README.md")]
    [InlineData("mv .company/tasks/t/state.json .company/x.json")]
    [InlineData("cp .company/x.json .company/tasks/t/STATE.JSON")]
    [InlineData("touch .company/lease.json")]
    [InlineData("mv .company .company-old")]
    [InlineData("mv -f .company/a.md .company/b.md")]
    [InlineData("mv .company/a.md .company/b.md .company/c")]
    [InlineData("mv ~/.ssh/id .company/x")]
    [InlineData("find . -name x -delete")]
    [InlineData("find . -exec rm {} ;")]
    [InlineData("rm .company/x.md")]
    public void company_の外_守るファイル_いつもと違う形は聞く(string command)
    {
        Assert.False(Decide(command).AutoApprove, command);
    }

    [Fact]
    public void フォルダごとの_mv_は聞く()
    {
        Directory.CreateDirectory(Path.Combine(_workspace.Paths.TasksRoot, "t"));
        Assert.False(Decide("mv .company/tasks/t .company/tasks/u").AutoApprove);
    }

}
