using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Checksum;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Splitting;

/// <summary>分割の経過 (#59)。</summary>
/// <param name="Done">ここまでに書き出したバイト数。</param>
/// <param name="Total">元のファイルの大きさ。</param>
/// <param name="CurrentName">いま書いている断片の名前。</param>
internal readonly record struct SplitProgress(long Done, long Total, string CurrentName);

/// <summary>分割の結果。</summary>
/// <param name="Parts">作った断片の数。</param>
/// <param name="JoinerName">作った結合用のプログラムの名前。</param>
/// <param name="Cancelled">途中で中断されたか。</param>
/// <param name="BatchName">作った結合用のバッチファイルの名前。作らなかったときは null (#343)。</param>
internal sealed record SplitResult(int Parts, string JoinerName, bool Cancelled, string? BatchName = null);

/// <summary>
/// 結合用のプログラムの名前を使えない (#373)。
/// </summary>
/// <param name="taken">その名前に既にある、置き換えられないものの名前。名前が空になるときは <see langword="null"/>。</param>
internal sealed class JoinerNameException(string? taken)
    : IOException(taken is null ? "The name of the joining program would be empty." : $"'{taken}' already exists.")
{
    /// <summary>その名前に既にある、置き換えられないものの名前。</summary>
    public string? Taken { get; } = taken;
}

/// <summary>
/// 大きなファイルを決まった大きさに分ける (#59)。
/// </summary>
/// <remarks>
/// <para>
/// 断片は <c>&lt;元の名前&gt;.001</c> <c>.002</c> … とする。7-Zip と同じ形で、
/// 受け取った人が別のソフトでも結合できる。
/// </para>
/// <para>
/// あわせて、断片を結合する小さな実行ファイルを <c>&lt;元の名前&gt;.exe</c> として作る。
/// 受け取った人が Expzip を持っていなくても元に戻せるようにするため。中身は
/// <c>tools/joiner</c> にある C のプログラムで、末尾に元の名前と照合用の値を書き足して配る。
/// 元の名前に文字の向きを変える制御文字があれば、プログラムの名前からはその文字を除く (#373、<see cref="JoinerPath"/>)。
/// </para>
/// <para>
/// <b>元のファイルは消さない。</b> 分割は移動ではなく写しを作る操作として扱う。
/// </para>
/// </remarks>
internal static class FileSplitter
{
    /// <summary>分割サイズの下限。これより小さいと断片が増えすぎる。</summary>
    public const long MinimumChunk = 128 * 1024;

    /// <summary>まとめて読み書きする大きさ。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>結合用のプログラムの末尾に書き足す印。</summary>
    private static ReadOnlySpan<byte> FooterMagic => "EXPZSPLT"u8;

    /// <summary>書き足す固定部の大きさ。結合用のプログラム側と揃えること。</summary>
    private const int FooterSize = 32;

    /// <summary>同梱してある結合用のプログラムの置き場。</summary>
    private const string JoinerResource = "Expzip.Resources.joiner-x86.exe";

    /// <summary>ファイルを分ける。</summary>
    /// <param name="sourcePath">分ける対象。</param>
    /// <param name="destinationDirectory">断片と結合用のプログラムの置き場。</param>
    /// <param name="chunkSize">1つあたりの大きさ。</param>
    /// <param name="progress">経過の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <param name="makeBatch">結合用のバッチファイルも作るか (#343)。</param>
    /// <exception cref="IOException">読み書きに失敗した場合。</exception>
    public static SplitResult Split(
        string sourcePath,
        string destinationDirectory,
        long chunkSize,
        IProgress<SplitProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool makeBatch = false)
    {
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(destinationDirectory, name);

        // 全部を一時的な名前に書いてから、まとめて置き換える。失敗や中止で、前からあった同じ名前のファイルは消えない (#320)
        var written = new List<AtomicFile>();

        // MotW は分割ファイルに引き継ぐ。結合用のプログラムには付けない。手元で作っただけのものを実行するたびに
        // Windows の警告が出てしまう。結合用のプログラムは、結合するときに分割ファイルの MotW を読む (#321)
        var zone = MarkOfTheWeb.TryRead(sourcePath);

        // 結合用のプログラムの名前は、書き始める前に決めて確かめる。使えなければ保存先のフォルダーも作らない (#373)
        var joinerPath = JoinerPath(target);
        var replaceJoiner = MayReplaceJoiner(joinerPath, name);

        Directory.CreateDirectory(destinationDirectory);

        try
        {
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var total = source.Length;
            var buffer = new byte[BufferSize];
            var crc = new Crc32();
            using var sha = makeBatch && JoinBatch.CanMake(name) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            var parts = 0;
            long done = 0;

            while (done < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var partPath = PartPath(target, ++parts);
                var file = AtomicFile.Create(partPath, overwrite: true, zone);
                written.Add(file);
                progress?.Report(new SplitProgress(done, total, Path.GetFileName(partPath)));

                {
                    var part = file.Stream;
                    var remaining = Math.Min(chunkSize, total - done);

                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var want = (int)Math.Min(buffer.Length, remaining);
                        var read = source.Read(buffer, 0, want);

                        if (read <= 0)
                        {
                            // 途中で読めなくなった。書きかけを残さない
                            throw new IOException(Localization.Strings.SplitSourceShrank);
                        }

                        part.Write(buffer, 0, read);
                        crc.Update(new ArraySegment<byte>(buffer, 0, read));
                        sha?.AppendData(buffer, 0, read);
                        remaining -= read;
                        done += read;

                        progress?.Report(new SplitProgress(
                            done, total, Path.GetFileName(partPath)));
                    }
                }
            }

            var joiner = AtomicFile.Create(joinerPath, overwrite: replaceJoiner);
            written.Add(joiner);
            WriteJoiner(joiner.Stream, name, parts, total, crc.Value);

            // バッチファイルにも MotW は付けない。結合するときに分割ファイルの MotW を見る (#343)
            string? batchName = null;
            if (sha is not null)
            {
                var batchPath = BatchPath(target);
                var batch = AtomicFile.Create(batchPath);
                written.Add(batch);
                JoinBatch.Write(batch.Stream, name, parts, total, sha.GetHashAndReset(), JoinBatch.NewWorkName());
                batchName = Path.GetFileName(batchPath);
            }

            try
            {
                AtomicFile.CommitAll(written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && !replaceJoiner && Occupied(joinerPath))
            {
                // 分割の間に、文字を除いた結合用のプログラムの名前に何かが置かれた。置き換えずに止まったので、その名前を挙げて知らせる (#373)
                throw new JoinerNameException(Path.GetFileName(joinerPath));
            }

            return new SplitResult(parts, Path.GetFileName(joinerPath), false, batchName);
        }
        catch (OperationCanceledException)
        {
            // 中途半端な断片は結合に使えず、残しておくと「揃っている」と勘違いさせる。
            // 書きかけは下で消える
            return new SplitResult(0, string.Empty, true);
        }
        finally
        {
            foreach (var file in written)
            {
                file.Dispose();
            }
        }
    }

    /// <summary>分割すると置き換わる、既にあるファイル。</summary>
    public static IReadOnlyList<string> ExistingOutputs(
        string sourcePath, string destinationDirectory, long chunkSize, bool makeBatch = false)
    {
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(destinationDirectory, name);
        var parts = CountParts(new FileInfo(sourcePath).Length, chunkSize);

        // 文字を除いた名前の結合用のプログラムは、同じ元の名前から作ったものだけを尋ねる。ほかのものがあれば例外 (#373)
        var joinerPath = JoinerPath(target);
        var joiner = MayReplaceJoiner(joinerPath, name) ? [joinerPath] : Array.Empty<string>();

        return Enumerable.Range(1, parts)
            .Select(number => PartPath(target, number))
            .Concat(joiner)
            .Concat(makeBatch && JoinBatch.CanMake(name) ? [BatchPath(target)] : [])
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>
    /// 結合用のプログラムのパス。元の名前から、文字の向きを変える制御文字を除いて <c>.exe</c> を付ける (#373)。
    /// </summary>
    /// <remarks>
    /// 元の名前のままだと、U+202E などで拡張子を偽装した名前の実行ファイルになり、Defender に止められる。
    /// 結合用のプログラムは自分の名前ではなく、末尾に書いた元の名前で分割ファイルを探すので、名前を変えても結合できる。
    /// 分割ファイルの名前は元のまま (本当の拡張子は必ず数字なので、実行されない)。
    /// </remarks>
    /// <exception cref="JoinerNameException">除くと名前が空になる場合。</exception>
    /// <exception cref="ReservedNameException">除いた名前が、この Windows で装置を指す場合。</exception>
    public static string JoinerPath(string target)
    {
        var name = Path.GetFileName(target);
        var stripped = Inspection.BidiControl.Strip(name);
        if (stripped.Length == 0)
        {
            throw new JoinerNameException(null);
        }

        var path = Path.Combine(Path.GetDirectoryName(target) ?? string.Empty, stripped + ".exe");
        if (stripped != name && ReservedNameException.IsReserved(Path.GetFullPath(path)))
        {
            throw new ReservedNameException(path);
        }

        return Guard(path);
    }

    /// <summary>結合用のバッチファイルのパス。作れる名前 (<see cref="JoinBatch.CanMake"/>) のときだけ呼ぶ。</summary>
    private static string BatchPath(string target) => Guard(target + ".cmd");

    /// <summary>
    /// 最後の守り。文字の向きを変える制御文字が残った名前の実行ファイルを作らない (#373)。
    /// </summary>
    /// <remarks>
    /// 名前を決める処理があとで壊れても、アプリを使う人の PC にもテストを走らせる PC にも、偽装した名前の実行ファイルを作らない。
    /// </remarks>
    private static string Guard(string path)
        => Inspection.BidiControl.Contains(Path.GetFileName(path))
            ? throw new InvalidOperationException("結合用のファイルの名前に、文字の向きを変える文字が残っている")
            : path;

    /// <summary>
    /// 結合用のプログラムの名前にあるものを置き換えてよいか。
    /// </summary>
    /// <remarks>
    /// <para>名前を変えていなければ、今までどおり置き換える (尋ねるのは画面)。</para>
    /// <para>
    /// 文字を除いた名前には、別の元のファイルの結合用のプログラムや、関係の無いファイルがあることがある。
    /// 何も無ければ置き換えない指定で作り、分割の間に置かれたものも置き換えない。
    /// Expzip が同じ元の名前から作った結合用のプログラムなら (同じファイルを同じ場所にもう一度分割したとき)、
    /// ほかの分割ファイルと同じく尋ねて置き換える。それ以外のものがあれば、何も書かずに止める。
    /// </para>
    /// </remarks>
    /// <exception cref="JoinerNameException">置き換えられないものがある場合。</exception>
    private static bool MayReplaceJoiner(string joinerPath, string name)
    {
        if (Path.GetFileName(joinerPath) == name + ".exe")
        {
            return true;
        }

        if (!Occupied(joinerPath))
        {
            return false;
        }

        var attributes = File.GetAttributes(joinerPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && IsJoinerFor(joinerPath, name))
        {
            return true;
        }

        throw new JoinerNameException(Path.GetFileName(joinerPath));
    }

    /// <summary>その名前に何かあるか。リンクはたどらず、フォルダーや壊れたリンクも「ある」とみなす。</summary>
    private static bool Occupied(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Expzip が、この元の名前から作った結合用のプログラムか。末尾の印と名前だけを読む。</summary>
    private static bool IsJoinerFor(string path, string name)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var tail = encoded.Length + FooterSize;
            if (stream.Length < tail)
            {
                return false;
            }

            var buffer = new byte[tail];
            stream.Seek(-tail, SeekOrigin.End);
            stream.ReadExactly(buffer);

            var footer = buffer.AsSpan(encoded.Length);
            return footer[..FooterMagic.Length].SequenceEqual(FooterMagic)
                   && BitConverter.ToUInt32(footer[28..]) == (uint)name.Length
                   && buffer.AsSpan(0, encoded.Length).SequenceEqual(encoded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 断片の名前。7-Zip と同じく最低3桁にする。
    /// </summary>
    /// <remarks>
    /// 1000 個を超えると4桁になる。結合用のプログラム側も同じ数え方をしている。
    /// </remarks>
    public static string PartPath(string target, int number)
        => $"{target}.{number:D3}";

    /// <summary>この大きさで分けたら断片がいくつになるか。</summary>
    public static int CountParts(long length, long chunkSize)
        => (int)Math.Min(int.MaxValue, (length + chunkSize - 1) / Math.Max(1, chunkSize));

    /// <summary>
    /// 結合用のプログラムを書き出す。
    /// </summary>
    /// <remarks>
    /// 同梱の実行ファイルの後ろに、元の名前と照合用の値を書き足す。形は
    /// <c>tools/joiner/README.md</c> に書いてある。名前を固定部より前に置くのは、
    /// 長さが変わるため。読む側は末尾から固定部を読み、そこに書かれた長さのぶんだけ
    /// 手前へ戻って名前を読む。
    /// </remarks>
    private static void WriteJoiner(Stream output, string name, int parts, long size, long crc)
    {
        using var stub = typeof(FileSplitter).Assembly
            .GetManifestResourceStream(JoinerResource)
            ?? throw new InvalidOperationException(Strings.EmbeddedToolMissing(JoinerResource));

        stub.CopyTo(output);

        var encoded = Encoding.Unicode.GetBytes(name);
        output.Write(encoded);

        var footer = new byte[FooterSize];
        FooterMagic.CopyTo(footer);
        BitConverter.TryWriteBytes(footer.AsSpan(8), 1u);
        BitConverter.TryWriteBytes(footer.AsSpan(12), (uint)parts);
        BitConverter.TryWriteBytes(footer.AsSpan(16), (ulong)size);
        BitConverter.TryWriteBytes(footer.AsSpan(24), (uint)crc);
        BitConverter.TryWriteBytes(footer.AsSpan(28), (uint)name.Length);
        output.Write(footer);
    }
}
