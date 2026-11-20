using System.IO;
using System.Text;
using System.Text.Json;
using Expzip.Archives;

namespace Expzip.Configuration;

/// <summary>設定ファイルの読み書き。</summary>
/// <remarks>
/// <para>
/// 保存先は <see cref="DataFolder"/> が決める。exe 単体の版は exe と同じフォルダ、
/// Store 版は <c>%LOCALAPPDATA%\Expzip</c> (docs/SPEC.md 7章)。
/// </para>
/// <para>
/// exe を書き込めない場所 (Program Files や読み取り専用のUSBメモリなど) に
/// 置かれることがある。その場合でも設定を保存できないだけで、アプリ自体は
/// 通常どおり動かす。読み書きの失敗でアプリを止めない。
/// </para>
/// </remarks>
internal static class SettingsStore
{
    private const string FileName = "Expzip.settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        // 利用者が直接開いて編集することも想定して整形する
        WriteIndented = true,
    };

    /// <summary>
    /// 読む大きさの上限 (#399)。ふつうの設定ファイルは数 KB。これより大きければ読まずに「読めない」とする。
    /// ほかのプログラムなどが巨大なファイルを置いても、メモリーを使い切って起動できなくならないように。
    /// </summary>
    internal const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>最近開いた書庫を覚える数 (#10)。手で書き足されていても、読んだときにここまで切る。</summary>
    internal const int RecentLimit = 10;

    /// <summary>同じ中身の写しがあるかを比べる数の上限 (#399)。</summary>
    private const int CompareLimit = 50;

    /// <summary>写しの名前がぶつかったときに試す数 (#399)。</summary>
    private const int KeepAttempts = 10;

    /// <summary>ほかのプログラムが開いていて読めなかったときに読む回数と、その間に待つ時間 (#399)。起動を待たせるのは長くて 0.2 秒。</summary>
    private const int ReadAttempts = 5;

    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>設定ファイルのパス。</summary>
    public static string FilePath { get; } = Path.Combine(DataFolder.Path, FileName);

    /// <summary>画面から起動したときに設定を読み込む。</summary>
    /// <remarks>
    /// <para>
    /// 壊れた設定ファイルで起動できなくなるのが最も困るので、読めなくても既定の値で動かす。
    /// ただし設定ファイルがあったのに読めなかったときは、<see cref="AppSettings.LoadProblem"/> を付けて、
    /// そのセッションでは保存しない (#399)。保存すると、元の設定が既定の値で上書きされて失われる。
    /// </para>
    /// <para>
    /// 中身を読めたのに設定として読めなかったとき (壊れている) は、読んだ中身を別の名前で残す。
    /// ほかのプログラムが開いていて読めなかっただけなら、次の起動で読めるので、残さない。
    /// 元のファイルは読むだけで、名前の変更も削除もしない。
    /// </para>
    /// </remarks>
    public static AppSettings Load() => Load(FilePath, keepCopy: true);

    /// <summary>コマンドラインから実行したときに設定を読み込む。読めなかった中身の写しは作らない。</summary>
    /// <remarks>コマンドラインからの実行は設定を保存しないので、写しは要らない。スクリプトから繰り返し呼ばれても写しを増やさない。</remarks>
    public static AppSettings LoadForCommand() => Load(FilePath, keepCopy: false);

    /// <summary>設定を読み込む。テストは置き場所を渡して呼ぶ。</summary>
    internal static AppSettings Load(string path, bool keepCopy)
    {
        byte[]? bytes;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // 先に File.Exists で確かめない。あっても一時的なエラーでは「無い」と返り、保存してよい扱いになるため。
                // 無いと分かるのは、開いて FileNotFound / DirectoryNotFound になったときだけ
                bytes = ReadLimited(path);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new AppSettings();
            }
            catch (IOException ex) when (AtomicFile.IsSharing(ex) && attempt < ReadAttempts)
            {
                // ウイルス対策やバックアップが一瞬だけ開いていることがある。少し待って読み直す
                Thread.Sleep(ReadRetryDelay);
            }
            catch (IOException ex) when (AtomicFile.IsSharing(ex))
            {
                return Unreadable(path, keptPath: null, SettingsLoadFailure.InUse);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
            {
                return Unreadable(path, keptPath: null, SettingsLoadFailure.Inaccessible);
            }
        }

        // 大きすぎるものは中身を読まないので、残さない。保存もしないので、元のファイルはそのまま残る。
        // ふつうの設定ファイルは数 KB なので、壊れているものとして知らせる
        if (bytes is null)
        {
            return Unreadable(path, keptPath: null, SettingsLoadFailure.Damaged);
        }

        // 0 バイトのファイルには守る中身が無い。無いときと同じに扱い、保存してよい
        if (bytes.Length == 0)
        {
            return new AppSettings();
        }

        var settings = Parse(bytes);
        if (settings is null)
        {
            return Unreadable(path, keepCopy ? TryKeepCopy(path, bytes) : null, SettingsLoadFailure.Damaged);
        }

        // 古い設定ファイルや手で編集されたものには項目が無いことがある
        settings.RecentArchives ??= [];
        settings.RecentArchives.RemoveAll(p => p is null);
        if (settings.RecentArchives.Count > RecentLimit)
        {
            settings.RecentArchives.RemoveRange(RecentLimit, settings.RecentArchives.Count - RecentLimit);
        }

        return settings;
    }

    /// <summary>設定を保存する。</summary>
    /// <returns>
    /// 保存できたら true。書き込めない場所に置かれている場合と、設定ファイルを読めなかったセッション (#399) では false。
    /// </returns>
    public static bool TrySave(AppSettings settings) => TrySave(settings, FilePath);

    /// <summary>設定を保存する。テストは置き場所を渡して呼ぶ。</summary>
    internal static bool TrySave(AppSettings settings, string path)
    {
        // 読めなかった設定ファイルを、既定の値で上書きしない (#399)
        if (settings.LoadProblem is not null)
        {
            return false;
        }

        try
        {
            // 途中で止まっても、前の設定が残る (#320)
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return false;
        }
    }

    private static AppSettings Unreadable(string path, string? keptPath, SettingsLoadFailure failure)
        => new() { LoadProblem = new SettingsLoadProblem(path, keptPath, failure) };

    /// <summary>
    /// 上限までを読む。上限を超えていれば null。読んでいる間に大きくなっても、上限 + 1 バイトより先は読まない。
    /// </summary>
    /// <remarks>
    /// 共有の指定に Delete も付けるが、開いている間はほかの Expzip の置き換え (名前の変更) は通らない。
    /// 読んでいる一瞬にほかの Expzip の保存が重なると、そちらの保存が失敗する (やり直しの間に読み終われば通る)。
    /// 前の作り (<c>File.ReadAllText</c>) と同じ。
    /// </remarks>
    private static byte[]? ReadLimited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxBytes + 1L - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>設定として読む。読めなければ null。</summary>
    /// <remarks>
    /// 文字にするのは前の作り (<c>File.ReadAllText</c>) と同じ読み方にする。BOM を見て UTF-8 の BOM を除き、
    /// UTF-16 も読む。UTF-8 として正しくないバイトは置き換え文字にして読み進める。
    /// 今まで読めていたファイル (メモ帳で BOM 付きで保存したものなど) を、壊れている扱いにしない。
    /// </remarks>
    private static AppSettings? Parse(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonSerializer.Deserialize<AppSettings>(reader.ReadToEnd(), Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                   or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 読めなかった中身を、同じフォルダーの別の名前で残す (#399)。残した場所を返す。残せなければ null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 名前は <c>Expzip.settings.unreadable-日時.json</c>。ぶつかれば末尾に <c>-2</c>、<c>-3</c> … を付ける。
    /// 上書きしない指定で書くので、前に残した写しも、書いている間にできたファイルも置き換えない。
    /// 同じ中身の写しが既にあれば、作らずにその場所を返す。壊れたまま起動するたびに増えないように。
    /// </para>
    /// <para>
    /// 写すのは読んだバイト列そのもの。ファイルを読み直さないので、読めなかった中身と写しが食い違わない。
    /// 元のファイルの個別のアクセス権も写す。フォルダーから受け継いだ広いアクセス権で、
    /// 最近開いた書庫のパスなどがほかの人に見えないように。写せなければ残さない。
    /// </para>
    /// <para>
    /// 残せなくても起動は止めない。ここでの失敗は、どれも「残せなかった」として扱う。
    /// </para>
    /// </remarks>
    private static string? TryKeepCopy(string path, byte[] bytes)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var stem = Path.GetFileNameWithoutExtension(path) + ".unreadable-";
            var extension = Path.GetExtension(path);

            if (FindSameCopy(folder, stem + "*" + extension, bytes) is { } same)
            {
                return same;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            for (var attempt = 1; attempt <= KeepAttempts; attempt++)
            {
                var kept = Path.Combine(folder, stem + stamp + (attempt == 1 ? "" : "-" + attempt) + extension);

                // 前に残したものに触れないよう、名前に何かあれば開かずに次の名前にする
                if (File.Exists(kept) || Directory.Exists(kept))
                {
                    continue;
                }

                using var file = AtomicFile.Create(kept, overwrite: false);
                file.CopyAccessRulesFrom(path);
                file.Stream.Write(bytes);

                // 書いている間に同じ名前ができていたら false が返り、置き換えずに一時ファイルを消す
                if (file.Commit())
                {
                    return kept;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    /// <summary>同じ中身の写しを探す。比べるのは同じ大きさのものだけで、数にも上限を付ける。リンクは開かない。</summary>
    private static string? FindSameCopy(string folder, string pattern, byte[] bytes)
    {
        var compared = 0;
        foreach (var candidate in Directory.EnumerateFiles(folder, pattern))
        {
            var info = new FileInfo(candidate);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != bytes.Length)
            {
                continue;
            }

            if (++compared > CompareLimit)
            {
                break;
            }

            try
            {
                if (ReadLimited(candidate) is { } other && other.AsSpan().SequenceEqual(bytes))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めない写しは比べずに飛ばす
            }
        }

        return null;
    }
}
