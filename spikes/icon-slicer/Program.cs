using SkiaSharp;

// キャラクターシートから部門アイコンを作る（設計 §15-5）。
if (args.Length < 2)
{
    Console.Error.WriteLine("使い方: slice <sheet.png> <出力先> / normalize <入力> <出力> / sheet <入力> <出力.png>");
    return 1;
}

string[] Names = ["working", "resting", "awaiting-approval", "consulting", "degraded", "unknown"];
var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

switch (args[0])
{
    case "slice": Slice(args[1], args[2]); break;
    case "normalize": Normalize(args[1], args[2]); break;
    case "sheet": Sheet(args[1], args[2]); break;
    default:
        Console.Error.WriteLine($"知らない命令: {args[0]}");
        return 1;
}

return 0;

// **格子で切らない。** 絵がセルからはみ出す（ノートPC・煙・広げた腕）ので、
// アルファの塊（連結成分）で切る。
void Slice(string source, string outDir)
{
    Directory.CreateDirectory(outDir);
    using var bmp = SKBitmap.Decode(source);
    int w = bmp.Width, h = bmp.Height;
    const byte Threshold = 16;

    var label = new int[w * h];
    var boxes = new List<(int Left, int Top, int Right, int Bottom, int Area)>();
    var queue = new Queue<int>();

    for (var start = 0; start < label.Length; start++)
    {
        if (label[start] != 0 || bmp.GetPixel(start % w, start / w).Alpha < Threshold) continue;

        var id = boxes.Count + 1;
        int left = w, top = h, right = 0, bottom = 0, area = 0;
        label[start] = id;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            int px = p % w, py = p / w;
            area++;
            left = Math.Min(left, px); right = Math.Max(right, px);
            top = Math.Min(top, py); bottom = Math.Max(bottom, py);

            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var n = ny * w + nx;
                if (label[n] != 0 || bmp.GetPixel(nx, ny).Alpha < Threshold) continue;
                label[n] = id;
                queue.Enqueue(n);
            }
        }

        boxes.Add((left, top, right, bottom, area));
    }

    var big = boxes.Where(b => b.Area > w * h / 400).ToList();
    Console.WriteLine($"塊 {boxes.Count} 個 / 大きいもの {big.Count} 個");

    // 汗の線や煙の切れ端は、いちばん近い大きな塊に足す。
    foreach (var small in boxes.Where(b => b.Area <= w * h / 400 && b.Area > 200))
    {
        double cx = (small.Left + small.Right) / 2.0, cy = (small.Top + small.Bottom) / 2.0;
        var nearest = big.OrderBy(b =>
            Math.Pow(cx - (b.Left + b.Right) / 2.0, 2) + Math.Pow(cy - (b.Top + b.Bottom) / 2.0, 2)).First();
        var i = big.IndexOf(nearest);
        big[i] = (Math.Min(nearest.Left, small.Left), Math.Min(nearest.Top, small.Top),
            Math.Max(nearest.Right, small.Right), Math.Max(nearest.Bottom, small.Bottom), nearest.Area + small.Area);
    }

    var ordered = big.OrderBy(b => (b.Top + b.Bottom) / 2 < h / 2 ? 0 : 1).ThenBy(b => b.Left).ToList();
    for (var i = 0; i < ordered.Count; i++)
    {
        var b = ordered[i];
        int bw = b.Right - b.Left + 1, bh = b.Bottom - b.Top + 1;
        var side = Math.Max(bw, bh);
        using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using (var piece = new SKBitmap(bw, bh))
        {
            bmp.ExtractSubset(piece, new SKRectI(b.Left, b.Top, b.Right + 1, b.Bottom + 1));
            surface.Canvas.DrawBitmap(piece, (side - bw) / 2f, (side - bh) / 2f);
        }

        Save(surface, Path.Combine(outDir, (i < Names.Length ? Names[i] : $"extra-{i}") + ".png"));
        Console.WriteLine($"{(i < Names.Length ? Names[i] : $"extra-{i}")}: {bw}x{bh}");
    }
}

// **倍率は手で決める。** 頭の幅の自動検出は、ノートPCや煙を頭と数えて外した ——
// 6枚しかないので、目で見て決める方が早くて確かである。
void Normalize(string inDir, string outDir)
{
    Directory.CreateDirectory(outDir);
    const int Canvas = 256;

    (string Name, double Fill, double BottomMargin)[] poses =
    [
        ("working", 0.94, 0.02),
        ("resting", 0.98, 0.06),
        ("awaiting-approval", 0.90, 0.02),
        ("consulting", 0.90, 0.02),
        ("degraded", 1.00, 0.04),
        ("unknown", 1.00, 0.02),
    ];

    foreach (var (name, fill, bottom) in poses)
    {
        using var src = SKBitmap.Decode(Path.Combine(inDir, name + ".png"));
        var factor = Canvas * fill / Math.Max(src.Width, src.Height);
        int w = (int)Math.Round(src.Width * factor), h = (int)Math.Round(src.Height * factor);

        using var surface = SKSurface.Create(new SKImageInfo(Canvas, Canvas, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using (var scaled = src.Resize(new SKImageInfo(w, h), sampling))
        {
            // 横は中央、縦は下揃え —— 立ち姿と座り姿の足元を合わせる。
            surface.Canvas.DrawBitmap(scaled, (Canvas - w) / 2f, Canvas - h - (float)(Canvas * bottom));
        }

        Save(surface, Path.Combine(outDir, name + ".png"));
        Console.WriteLine($"{name}: {src.Width} → {w}x{h}");
    }
}

// 実寸で並べて見る（設計 §15-5 の判定）。
void Sheet(string dir, string outPath)
{
    var bitmaps = Names.Select(n => SKBitmap.Decode(Path.Combine(dir, n + ".png"))).ToArray();
    const int Cell = 96;
    var width = Cell * 6 + 40;

    using var surface = SKSurface.Create(new SKImageInfo(width, 330));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.White);
    using var dark = new SKPaint { Color = new SKColor(0x1C, 0x1C, 0x1E) };
    using var light = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true };
    using var onDark = new SKPaint { Color = new SKColor(0xF2, 0xF2, 0xF7), IsAntialias = true };
    using var font = new SKFont(SKTypeface.Default, 11);

    void Row(float y, int size, bool isDark)
    {
        for (var i = 0; i < bitmaps.Length; i++)
        {
            using var scaled = bitmaps[i].Resize(new SKImageInfo(size, size), sampling);
            canvas.DrawBitmap(scaled, 20 + i * Cell + (Cell - size) / 2f, y);
            canvas.DrawText((i + 1).ToString(), 20 + i * Cell + Cell / 2f, y + size + 14,
                SKTextAlign.Center, font, isDark ? onDark : light);
        }
    }

    canvas.DrawText("64px", 6, 20, SKTextAlign.Left, font, light);
    Row(28, 64, false);
    canvas.DrawText("40px", 6, 130, SKTextAlign.Left, font, light);
    Row(138, 40, false);
    canvas.DrawRect(new SKRect(0, 200, width, 320), dark);
    canvas.DrawText("40px / 24px（ダーク）", 6, 218, SKTextAlign.Left, font, onDark);
    Row(226, 40, true);
    Row(282, 24, true);

    Save(surface, outPath);
    foreach (var b in bitmaps) b.Dispose();
    Console.WriteLine($"書いた: {outPath}");
}

void Save(SKSurface surface, string path)
{
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(path);
    data.SaveTo(file);
}
