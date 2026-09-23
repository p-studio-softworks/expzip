// 画面を操作するテストに使う書庫を作り直す (#97)。
//
//     dotnet run --project tools/uifixtures -- <出したい場所>
//
// 出したい場所の下に ui_work と nest_work を作る。テストのスクリプトはそこを見る。
//
// **これらの書庫は一時領域に置いてあり、掃除で消える。**実際に消えて、
// #15・#20・#30・#49 の確認 (128項目) が「書庫が無くて始められない」状態に
// なった。中身はテストのスクリプトが名前で見ているので、作り方をここに残しておく。
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

// 7z は書けないので 7z.exe を借りる。場所は環境変数 EXPZIP_7Z で指定でき、
// 無ければ 7-Zip の標準の場所を見る。見つからなければ 7z を使う確認だけが飛ぶ
var sevenZipTool = Environment.GetEnvironmentVariable("EXPZIP_7Z") is { Length: > 0 } configured
    ? configured
    : @"C:\Program Files\7-Zip\7z.exe";

Console.OutputEncoding = Encoding.UTF8;

if (args.Length != 1)
{
    Console.WriteLine("使い方: dotnet run --project tools/uifixtures -- <出したい場所>");
    return 1;
}

var ui = Path.Combine(args[0], "ui_work");
var nest = Path.Combine(args[0], "nest_work");

Directory.CreateDirectory(ui);
Directory.CreateDirectory(nest);

// ---------------------------------------------------------------- #20 保護された項目の色
// 色が付かない側。付く側と見比べるために要る
Zip(Path.Combine(ui, "plain.zip"), null, 0,
    ("文書.txt", "yomeru"),
    ("資料/仕様.txt", "shiyou"));

// まるごと暗号化。一覧の名前がすべて緑になる
Zip(Path.Combine(ui, "all.zip"), "aikotoba", 0,
    ("秘密1.txt", "himitsu"),
    ("秘密2.txt", "himitsu"),
    ("資料/秘密3.txt", "himitsu"));

// 一部だけ暗号化。公開 の中は色が付かず、秘密 の中だけ付く
ZipMixed(Path.Combine(ui, "mixed.zip"), "aikotoba",
    [("公開/読める.txt", "yomeru"), ("公開/案内.txt", "annai")],
    [("秘密/秘密.txt", "himitsu"), ("秘密/控え.txt", "hikae")]);

// AES-256。合言葉を覚えるか、タブを閉じて忘れるかを見る。
// **合言葉はテストのスクリプトが打ち込むものと同じにする**
Zip(Path.Combine(ui, "aes256.zip"), "ひみつのあいことば", 256,
    ("秘密.txt", "himitsu desu"),
    ("資料/報告.txt", "houkoku desu"));

// ---------------------------------------------------------------- #15 名前の変更
Zip(Path.Combine(ui, "rename_src.zip"), null, 0,
    ("報告書.txt", "houkoku"),
    ("資料/仕様.txt", "shiyou"));

Zip(Path.Combine(ui, "clickrename.zip"), null, 0,
    ("報告書.txt", "houkoku"),
    ("資料/仕様.txt", "shiyou"));

// ---------------------------------------------------------------- #49 ツリーの開き
// 開いておく枝 (資料/2024) と、畳んだままにする枝 (別のフォルダ/中) の両方が要る
Zip(Path.Combine(ui, "tree.zip"), null, 0,
    ("資料/2024/1月/報告.txt", "ichigatsu"),
    ("資料/2024/2月/報告.txt", "nigatsu"),
    ("別のフォルダ/中/覚書.txt", "oboegaki"));

File.WriteAllText(Path.Combine(ui, "tree-added.txt"), "ato kara tashita", Encoding.UTF8);

// ---------------------------------------------------------------- #30 書庫の中の書庫
// **1つ消しても残るものを入れておく。**書き戻しの確認が、消えたことと
// 残ったことの両方を見るため
var inner = Path.Combine(nest, "内側.zip");

Zip(inner, null, 0,
    ("内側メモ.txt", "uchigawa no memo"),
    ("資料/表.csv", "a,b,c"),
    ("資料/覚書.txt", "oboegaki"));

var files = new List<(string Name, byte[] Data)>
{
    ("内側.zip", File.ReadAllBytes(inner)),
    ("親のメモ.txt", Encoding.UTF8.GetBytes("oya no memo")),
};

// 書き換えられない形式でも中は見せる、という確認のための 7z
if (SevenZip(sevenZipTool,Path.Combine(nest, "中身.7z"), "seven.txt", "nana no naka") is { } seven)
{
    files.Add(("中身.7z", File.ReadAllBytes(seven)));
}
else
{
    Console.WriteLine("7z.exe が無いので 中身.7z は作りません: " + sevenZipTool);
}

ZipRaw(Path.Combine(nest, "nest.zip"), files);

// 3段。deep.zip > 中.zip > 内側.zip > 内側メモ.txt
var middle = Path.Combine(nest, "中.zip");

ZipRaw(middle, [("内側.zip", File.ReadAllBytes(inner))]);
ZipRaw(Path.Combine(nest, "deep.zip"), [("中.zip", File.ReadAllBytes(middle))]);
File.Delete(middle);
File.Delete(inner);

Console.WriteLine("作りました: " + ui);
Console.WriteLine("作りました: " + nest);
return 0;

// ---------------------------------------------------------------- 作る口

// 書庫の中の書庫は縮めずに入れる。取り出す前と後で中身が変わらないほうが追いやすい
static void ZipRaw(string path, IEnumerable<(string Name, byte[] Data)> files)
{
    using var stream = new ZipOutputStream(File.Create(path));
    stream.SetLevel(0);

    foreach (var (name, data) in files)
    {
        stream.PutNextEntry(new ZipEntry(name) { DateTime = DateTime.Now });
        stream.Write(data);
        stream.CloseEntry();
    }
}

// 合言葉を渡すとすべて暗号化する。aes が 0 なら昔ながらの ZipCrypto
static void Zip(string path, string? password, int aes, params (string Name, string Text)[] files)
{
    using var stream = new ZipOutputStream(File.Create(path));
    stream.SetLevel(6);

    if (password is not null)
    {
        stream.Password = password;
    }

    foreach (var (name, text) in files)
    {
        var entry = new ZipEntry(name) { DateTime = DateTime.Now };

        if (password is not null && aes > 0)
        {
            entry.AESKeySize = aes;
        }

        stream.PutNextEntry(entry);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.CloseEntry();
    }
}

// 一部だけ暗号化する。合言葉は項目ごとに入れ替えられる
static void ZipMixed(
    string path, string password,
    (string Name, string Text)[] plain, (string Name, string Text)[] secret)
{
    using var stream = new ZipOutputStream(File.Create(path));
    stream.SetLevel(6);

    foreach (var (name, text) in plain)
    {
        stream.Password = null;
        stream.PutNextEntry(new ZipEntry(name) { DateTime = DateTime.Now });
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.CloseEntry();
    }

    foreach (var (name, text) in secret)
    {
        stream.Password = password;
        stream.PutNextEntry(new ZipEntry(name) { DateTime = DateTime.Now });
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.CloseEntry();
    }
}

// 7z を1つ作る。作れなければ null
static string? SevenZip(string tool, string path, string name, string text)
{
    if (!File.Exists(tool))
    {
        return null;
    }

    var stage = Path.Combine(Path.GetTempPath(), "expzip-fixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stage);

    try
    {
        File.WriteAllText(Path.Combine(stage, name), text, Encoding.UTF8);
        File.Delete(path);

        var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = tool,
            ArgumentList = { "a", "-t7z", path, Path.Combine(stage, name) },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        run?.WaitForExit();

        return run is { ExitCode: 0 } ? path : null;
    }
    finally
    {
        Directory.Delete(stage, true);
    }
}
