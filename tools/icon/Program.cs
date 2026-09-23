// アプリのアイコンを描き出す (#126)。
//
//     dotnet run --project tools/icon                        Expzip.ico を作り直す
//     dotnet run --project tools/icon -- --png 512 <出力先>    1 枚の PNG にする (GitHub のアイコンなど)
//     dotnet run --project tools/icon -- --card <出力先>       リポジトリの Social preview の画像を作る (#139)
//
// 絵の正本は src/Expzip/Ui/AppIcon.xaml。バージョン情報はそれをそのまま出し、
// exe に付ける ico はここでそれを描き出して作る。**絵を直したらこれも走らせる。**
// 外部の画像ツールは使わない。WPF で描けば、画面に出るものと同じ絵になる。
//
// **出来上がりの src/Expzip/Resources/Expzip.ico はリポジトリに入れてある。**
// Expzip 本体のビルドにこのツールは要らない。
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Expzip.Tools.Icon;

internal static class Program
{
    /// <summary>
    /// ico に入れる大きさ。Windows が使う大きさを揃える。
    /// 16〜48 は一覧やタスクバー、表示倍率を上げたときは 20・24・40 も使われる。256 は大アイコン表示
    /// </summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        var repo = FindRepository();
        if (repo is null)
        {
            Console.Error.WriteLine("リポジトリが見つかりません (Expzip.slnx のあるフォルダーから走らせてください)");
            return 2;
        }

        var image = LoadImage(Path.Combine(repo, "src", "Expzip", "Ui", "AppIcon.xaml"));

        if (args.Length == 3 && args[0] == "--png" && int.TryParse(args[1], out var size) && size > 0)
        {
            var path = Path.GetFullPath(args[2]);
            File.WriteAllBytes(path, Render(image, size));
            Console.WriteLine($"書きました: {path} ({size} × {size})");
            return 0;
        }

        if (args.Length == 2 && args[0] == "--card")
        {
            var path = Path.GetFullPath(args[1]);
            File.WriteAllBytes(path, RenderCard(image));
            Console.WriteLine($"書きました: {path} ({CardWidth} × {CardHeight})");
            return 0;
        }

        if (args.Length > 0)
        {
            Console.Error.WriteLine("使い方: dotnet run --project tools/icon [-- --png <大きさ> <出力先> | -- --card <出力先>]");
            return 2;
        }

        var output = Path.Combine(repo, "src", "Expzip", "Resources", "Expzip.ico");
        File.WriteAllBytes(output, BuildIcon(Sizes.Select(s => (s, Render(image, s))).ToList()));
        Console.WriteLine($"書きました: {output}");
        Console.WriteLine($"大きさ: {string.Join(" / ", Sizes)}");
        return 0;
    }

    private static DrawingImage LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var dictionary = (ResourceDictionary)XamlReader.Load(stream);
        return (DrawingImage)dictionary["AppIconImage"];
    }

    /// <summary>1 つの大きさを PNG に描く。</summary>
    private static byte[] Render(DrawingImage image, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(image, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    // Social preview の画像 (#139)。大きさは GitHub の勧める 1280 × 640
    private const int CardWidth = 1280;
    private const int CardHeight = 640;

    /// <summary>
    /// 背景は明るくする。アイコンの奥の紺を背景にすると、フォルダーの輪郭が沈む。
    /// 名前の字はアイコンの奥の紺にそろえる (背景との明るさの差 10 : 1 ほど)
    /// </summary>
    private static readonly Brush CardBackground = Frozen(Color.FromRgb(0xF4, 0xF8, 0xFC));
    private static readonly Brush CardText = Frozen(Color.FromRgb(0x16, 0x3F, 0x6B));

    /// <summary>
    /// アイコンと名前を横に並べた画像を描く。SNS では縮めて出るので、字は大きくし、飾りは足さない。
    /// 絵は 64 × 64 の升目の中で中心より下に寄っている (フォルダーは x 6〜58、y 12〜56) ので、
    /// 升目ではなく、フォルダーの見た目の中心で名前とそろえる
    /// </summary>
    private static byte[] RenderCard(DrawingImage image)
    {
        const double iconSize = 320;
        const double unit = iconSize / 64;
        const double gap = 56;
        const double fontSize = 168;

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText("Expzip", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontSize, CardText, 1.0);
        var ink = text.BuildGeometry(new Point(0, 0)).Bounds;

        // 横: フォルダーの左端から字の右端までを、画像の中央に置く
        var folderWidth = (58 - 6) * unit;
        var left = (CardWidth - (folderWidth + gap + ink.Width)) / 2;
        var iconX = left - 6 * unit;
        var textX = left + folderWidth + gap - ink.Left;

        // 縦: フォルダーの中心と大文字の高さの中心を、画像の中央にそろえる
        var iconY = CardHeight / 2.0 - (12 + 56) / 2.0 * unit;
        var textY = CardHeight / 2.0 + typeface.CapsHeight * fontSize / 2 - text.Baseline;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CardBackground, null, new Rect(0, 0, CardWidth, CardHeight));
            context.DrawImage(image, new Rect(iconX, iconY, iconSize, iconSize));
            context.DrawText(text, new Point(textX, textY));
        }

        var bitmap = new RenderTargetBitmap(CardWidth, CardHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// PNG を並べて ico にする。中身を PNG のまま入れる形は Windows Vista から読める。
    /// </summary>
    private static byte[] BuildIcon(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        // 見出し: 予約 0 / 種類 1 (アイコン) / 枚数
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            // 256 は 1 バイトに入らないので 0 と書く決まり
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);      // 色数 (パレットなし)
            writer.Write((byte)0);      // 予約
            writer.Write((ushort)1);    // 面の数
            writer.Write((ushort)32);   // 1 画素のビット数
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            writer.Write(png);
        }

        writer.Flush();
        return memory.ToArray();
    }

    private static string? FindRepository()
    {
        var here = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (here is not null)
        {
            if (File.Exists(Path.Combine(here.FullName, "Expzip.slnx")))
            {
                return here.FullName;
            }

            here = here.Parent;
        }

        return null;
    }
}
