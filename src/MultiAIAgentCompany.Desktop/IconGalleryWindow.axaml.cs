using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Desktop;

/// <summary>一覧の1行。<b>絵と、その絵が何を意味するかを並べる</b>（設計 §15）。</summary>
public sealed record IconGalleryRow(string Title, string Detail, Bitmap? Image = null, string Glyph = "");

/// <summary>
/// アイコンを実寸で並べて見る窓（設計 §15-5）。
/// </summary>
/// <remarks>
/// <b>絵を足したら、ここで人間が見る。</b> §15-5 は「24px に縮小して並べて見る。
/// そこで区別が付かないなら、絵ではなく状態設計の問題」と決めている ——
/// その確かめを**アプリの中**に置いておく。外の確認シートは、ビルドと一緒に古くなる。
/// </remarks>
public partial class IconGalleryWindow : Window
{
    public IconGalleryWindow()
    {
        // **`InitializeComponent` より前に置く**（レビューで指摘）——
        // あとに置くと、初期化時に一度 null として評価される。
        DataContext = this;
        InitializeComponent();
    }

    public IReadOnlyList<IconGalleryRow> Poses { get; } =
    [
        new("働いている（Working）", "人間は何もしなくてよい", PoseImages.Of(DepartmentPose.Working)),
        new("手が空いている（Resting）", "仕事を割り当てられる", PoseImages.Of(DepartmentPose.Resting)),
        new("承認まち（AwaitingApproval）", "(a) 許可か拒否を決める → 部門のターミナルへ", PoseImages.Of(DepartmentPose.AwaitingApproval)),
        new("相談中（Consulting）", "(b) 質問を読んで答える → .company/ の question.md へ", PoseImages.Of(DepartmentPose.Consulting)),
        new("調子が悪い（Degraded）", "動いてはいるが何かおかしい。様子を見る", PoseImages.Of(DepartmentPose.Degraded)),
        new("分からない（Unknown）", "特に行動は要らない。長く続くなら見に行く", PoseImages.Of(DepartmentPose.Unknown)),

        // **状態のポーズではない**（設計 §52-4）。受理したときのねぎらいにだけ出る。
        new("おじぎ（ねぎらい）", "状態ではない。報告を受理したときに少しだけ出る", PoseImages.Bowing),
    ];

    public IReadOnlyList<IconGalleryRow> Marks { get; } =
    [
        new("要回答（バッジ）", "question.md を読んで answer.md を書く", Glyph: "❓"),
        new("要受理（バッジ）", "report.md を読んで、受理か差し戻しを決める", Glyph: "📝"),
        new("要確認（バッジ）", "再起動を跨いだ Dispatched。送られたか確かめる", Glyph: "📮"),
        new("報告が来ない（バッジ）", "期限までに報告を観測していない。失敗とは限らない", Glyph: "⏳"),
        new("倒れている（印）", "プロセスが Failed。原因を見て、再起動するか決める", Glyph: "⚠️"),
        new("終了した（印）", "プロセスが Exited。意図した終了なら何もしない", Glyph: "⏹"),
    ];

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
