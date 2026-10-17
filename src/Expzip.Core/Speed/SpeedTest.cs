using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Expzip.Archives;

namespace Expzip.Speed;

/// <summary>測っている段階 (#269)。画面に何をしているかを出すのに使う。</summary>
/// <param name="Step">いまの段階。</param>
/// <param name="Kind">試しているデータの種類。ディスクの段階では使わない。</param>
/// <param name="Done">終わった段階の数。</param>
/// <param name="Total">段階の総数。</param>
internal readonly record struct SpeedProgress(SpeedStep Step, SpeedKind Kind, int Done, int Total);

internal enum SpeedStep
{
    /// <summary>ディスクの読み書きの速さを測っている。</summary>
    Disk,

    /// <summary>試すデータを作っている。</summary>
    Preparing,

    Compressing,

    Extracting,
}

/// <summary>
/// このパソコンで、圧縮と展開にどのくらいかかるかを測る (#269)。
/// </summary>
/// <remarks>
/// <para>
/// <b>ふだんの圧縮・展開と同じ処理</b> (<see cref="ZipArchiveWriter.Add"/> と
/// <see cref="ArchiveExtractor.Extract"/>) を、作ったデータに実際にかけて測る。
/// コアの数だけ並べることも、HDD やネットワーク上のフォルダーでは並べないことも (#195)、
/// ふだんどおりに効く。
/// </para>
/// <para>
/// 試すデータは 3 種類。文書のように縮むもの、写真のように縮まないもの、小さなファイルが
/// たくさん。展開では出所の印を付ける (#12)。インターネットから入手した書庫を展開するときと
/// 同じ手間がかかり、ファイルが多いと効く (#200)。
/// </para>
/// <para>
/// 揺れを抑えるため、どれも慣らしの 1 回の後に 3 回測り、真ん中の値を使う。
/// 速いパソコンで全体が 15 秒ほど。遅いディスクでは試す量を減らす。終わったら、中断しても失敗しても、
/// 試したファイルは消す。
/// </para>
/// </remarks>
internal static class SpeedTest
{
    /// <summary>試すデータの量 (速いディスクのとき)。</summary>
    private const long DocumentBytes = 128L * 1024 * 1024;

    private const long MediaBytes = 256L * 1024 * 1024;

    private const int SmallFileCount = 4000;

    /// <summary>小さなファイル 1 個の大きさ。見積もりもこの大きさのファイルが並ぶとみなす。</summary>
    public const int SmallFileBytes = 8 * 1024;

    /// <summary>1 個のファイルの大きさ。</summary>
    private const int DocumentFileBytes = 4 * 1024 * 1024;

    private const int MediaFileBytes = 16 * 1024 * 1024;

    /// <summary>この速さより遅いディスクでは、試す量をその分だけ減らす。</summary>
    private const double FullSizeDiskSpeed = 512.0 * 1024 * 1024;

    /// <summary>試す量をここまでは減らしてよい。少なすぎると、1 回ごとの手間ばかりを測ることになる。</summary>
    private const double MinimumScale = 1.0 / 8;

    /// <summary>ディスクの速さを測る量と時間の上限。遅いネットワークでも長くは待たせない。</summary>
    private const long DiskTestBytes = 256L * 1024 * 1024;

    private static readonly TimeSpan DiskTestLimit = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 測る回数。真ん中の値を使い、1 回だけほかの処理に割り込まれたときの揺れを抑える。
    /// </summary>
    private const int Runs = 3;

    /// <summary>
    /// 測る前に、捨てる前提で動かす回数。起動して最初の 1 回は、プログラムを機械語に直す手間が入り、
    /// 2 割ほど遅く出る。
    /// </summary>
    private const int WarmUpRuns = 1;

    /// <summary>
    /// 溜め置き (キャッシュ) を通さずに開く関数。画面の側が差し替えられる。<see langword="null"/> なら
    /// Windows の方法で開く。
    /// </summary>
    /// <remarks>
    /// 受け取るのはパス、開き方、読むか書くか。書き終えるのは <see cref="RandomAccess.FlushToDisk"/> で待つ。
    /// 開けなければ例外を投げてよい。そのときはふつうに開いて測る。
    /// </remarks>
    internal static Func<string, FileMode, FileAccess, Microsoft.Win32.SafeHandles.SafeFileHandle>? OpenUncachedOverride
    {
        get;
        set;
    }

    private static readonly SpeedKind[] Kinds = [SpeedKind.Documents, SpeedKind.Media, SpeedKind.SmallFiles];

    /// <summary>
    /// <paramref name="folder"/> の中で試して測る。
    /// </summary>
    /// <param name="folder">試すファイルを置くフォルダー。この中に作業用のフォルダーを作り、終わったら消す。</param>
    /// <param name="location">結果に残す場所。一時フォルダーなら空。</param>
    /// <param name="progress">進み具合の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="OperationCanceledException">中断した場合。</exception>
    /// <exception cref="IOException">書き込めない場合や、空きが足りない場合。</exception>
    public static SpeedResult Run(
        string folder,
        string location,
        IProgress<SpeedProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SpeedResult
        {
            MeasuredAt = DateTime.Now,
            MachineName = Environment.MachineName,
            Cpu = MachineInfo.CpuName(),
            Cores = Environment.ProcessorCount,
            MemoryBytes = MachineInfo.MemoryBytes(),
            Location = location,
            Storage = MachineInfo.StorageOf(folder),
            OnBattery = MachineInfo.OnBattery(),
        };

        var work = Path.Combine(folder, $"expzip-speedtest-{Guid.NewGuid():N}"[..26]);
        Directory.CreateDirectory(work);

        var total = 1 + (Kinds.Length * (1 + (2 * (WarmUpRuns + Runs))));
        var done = 0;

        try
        {
            progress?.Report(new SpeedProgress(SpeedStep.Disk, default, done, total));
            var disks = new List<(double Write, double Read)>();
            for (var i = 0; i < Runs; i++)
            {
                disks.Add(MeasureDisk(Path.Combine(work, "disk.bin"), cancellationToken));
            }

            result.DiskWriteBytesPerSecond = Median(disks.Select(d => d.Write));
            result.DiskReadBytesPerSecond = Median(disks.Select(d => d.Read));
            done++;

            var slower = Math.Min(result.DiskWriteBytesPerSecond, result.DiskReadBytesPerSecond);
            var scale = Math.Clamp(slower / FullSizeDiskSpeed, MinimumScale, 1);

            foreach (var kind in Kinds)
            {
                var source = Path.Combine(work, $"{kind}");
                var archive = Path.Combine(work, $"{kind}.zip");
                var output = Path.Combine(work, $"{kind}-out");

                progress?.Report(new SpeedProgress(SpeedStep.Preparing, kind, done, total));
                var (bytes, files) = Generate(kind, source, scale, cancellationToken);
                done++;

                // 慣らしの分は捨て、残りの真ん中の値を使う
                var compress = new List<double>();
                var extract = new List<double>();
                for (var i = 0; i < WarmUpRuns + Runs; i++)
                {
                    File.Delete(archive);
                    TempWorkspace.TryDeleteTree(output);

                    progress?.Report(new SpeedProgress(SpeedStep.Compressing, kind, done, total));
                    var c = Time(() => Compress(source, archive, cancellationToken));
                    done++;

                    progress?.Report(new SpeedProgress(SpeedStep.Extracting, kind, done, total));
                    var e = Time(() => Extract(archive, output, cancellationToken));
                    done++;

                    if (i >= WarmUpRuns)
                    {
                        compress.Add(c.TotalSeconds);
                        extract.Add(e.TotalSeconds);
                    }
                }

                result.Kinds.Add(new SpeedKindResult
                {
                    Kind = kind,
                    ContentBytes = bytes,
                    FileCount = files,
                    ArchiveBytes = new FileInfo(archive).Length,
                    CompressSeconds = Median(compress),
                    ExtractSeconds = Median(extract),
                });

                // 次の種類を試す前に消す。全部を残すと、試しに要る空きが 3 倍になる
                TempWorkspace.TryDeleteTree(source);
                TempWorkspace.TryDeleteTree(output);
                File.Delete(archive);
            }

            return result;
        }
        finally
        {
            TempWorkspace.TryDeleteTree(work);
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static TimeSpan Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    /// <summary>ふだんの「新しい書庫に追加」と同じ道で圧縮する。</summary>
    private static void Compress(string source, string archive, CancellationToken cancellationToken)
    {
        ZipArchiveWriter.CreateEmpty(archive);
        var added = ZipArchiveWriter.Add(archive, [source], string.Empty, replaceExisting: false, null, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (added.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (added.Failed.Count > 0)
        {
            throw new IOException(added.Failed[0].Reason);
        }
    }

    /// <summary>ふだんの展開と同じ道で、書庫全体を展開する。</summary>
    private static void Extract(string archive, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        var extracted = ArchiveExtractor.Extract(
            archive, ArchiveFormat.Zip, null, output, overwrite: true, null, cancellationToken, MarkOfTheWeb.Internet);

        cancellationToken.ThrowIfCancellationRequested();
        if (extracted.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (extracted.Failed.Count > 0)
        {
            throw new IOException(extracted.Failed[0].Reason);
        }
    }

    // ------------------------------------------------------------------ 試すデータ

    /// <summary>試すデータを作る。</summary>
    /// <returns>作ったデータの合計の大きさと、ファイルの数。</returns>
    /// <remarks>毎回同じ中身にする。中身で縮み方が変わると、測るたびに結果が揺れる。</remarks>
    private static (long Bytes, int Files) Generate(
        SpeedKind kind, string folder, double scale, CancellationToken cancellationToken)
    {
        var random = new Random(269);
        var (count, size) = kind switch
        {
            SpeedKind.Documents => ((int)(DocumentBytes * scale / DocumentFileBytes), DocumentFileBytes),
            SpeedKind.Media => ((int)(MediaBytes * scale / MediaFileBytes), MediaFileBytes),
            _ => ((int)(SmallFileCount * scale), SmallFileBytes),
        };
        count = Math.Max(1, count);

        var buffer = new byte[size];
        var text = new TextMaker(random);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (kind == SpeedKind.Media)
            {
                // 乱数は縮まない。既に圧縮されたものと同じく格納になる (#38)
                random.NextBytes(buffer);
            }
            else
            {
                text.Fill(buffer);
            }

            // 小さなファイルは 100 個ずつフォルダーに分ける。1 つのフォルダーに何千個も並ぶ形は少ない
            var path = kind == SpeedKind.SmallFiles
                ? Path.Combine(folder, $"{i / 100:D3}", $"{i:D5}.txt")
                : Path.Combine(folder, $"{i:D3}.{(kind == SpeedKind.Media ? "jpg" : "txt")}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, buffer);
        }

        return ((long)count * size, count);
    }

    /// <summary>
    /// 文書のように縮む文字を作る。決まった数の語を、でたらめな順に並べる。
    /// </summary>
    /// <remarks>
    /// 同じ文を繰り返すと、圧縮がほとんど手間をかけずに縮めてしまい、文書より速く見える。
    /// 語の並びをでたらめにすると、ふつうの文書と同じくらいに縮み (3 分の 1 ほど)、同じくらい手間がかかる。
    /// </remarks>
    private sealed class TextMaker
    {
        private readonly Random _random;
        private readonly byte[][] _words;

        public TextMaker(Random random)
        {
            _random = random;

            const string letters = "abcdefghijklmnopqrstuvwxyz";
            _words = Enumerable.Range(0, 2000).Select(_ =>
            {
                var length = random.Next(2, 10);
                var word = new char[length + 1];
                for (var i = 0; i < length; i++)
                {
                    word[i] = letters[random.Next(letters.Length)];
                }

                word[length] = ' ';
                return Encoding.ASCII.GetBytes(word);
            }).ToArray();
        }

        public void Fill(byte[] buffer)
        {
            var at = 0;
            var line = 0;

            while (at < buffer.Length)
            {
                var word = _words[_random.Next(_words.Length)];
                var n = Math.Min(word.Length, buffer.Length - at);
                word.AsSpan(0, n).CopyTo(buffer.AsSpan(at));
                at += n;

                if ((line += n) > 70 && at < buffer.Length)
                {
                    buffer[at++] = (byte)'\n';
                    line = 0;
                }
            }
        }
    }

    // ------------------------------------------------------------------ ディスク

    /// <summary>
    /// 保存先のディスクに読み書きする速さを測る。
    /// </summary>
    /// <returns>書く速さと読む速さ (バイト / 秒)。</returns>
    /// <remarks>
    /// <para>
    /// Windows の溜め置き (キャッシュ) を通さずに読み書きする。通すと、書いたつもりでメモリーに
    /// 溜まっただけのものや、メモリーから読み返しただけのものを測ってしまい、ディスクが何倍も速く見える。
    /// 溜め置きを通さない読み書きができない場所では、通して測る。
    /// </para>
    /// <para>
    /// 量か時間のどちらかが上限に届いたら止める。遅いネットワークの先でも長く待たせない。
    /// </para>
    /// </remarks>
    private static (double Write, double Read) MeasureDisk(string path, CancellationToken cancellationToken)
    {
        const int chunk = 4 * 1024 * 1024;

        // 溜め置きを通さない読み書きは、メモリーの位置をディスクの区切り (4096 バイト) に揃える必要がある
        const int alignment = 4096;
        var raw = GC.AllocateUninitializedArray<byte>(chunk + alignment, pinned: true);
        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);

        try
        {
            var offset = (int)((alignment - (handle.AddrOfPinnedObject() % alignment)) % alignment);
            var buffer = raw.AsMemory(offset, chunk);
            new Random(269).NextBytes(buffer.Span);

            long written = 0;
            var watch = Stopwatch.StartNew();

            using (var file = OpenUncached(path, FileMode.Create, FileAccess.Write))
            {
                while (written < DiskTestBytes && watch.Elapsed < DiskTestLimit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RandomAccess.Write(file, buffer.Span, written);
                    written += chunk;
                }

                // 溜め置きを通したときに備え、ディスクに書き終えるのを待つ
                RandomAccess.FlushToDisk(file);
            }

            var write = written / watch.Elapsed.TotalSeconds;

            long read = 0;
            watch.Restart();

            using (var file = OpenUncached(path, FileMode.Open, FileAccess.Read))
            {
                while (read < written && watch.Elapsed < DiskTestLimit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = RandomAccess.Read(file, buffer.Span, read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }
            }

            return (write, read / watch.Elapsed.TotalSeconds);
        }
        finally
        {
            handle.Free();
            File.Delete(path);
        }
    }

    /// <summary>溜め置きを通さずに開く。できない場所では、ふつうに開く。</summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenUncached(
        string path, FileMode mode, FileAccess access)
    {
        // FILE_FLAG_NO_BUFFERING。.NET に名前は無いが、値を渡せば使える
        const FileOptions noBuffering = (FileOptions)0x20000000;

        if (OpenUncachedOverride is { } open)
        {
            try
            {
                return open(path, mode, access);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
            {
                // 下でふつうに開く。作りかけのファイルは開き直すときに作り直す
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            try
            {
                return File.OpenHandle(path, mode, access, FileShare.None, noBuffering | FileOptions.WriteThrough);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                // 下でふつうに開く
            }
        }

        return File.OpenHandle(path, mode, access, FileShare.None, FileOptions.WriteThrough);
    }
}
