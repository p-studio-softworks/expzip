using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Expzip.Archives;

/// <summary>CAB の中の 1 ファイル。</summary>
/// <param name="Name">書庫の中の名前。区切りは <c>\</c> のまま。</param>
/// <param name="Length">展開後の大きさ。</param>
/// <param name="LastWriteTime">更新日時。読めなければ <see langword="default"/>。</param>
internal readonly record struct CabEntry(string Name, long Length, DateTime LastWriteTime);

/// <summary>
/// CAB を読む (#182)。展開は Windows に入っている <c>cabinet.dll</c> に任せる。読み取りのみ。
/// </summary>
/// <remarks>
/// <para>
/// CAB の圧縮は 3 通り (MSZIP / LZX / Quantum) あり、<c>cabinet.dll</c> はすべてを扱える。
/// 復号の部分を自分で書かずに済む。Windows に入っているもので、配るものは増えない。
/// 読み込むのは Windows のシステムフォルダー (System32) からだけにする。
/// exe の隣に置かれた同じ名前の DLL を読まないため。
/// </para>
/// <para>
/// <c>cabinet.dll</c> は、ファイルの読み書きをこちらの関数で行わせる作り。
/// 開くファイルを名前ではなく印で渡し、こちらで本物に結び付ける。書庫の中に書かれた
/// 名前でディスクに直接書かせることは無い。取り出した中身はいったん手元に受け、
/// ほかの形式と同じ道筋 (<see cref="ExtractState"/>) で書き出す。
/// </para>
/// </remarks>
internal static class CabReader
{
    /// <summary>メモリに受ける大きさの上限。これより大きいものは一時ファイルに受ける。</summary>
    private const long InMemoryLimit = 16 * 1024 * 1024;

    /// <summary>CAB のしるし。</summary>
    public static bool IsCab(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.Read(head) == 4 && head.SequenceEqual("MSCF"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>中身を一覧にする。</summary>
    /// <param name="format">CAB か、CAB が入った exe (#182)。</param>
    /// <exception cref="InvalidDataException">CAB として読めない場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ArchiveFormat format = ArchiveFormat.Cab)
    {
        var builder = new ArchiveTreeBuilder(path);
        var count = 0;

        Run(path, format, entry =>
        {
            builder.AddFile(entry.Name, entry.Length, compressedLength: 0,
                compressedLengthKnown: false, entry.LastWriteTime);

            if (++count % 100 == 0)
            {
                progress?.Report(new OpenProgress(count, count));
            }

            return false;
        }, static (_, _) => { }, cancellationToken);

        progress?.Report(new OpenProgress(count, count));

        return builder.Build(path, format, totalCompressedLength: new FileInfo(path).Length);
    }

    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null,
        ArchiveFormat format = ArchiveFormat.Cab)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress);

        try
        {
            Visit(archivePath, sourceNames, (entry, open) =>
            {
                state.TotalBytes += entry.Length;
                state.Write(entry.Name, entry.Length, entry.LastWriteTime, open, cancellationToken);
            }, cancellationToken, format);
        }
        catch (OperationCanceledException)
        {
            // 中断は ExtractState が数えている
        }

        return state.ToResult();
    }

    /// <summary>
    /// 中身を 1 件ずつ、書庫に入っている順に渡す。取り出しと検査で共通に使う。
    /// </summary>
    /// <param name="sourceNames">渡す名前。<see langword="null"/> ならすべて。</param>
    /// <param name="visit">1 件ごとに呼ぶ。2 番目は中身を読む流れを開く。流れは呼び出しの中で閉じる。</param>
    /// <exception cref="InvalidDataException">CAB として読めない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static void Visit(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        Action<CabEntry, Func<Stream>> visit,
        CancellationToken cancellationToken,
        ArchiveFormat format = ArchiveFormat.Cab)
    {
        var (openFirst, siblingFolder) = Source(archivePath, format);
        Visit(openFirst, siblingFolder, sourceNames, visit, cancellationToken);
    }

    /// <summary>
    /// ファイルではなく流れで渡された CAB を読む。MSI に埋め込まれた CAB などに使う (#182)。
    /// </summary>
    /// <param name="openFirst">CAB の流れを開く。呼ぶたびに頭から読める新しい流れを返す。</param>
    /// <param name="siblingFolder">
    /// 続きの CAB を探すフォルダー。<see langword="null"/> なら続きは開かない (欠けているものとして断る)。
    /// </param>
    public static void Visit(
        Func<Stream> openFirst,
        string? siblingFolder,
        IReadOnlySet<string>? sourceNames,
        Action<CabEntry, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        new Session(openFirst, siblingFolder,
            entry => sourceNames is null || sourceNames.Contains(entry.Name),
            (entry, content) => visit(entry, () => new KeepOpenStream(content)),
            cancellationToken).Run();
    }

    /// <summary><c>cabinet.dll</c> に書庫を頭から読ませる。</summary>
    /// <param name="wanted">取り出すかどうか。取り出さないものは展開もしない。</param>
    /// <param name="done">取り出したものを受け取る。流れはこの後で閉じる。</param>
    private static void Run(
        string path,
        ArchiveFormat format,
        Func<CabEntry, bool> wanted,
        Action<CabEntry, Stream> done,
        CancellationToken cancellationToken)
    {
        var (openFirst, siblingFolder) = Source(path, format);
        var session = new Session(openFirst, siblingFolder, wanted, done, cancellationToken);
        session.Run();
    }

    /// <summary>
    /// CAB を頭から読む流れの開き方と、続きの CAB を探すフォルダー。
    /// CAB が入った exe (#182) では、exe の中の CAB を読み、続きは探さない。
    /// </summary>
    private static (Func<Stream> OpenFirst, string? SiblingFolder) Source(string path, ArchiveFormat format)
        => format == ArchiveFormat.CabExe
            ? (CabExeReader.Opener(path), null)
            : (() => ArchiveFile.OpenRead(path), FolderOf(path));

    private static string FolderOf(string path) => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

    /// <summary>
    /// フォルダーの中の、名前で指されたファイルのパス。区切りを含む名前などは開かない
    /// (名前は書庫の中に書かれているので、<c>..\</c> などで別の場所を読ませない)。
    /// </summary>
    internal static string? SiblingOf(string folder, string name)
        => name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0 || name is "." or ".."
            ? null
            : Path.Combine(folder, name);

    /// <summary>1 回の読み取り。<c>cabinet.dll</c> から呼び戻される関数と、開いたファイルを持つ。</summary>
    private sealed class Session(
        Func<Stream> openFirst,
        string? siblingFolder,
        Func<CabEntry, bool> wanted,
        Action<CabEntry, Stream> done,
        CancellationToken cancellationToken)
    {
        /// <summary>書庫を開かせるときの印。本物の名前は渡さない (日本語の名前を ANSI で渡せないため)。</summary>
        private const string Folder = "cab:\\";

        private const string First = "0";

        private readonly Dictionary<nint, Stream> _files = [];
        private readonly Dictionary<nint, CabEntry> _entries = [];
        private nint _next = 1;
        private Exception? _failure;

        // 呼び戻される関数。読み取りの間、捨てられないよう持っておく
        private readonly Native.Alloc _alloc = static size => Marshal.AllocHGlobal((nint)size);
        private readonly Native.Free _free = static memory => Marshal.FreeHGlobal(memory);
        private Native.OpenFile? _open;
        private Native.ReadFile? _read;
        private Native.WriteFile? _write;
        private Native.CloseFile? _close;
        private Native.SeekFile? _seek;
        private Native.Notify? _notify;

        public void Run()
        {
            _open = Open;
            _read = Read;
            _write = Write;
            _close = Close;
            _seek = Seek;
            _notify = Notify;

            var error = Marshal.AllocHGlobal(16);
            var context = IntPtr.Zero;

            try
            {
                context = Native.FDICreate(_alloc, _free, _open, _read, _write, _close, _seek,
                    Native.CpuUnknown, error);

                if (context == IntPtr.Zero)
                {
                    throw new OutOfMemoryException();
                }

                // 分割された CAB は、1 回の読み取りでは今の CAB で始まるファイルしか出てこない。
                // 次の CAB の名前を聞いて、続けて読む。前の CAB から続くファイルは、
                // そちらで取り出し済みなので飛ばされる (知らせが別の種類で来る)
                var current = Ascii(First);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { First };

                while (true)
                {
                    _nextCabinet = null;
                    _started = false;

                    var ok = Native.FDICopy(context, current, Ascii(Folder), 0, _notify, IntPtr.Zero, IntPtr.Zero);

                    if (_failure is not null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ok)
                    {
                        throw Describe(Marshal.ReadInt32(error));
                    }

                    if (_nextCabinet is not { } next || !seen.Add(next))
                    {
                        break;
                    }

                    // 続きが欠けていたら、その名前を言って断る。黙って一覧を途中で切らない
                    var nextPath = SiblingOf(next);
                    if (nextPath is null || !File.Exists(nextPath))
                    {
                        throw Missing(next);
                    }

                    current = Ansi(next);
                }
            }
            finally
            {
                if (context != IntPtr.Zero)
                {
                    Native.FDIDestroy(context);
                }

                Marshal.FreeHGlobal(error);

                foreach (var file in _files.Values)
                {
                    file.Dispose();
                }

                _files.Clear();
                GC.KeepAlive(this);
            }
        }

        /// <summary>今の読み取りで最初に開いた CAB の、次の CAB の名前。</summary>
        private string? _nextCabinet;

        /// <summary>今の読み取りで、最初の CAB の知らせを受けたか。</summary>
        private bool _started;

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + "\0");

        /// <summary>cabinet.dll へ渡す名前。受け取るときと同じく、Windows の ANSI の文字コードにする。</summary>
        private static byte[] Ansi(string text)
        {
            var memory = Marshal.StringToHGlobalAnsi(text);

            try
            {
                var length = 0;
                while (Marshal.ReadByte(memory, length) != 0)
                {
                    length++;
                }

                var bytes = new byte[length + 1];
                Marshal.Copy(memory, bytes, 0, length);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        /// <summary>同じフォルダーにある、続きの CAB のパス。区切りを含む名前などは開かない。</summary>
        private string? SiblingOf(string name)
            => siblingFolder is null ? null : CabReader.SiblingOf(siblingFolder, name);

        /// <summary>続きの CAB が見つからないこと。理由の文にパスが出るよう、引用符で囲んで渡す。</summary>
        private Exception Missing(string name)
            => new FileNotFoundException($"'{SiblingOf(name) ?? name}'");

        /// <summary>失敗の番号を、理由の分かる例外にする。</summary>
        private static Exception Describe(int code) => code switch
        {
            // 見つからない・書庫ではない・版が違う・壊れている・次の書庫が違う
            1 or 2 or 3 or 4 or 10 or 12 => new InvalidDataException(),
            5 => new OutOfMemoryException(),
            6 => new NotSupportedException(),
            _ => new IOException(),
        };

        /// <summary>書庫のファイルを開く。印を本物に結び付ける。</summary>
        /// <remarks>
        /// 続きの書庫 (分割された CAB) の名前は書庫の中に書かれている。
        /// 同じフォルダーにある、区切りを含まない名前のものだけを開く。
        /// </remarks>
        private nint Open(IntPtr name, int flags, int mode)
        {
            try
            {
                var requested = Marshal.PtrToStringAnsi(name) ?? string.Empty;

                if (!requested.StartsWith(Folder, StringComparison.Ordinal))
                {
                    return -1;
                }

                var relative = requested[Folder.Length..];
                Stream stream;

                if (relative == First)
                {
                    stream = openFirst();
                }
                else
                {
                    if (SiblingOf(relative) is not { } sibling)
                    {
                        return -1;
                    }

                    stream = File.OpenRead(sibling);
                }

                return Register(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or InvalidDataException)
            {
                // 見つからない続きの書庫など。cabinet.dll が失敗として扱う
                return -1;
            }
        }

        private nint Register(Stream stream)
        {
            var handle = _next++;
            _files[handle] = stream;
            return handle;
        }

        /// <summary>読み書きの受け渡しに使い回す場所。</summary>
        private byte[] _buffer = new byte[64 * 1024];

        private byte[] Buffer(uint count)
        {
            if (_buffer.Length < count)
            {
                _buffer = new byte[count];
            }

            return _buffer;
        }

        private uint Read(nint handle, IntPtr buffer, uint count)
        {
            try
            {
                var bytes = Buffer(count);
                var got = _files[handle].Read(bytes, 0, (int)count);
                Marshal.Copy(bytes, 0, buffer, got);
                return (uint)got;
            }
            catch (Exception ex)
            {
                _failure ??= ex;
                return uint.MaxValue;
            }
        }

        private uint Write(nint handle, IntPtr buffer, uint count)
        {
            try
            {
                var bytes = Buffer(count);
                Marshal.Copy(buffer, bytes, 0, (int)count);
                _files[handle].Write(bytes, 0, (int)count);
                return count;
            }
            catch (Exception ex)
            {
                _failure ??= ex;
                return uint.MaxValue;
            }
        }

        private int Close(nint handle)
        {
            if (_files.Remove(handle, out var stream))
            {
                stream.Dispose();
            }

            return 0;
        }

        private int Seek(nint handle, int distance, int origin)
        {
            try
            {
                return (int)_files[handle].Seek(distance, (SeekOrigin)origin);
            }
            catch (Exception ex)
            {
                _failure ??= ex;
                return -1;
            }
        }

        /// <summary>cabinet.dll からの知らせ。例外はここで止めて、読み取りを打ち切らせる。</summary>
        private nint Notify(int type, IntPtr info)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return -1;
                }

                return type switch
                {
                    Native.CabinetInfo => CabinetInfo(info),
                    Native.CopyFile => BeginFile(info),
                    Native.CloseFileInfo => EndFile(info),
                    Native.NextCabinet => NextCabinet(info),
                    _ => 0,
                };
            }
            catch (Exception ex)
            {
                _failure ??= ex;
                return -1;
            }
        }

        /// <summary>CAB を開いた知らせ。最初の CAB の、次の CAB の名前を覚える。</summary>
        private nint CabinetInfo(IntPtr info)
        {
            if (!_started)
            {
                _started = true;
                var next = Marshal.PtrToStringAnsi(Native.Pointer(info, 0));
                _nextCabinet = string.IsNullOrEmpty(next) ? null : next;
            }

            return 0;
        }

        /// <summary>
        /// ファイルが次の CAB へ続く知らせ。0 を返すと、同じフォルダーの次の CAB を開きに行く。
        /// </summary>
        /// <remarks>
        /// 開けなかったときは、失敗の番号を付けて同じ知らせがもう一度来る。
        /// そのまま 0 を返すと回り続けるので、ここで打ち切る。
        /// </remarks>
        private nint NextCabinet(IntPtr info)
        {
            if (Marshal.ReadInt32(info, Native.ErrorAt) == 0)
            {
                return 0;
            }

            _failure ??= Missing(Marshal.PtrToStringAnsi(Native.Pointer(info, 0)) ?? string.Empty);
            return -1;
        }

        /// <summary>1 ファイルの始まり。取り出すなら受け皿を返し、取り出さないなら 0。</summary>
        private nint BeginFile(IntPtr info)
        {
            var entry = new CabEntry(
                ReadName(Native.Pointer(info, 0), Native.Short(info, Native.AttribsAt)),
                (uint)Marshal.ReadInt32(info),
                ToDateTime(Native.Short(info, Native.DateAt), Native.Short(info, Native.TimeAt)));

            if (!wanted(entry))
            {
                return 0;
            }

            Stream sink = entry.Length <= InMemoryLimit
                ? new MemoryStream((int)entry.Length)
                : new FileStream(
                    Path.Combine(Path.GetTempPath(), $"Expzip-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.DeleteOnClose);

            var handle = Register(sink);
            _entries[handle] = entry;
            return handle;
        }

        /// <summary>1 ファイルの終わり。受けた中身を渡してから閉じる。</summary>
        private nint EndFile(IntPtr info)
        {
            var handle = Marshal.ReadIntPtr(info, Native.HandleAt);

            if (!_files.Remove(handle, out var content) || !_entries.Remove(handle, out var entry))
            {
                return 1;
            }

            using (content)
            {
                content.Position = 0;
                done(entry, content);
            }

            return 1;
        }

        /// <summary>
        /// 名前を読む。印が立っていれば UTF-8、なければ古い文字コード
        /// (ZIP と同じく、UTF-8 として読めるかを見て決める)。
        /// </summary>
        private static string ReadName(IntPtr name, int attributes)
        {
            var length = 0;
            while (Marshal.ReadByte(name, length) != 0)
            {
                length++;
            }

            var bytes = new byte[length];
            Marshal.Copy(name, bytes, 0, length);

            const int nameIsUtf8 = 0x80;
            return (attributes & nameIsUtf8) != 0
                ? Encoding.UTF8.GetString(bytes)
                : ZipArchiveReader.EntryNameEncoding.GetString(bytes);
        }

        /// <summary>MS-DOS の形の日付と時刻を読む。</summary>
        private static DateTime ToDateTime(int date, int time)
        {
            try
            {
                return new DateTime(
                    1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F,
                    time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
            }
            catch (ArgumentOutOfRangeException)
            {
                return default;
            }
        }
    }

    /// <summary>
    /// 受けた中身を読ませる包み。閉じても中身は閉じない (閉じるのは受けた側の後始末)。
    /// </summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary><c>cabinet.dll</c> の関数と、受け渡しの形 (fdi.h)。</summary>
    private static class Native
    {
        public const int CpuUnknown = -1;

        // 知らせの種類
        public const int CabinetInfo = 0;
        public const int CopyFile = 2;
        public const int CloseFileInfo = 3;
        public const int NextCabinet = 4;

        // 知らせの中身の位置。fdi.h は 32 ビットでは 4 バイト境界で詰め、64 ビットでは詰めない。
        // どちらでも、先頭の 4 バイトの数の後、ポインターの大きさの位置から並ぶ
        private static readonly int PointerSize = IntPtr.Size;
        public static readonly int HandleAt = 5 * PointerSize;
        public static readonly int DateAt = HandleAt + PointerSize;
        public static readonly int TimeAt = DateAt + 2;
        public static readonly int AttribsAt = TimeAt + 2;

        // attribs の後に、組の番号・CAB の番号・フォルダーの番号 (2 バイトずつ) が続き、失敗の番号 (4 バイト)
        public static readonly int ErrorAt = AttribsAt + 8;

        /// <summary>知らせの中の文字 (psz1、psz2、psz3) を読む。</summary>
        public static IntPtr Pointer(IntPtr info, int index) => Marshal.ReadIntPtr(info, (index + 1) * PointerSize);

        public static int Short(IntPtr info, int offset) => (ushort)Marshal.ReadInt16(info, offset);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr Alloc(uint size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Free(IntPtr memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint OpenFile(IntPtr name, int flags, int mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ReadFile(nint handle, IntPtr buffer, uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint WriteFile(nint handle, IntPtr buffer, uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int CloseFile(nint handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SeekFile(nint handle, int distance, int origin);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint Notify(int type, IntPtr info);

        [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr FDICreate(
            Alloc alloc, Free free, OpenFile open, ReadFile read, WriteFile write, CloseFile close,
            SeekFile seek, int cpuType, IntPtr error);

        [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FDICopy(
            IntPtr context, byte[] cabinet, byte[] cabinetPath, int flags, Notify notify,
            IntPtr decrypt, IntPtr user);

        [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FDIDestroy(IntPtr context);
    }
}
