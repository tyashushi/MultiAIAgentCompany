using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using MultiAIAgentCompany.Core.Coordination;
using SkiaSharp;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 画像と添付と報告のプレビュー（設計 §56-5 / §58-5 / §62-9）。表示を差し替えるたびに、前の画像は解放する。
/// </summary>
public partial class ImagePreviewWindow : Window
{
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Bitmap? _bitmap;
    private string? _fullPath;
    private bool _isImage;

    /// <summary>文字として出すのは先頭のここまで（§58-5）。</summary>
    private const int TextLimitBytes = 256 * 1024;

    /// <summary>この範囲に NUL があれば文字として扱わない（§58-5）。</summary>
    private const int BinarySniffBytes = 8 * 1024;

    public ImagePreviewWindow()
    {
        InitializeComponent();
        RevealButton.Content = OperatingSystem.IsMacOS() ? "Finder で開く"
            : OperatingSystem.IsWindows() ? "エクスプローラーで開く" : "フォルダで開く";
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); LoadImage(); };
        ImageArea.SizeChanged += (_, _) =>
        {
            if (_fullPath is null || !_isImage) return;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
        Closed += (_, _) => { _resizeTimer.Stop(); ClearImage(); };

        // **コードの色もテーマに合わせる**（§62-9）。ライブラリの既定はダーク固定で、
        // ライトの地に明るい字が乗って読めない。
        ApplyCodeTheme();
        ActualThemeVariantChanged += (_, _) => ApplyCodeTheme();
    }

    public void SetImage(string link, ImageLinkResolution resolution)
    {
        _resizeTimer.Stop();
        ClearImage();
        _fullPath = (resolution as ImageLinkResolution.Resolved)?.FullPath;
        PreviewPath.Text = _fullPath ?? link;
        RevealButton.IsEnabled = _fullPath is not null;
        PreviewMessage.Text = (resolution as ImageLinkResolution.Rejected)?.Reason;
        PreviewMessage.IsVisible = PreviewMessage.Text is not null;
        PreviewText.Text = null;
        TextArea.IsVisible = false;
        ClearMarkdown();
        _isImage = TranscriptImageLinks.IsImage(link);
        if (_isImage) LoadImage();
        else LoadText();
    }

    /// <summary>
    /// 添付を文字として出す。<b>読めなければ「プレビューできない」と言い、場所を開く方へ渡す</b>（§58-5）。
    /// </summary>
    private void LoadText()
    {
        if (_fullPath is null) return;
        try
        {
            byte[] bytes;
            long length;
            using (var stream = File.OpenRead(_fullPath))
            {
                length = stream.Length;
                bytes = new byte[(int)Math.Min(length, TextLimitBytes)];
                stream.ReadExactly(bytes);
            }

            var count = bytes.Length;
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(count, BinarySniffBytes)) >= 0)
            {
                ShowFailure("この種類はプレビューできない");
                return;
            }
            // 切った位置が文字の途中なら、文字の頭まで戻す（UTF-8 の継続バイトは 10xxxxxx）。
            if (length > count)
            {
                var back = count;
                while (back > 0 && count - back < 4 && (bytes[back - 1] & 0xC0) == 0x80) back--;
                if (back > 0 && bytes[back - 1] >= 0xC0) count = back - 1;
            }

            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, count); }
            catch (DecoderFallbackException)
            {
                ShowFailure("この種類はプレビューできない");
                return;
            }

            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            if (length > count) text += $"\n\n……（先頭 {TextLimitBytes / 1024}KB だけ表示している。全体は {Attachments.Megabytes(length)}）";
            // **.md は整形して出す**（設計 §62-9）。報告は見出しと箇条書きで読む文書なので、
            // 記号のまま出すと読む側が頭の中で組み直すことになる。
            if (TranscriptImageLinks.IsMarkdown(_fullPath))
            {
                var builder = new ObservableStringBuilder();
                PreviewMarkdown.ImageBasePath = Path.GetDirectoryName(_fullPath);
                PreviewMarkdown.MarkdownBuilder = builder;
                builder.Append(text);
                MarkdownArea.IsVisible = true;
            }
            else
            {
                PreviewText.Text = text;
                TextArea.IsVisible = true;
            }

            PreviewMessage.IsVisible = false;
        }
        catch (Exception exception)
        {
            // 読み込み失敗でアプリを落とさない（§49）。
            ShowFailure(exception is FileNotFoundException or DirectoryNotFoundException
                ? "ファイルが見つからない" : $"読めない（{exception.GetType().Name}）");
        }
    }

    private void LoadImage()
    {
        if (_fullPath is null || ImageArea.Bounds.Width <= 0 || ImageArea.Bounds.Height <= 0) return;
        ClearImage();
        try
        {
            using var stream = File.OpenRead(_fullPath);
            int width;
            int height;
            // **寸法だけを先に読む。** Avalonia が既に使う Skia で、原寸のピクセルは展開しない（§56）。
            using (var codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false)))
            {
                if (codec is null) throw new InvalidDataException("画像の寸法を読めない");
                width = codec.Info.Width;
                height = codec.Info.Height;
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("画像の寸法が不正");
            stream.Position = 0;
            // **縦長でも横長でも窓に収める。** 小さい画像を decode 時点でも拡大しない（§56）。
            var widthScale = Math.Min(1, ImageArea.Bounds.Width * RenderScaling / width);
            var heightScale = Math.Min(1, ImageArea.Bounds.Height * RenderScaling / height);
            _bitmap = widthScale <= heightScale
                ? Bitmap.DecodeToWidth(stream, Math.Max(1, (int)Math.Floor(width * widthScale)))
                : Bitmap.DecodeToHeight(stream, Math.Max(1, (int)Math.Floor(height * heightScale)));
            PreviewImage.MaxWidth = Math.Min(width, _bitmap.PixelSize.Width / RenderScaling);
            PreviewImage.MaxHeight = Math.Min(height, _bitmap.PixelSize.Height / RenderScaling);
            PreviewImage.Source = _bitmap;
            PreviewMessage.IsVisible = false;
        }
        catch (Exception exception)
        {
            // **壊れた絵ではなく理由を出す。** 読み込み失敗でアプリを落とさない（§49 / §56）。
            ShowFailure(exception is FileNotFoundException or DirectoryNotFoundException
                ? "ファイルが見つからない" : $"画像として読めない（{exception.GetType().Name}）");
        }
    }

    private void ClearImage()
    {
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void ApplyCodeTheme() =>
        PreviewMarkdown.CodeBlockColorTheme = ActualThemeVariant == ThemeVariant.Dark
            ? TextMateSharp.Grammars.ThemeName.DarkPlus
            : TextMateSharp.Grammars.ThemeName.LightPlus;

    private void ClearMarkdown()
    {
        PreviewMarkdown.MarkdownBuilder = new ObservableStringBuilder();
        MarkdownArea.IsVisible = false;
    }

    private void ShowFailure(string message)
    {
        ClearImage();
        ClearMarkdown();
        PreviewText.Text = null;
        TextArea.IsVisible = false;
        PreviewMessage.Text = message;
        PreviewMessage.IsVisible = true;
    }

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        if (_fullPath is null) return;
        try
        {
            if (!File.Exists(_fullPath)) { ShowFailure("ファイルが見つからない"); return; }
            ProcessStartInfo start;
            if (OperatingSystem.IsMacOS())
            {
                start = new ProcessStartInfo("open") { UseShellExecute = false };
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(_fullPath);
            }
            else if (OperatingSystem.IsWindows())
            {
                // explorer は `/select,"<path>"` を1つの引数として読む。ArgumentList で分けると
                // 引用の付き方が変わるので、ここだけ文字列で組む（パスに `"` は入らない）。
                start = new ProcessStartInfo("explorer.exe", $"/select,\"{_fullPath}\"") { UseShellExecute = false };
            }
            else
            {
                start = new ProcessStartInfo(Path.GetDirectoryName(_fullPath)!) { UseShellExecute = true };
            }
            using var process = Process.Start(start);
        }
        catch (Exception exception)
        {
            ShowFailure($"ファイルの場所を開けない（{exception.GetType().Name}）");
        }
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard) { ShowFailure("パスをコピーできない"); return; }
            await clipboard.SetTextAsync(PreviewPath.Text ?? "");
        }
        catch (Exception exception)
        {
            ShowFailure($"パスをコピーできない（{exception.GetType().Name}）");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
