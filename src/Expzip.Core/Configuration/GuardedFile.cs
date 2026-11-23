using System.IO;
using System.Text;
using Expzip.Archives;

namespace Expzip.Configuration;

/// <summary>読めた・読めなかったの結果。</summary>
internal enum GuardedReadStatus
{
    /// <summary>ファイルが無いか、0 バイト。守る中身が無いので、書いてよい。</summary>
    Missing,

    /// <summary>中身を読めた。</summary>
    Read,

    /// <summary>上限を超えていたので、中身を読まなかった。</summary>
    TooLarge,

    /// <summary>ほかのプログラムが開いたままにしていて、読み直しても読めなかった。</summary>
    InUse,

    /// <summary>アクセス権が無い、同じ名前のフォルダーがある、などで開けなかった。</summary>
    Inaccessible,
}

/// <summary>読んだ結果。<see cref="Bytes"/> は <see cref="GuardedReadStatus.Read"/> のときだけ入る。</summary>
internal readonly record struct GuardedRead(GuardedReadStatus Status, byte[]? Bytes);

/// <summary>
/// アプリを使う人のデータのファイル (設定、ルール) を、失わないように読む (#399、#406)。
/// </summary>
/// <remarks>
/// <para>
/// 読めなかったファイルを、読めなかったまま既定の中身で上書きしないために使う。読む側は、
/// 結果が <see cref="GuardedReadStatus.Missing"/> のときだけ書いてよく、それ以外で読めなかったときは書かない。
/// </para>
/// <para>
/// 設定は裏のスレッドで、ルールは UI のスレッドで読むので、状態を持たない。
/// </para>
/// </remarks>
internal static class GuardedFile
{
    /// <summary>
    /// 読む大きさの上限。ふつうの設定やルールのファイルは数 KB。これより大きければ読まずに「読めない」とする。
    /// ほかのプログラムなどが巨大なファイルを置いても、メモリーを使い切って Expzip が動かなくならないように。
    /// </summary>
    internal const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>同じ中身の写しがあるかを比べる数の上限。</summary>
    private const int CompareLimit = 50;

    /// <summary>写しの名前がぶつかったときに試す数。</summary>
    private const int KeepAttempts = 10;

    /// <summary>ほかのプログラムが開いていて読めなかったときに読む回数と、その間に待つ時間。待たせるのは長くて 0.2 秒。</summary>
    private const int ReadAttempts = 5;

    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>ファイルを読む。</summary>
    /// <remarks>
    /// 先に <c>File.Exists</c> で確かめない。あっても一時的なエラーでは「無い」と返り、書いてよい扱いになるため。
    /// 無いと分かるのは、開いて FileNotFound / DirectoryNotFound になったときだけ。
    /// 0 バイトのファイルには守る中身が無いので、無いときと同じに扱う。
    /// </remarks>
    public static GuardedRead Read(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var bytes = ReadLimited(path);
                return bytes is null ? new(GuardedReadStatus.TooLarge, null)
                    : bytes.Length == 0 ? new(GuardedReadStatus.Missing, null)
                    : new(GuardedReadStatus.Read, bytes);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(GuardedReadStatus.Missing, null);
            }
            catch (IOException ex) when (AtomicFile.IsSharing(ex) && attempt < ReadAttempts)
            {
                // ウイルス対策やバックアップが一瞬だけ開いていることがある。少し待って読み直す
                Thread.Sleep(ReadRetryDelay);
            }
            catch (IOException ex) when (AtomicFile.IsSharing(ex))
            {
                return new(GuardedReadStatus.InUse, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
            {
                return new(GuardedReadStatus.Inaccessible, null);
            }
        }
    }

    /// <summary>読んだバイト列を文字にする。</summary>
    /// <remarks>
    /// 前の作り (<c>File.ReadAllText</c>) と同じ読み方にする。BOM を見て UTF-8 の BOM を除き、
    /// UTF-16 も読む。UTF-8 として正しくないバイトは置き換え文字にして読み進める。
    /// 今まで読めていたファイル (メモ帳で BOM 付きで保存したものなど) を、壊れている扱いにしない。
    /// </remarks>
    public static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

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

    /// <summary>
    /// 読めなかった中身を、同じフォルダーの別の名前で残す。残した場所を返す。残せなければ null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 名前は <c>元の名前.unreadable-日時.拡張子</c> (例 <c>Expzip.settings.unreadable-20261123-101500.json</c>)。
    /// ぶつかれば末尾に <c>-2</c>、<c>-3</c> … を付ける。
    /// 上書きしない指定で書くので、前に残した写しも、書いている間にできたファイルも置き換えない。
    /// 同じ中身の写しが既にあれば、作らずにその場所を返す。壊れたまま読むたびに増えないように。
    /// </para>
    /// <para>
    /// 写すのは読んだバイト列そのもの。ファイルを読み直さないので、読めなかった中身と写しが食い違わない。
    /// 元のファイルの個別のアクセス権も写す。フォルダーから受け継いだ広いアクセス権で、
    /// 最近開いた書庫のパスや、手で書いたルールなどがほかの人に見えないように。写せなければ残さない。
    /// </para>
    /// <para>
    /// 残せなくても読む側は止めない。ここでの失敗は、どれも「残せなかった」として扱う。
    /// </para>
    /// </remarks>
    public static string? TryKeepCopy(string path, byte[] bytes)
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
