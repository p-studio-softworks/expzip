using System.IO;
using System.Runtime.InteropServices;

namespace Expzip.Archives;

/// <summary>MSI の中の 1 ファイル。</summary>
/// <param name="Path">一覧に出す、書庫の中のパス。区切りは <c>/</c>。</param>
/// <param name="Key">File 表の鍵。CAB の中では、この鍵が名前になっている。</param>
/// <param name="SourcePath">圧縮されていないファイルの、MSI の隣での置き場所。区切りは <c>/</c>。</param>
/// <param name="Length">展開後の大きさ。</param>
/// <param name="Sequence">取り出す順番。どの CAB に入っているかは、これで決まる。</param>
/// <param name="Compressed">CAB に入っているか。入っていなければ MSI の隣に置かれている。</param>
internal readonly record struct MsiFile(
    string Path, string Key, string SourcePath, long Length, int Sequence, bool Compressed);

/// <summary>ファイルの入れ物 (Media 表の 1 行)。</summary>
/// <param name="LastSequence">この入れ物に入っている、最後のファイルの順番。</param>
/// <param name="Cabinet">
/// CAB の名前。<c>#</c> で始まれば MSI に埋め込まれている。空なら CAB を使わない。
/// </param>
internal readonly record struct MsiMedia(int LastSequence, string Cabinet);

/// <summary>
/// MSI を読む (#182)。表は Windows の <c>msi.dll</c> で読み、中身は CAB (<see cref="CabReader"/>) から取り出す。
/// 読み取りのみ。
/// </summary>
/// <remarks>
/// <para>
/// MSI はデータベースで、ファイルの名前・置き場所・大きさは表に入っている。中身は CAB に入っていて、
/// CAB の中では名前ではなく File 表の鍵で呼ばれている。表を読まないと、本当の名前と階層は分からない。
/// </para>
/// <para>
/// 置き場所は、Windows の管理用インストール (<c>msiexec /a</c>) が作るのと同じにする。
/// Windows 自身が MSI からファイルだけを取り出す方法で、フォルダーの名前は表の「元の置き場所」の名前を使う
/// (<c>ProgramFilesFolder</c> なら多くは <c>PFiles</c>)。
/// </para>
/// <para>
/// MSI を開いても、中の処理 (カスタムアクション) は動かさない。表を読むだけ。
/// </para>
/// </remarks>
internal static class MsiReader
{
    /// <summary>埋め込まれた CAB をメモリに受ける大きさの上限。これより大きいものは一時ファイルに受ける。</summary>
    private const long InMemoryLimit = 64 * 1024 * 1024;

    /// <summary>中身を一覧にする。</summary>
    /// <exception cref="InvalidDataException">MSI として読めない場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (files, _) = ReadLayout(path, cancellationToken);
        var builder = new ArchiveTreeBuilder(path);

        foreach (var file in files)
        {
            builder.AddFile(file.Path, file.Length, compressedLength: 0,
                compressedLengthKnown: false, lastWriteTime: default);
        }

        progress?.Report(new OpenProgress(files.Count, files.Count));

        return builder.Build(path, ArchiveFormat.Msi, totalCompressedLength: new FileInfo(path).Length);
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
        string? basePath = null)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress);

        try
        {
            Visit(archivePath, sourceNames, (file, time, open) =>
            {
                state.TotalBytes += file.Length;
                state.Write(file.Path, file.Length, time, open, cancellationToken);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 中断は ExtractState が数えている
        }

        return state.ToResult();
    }

    /// <summary>
    /// 中身を 1 件ずつ、入れ物ごとに渡す。取り出しと検査で共通に使う。
    /// </summary>
    /// <param name="sourceNames">渡すファイルのパス。<see langword="null"/> ならすべて。</param>
    /// <param name="visit">
    /// 1 件ごとに呼ぶ。2 番目は更新日時 (分からなければ <see langword="null"/>)、3 番目は中身を読む流れを開く。
    /// 読めなかったものは、開くと例外を投げる流れで渡す。1 件の失敗で全体を止めないため。
    /// </param>
    public static void Visit(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        Action<MsiFile, DateTime?, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        var (files, media) = ReadLayout(archivePath, cancellationToken);
        var folder = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? string.Empty;

        var targets = files
            .Where(f => sourceNames is null || sourceNames.Contains(f.Path))
            .ToList();

        // 圧縮されていないファイルは、MSI の隣の元の置き場所から読む
        foreach (var file in targets.Where(static f => !f.Compressed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visit(file, null, () => OpenBeside(folder, file.SourcePath));
        }

        // CAB に入っているものは、入れ物ごとにまとめて取り出す
        foreach (var group in targets.Where(static f => f.Compressed).GroupBy(f => MediaOf(media, f.Sequence)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var byKey = new Dictionary<string, MsiFile>(StringComparer.Ordinal);
            foreach (var file in group)
            {
                byKey.TryAdd(file.Key, file);
            }

            var done = new HashSet<string>(StringComparer.Ordinal);
            Exception? failure = null;

            try
            {
                if (group.Key is not { } cabinet || cabinet.Length == 0)
                {
                    throw new InvalidDataException();
                }

                ReadCabinet(archivePath, folder, cabinet, byKey.Keys.ToHashSet(StringComparer.Ordinal), (entry, open) =>
                {
                    if (byKey.TryGetValue(entry.Name, out var file) && done.Add(entry.Name))
                    {
                        visit(file, entry.LastWriteTime == default ? null : entry.LastWriteTime, open);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or UnauthorizedAccessException)
            {
                failure = ex;
            }

            // CAB に見つからなかったもの、CAB ごと読めなかったものも、失敗として数えさせる
            foreach (var (key, file) in byKey)
            {
                if (!done.Contains(key))
                {
                    var reason = failure ?? new InvalidDataException();
                    visit(file, null, () => throw reason);
                }
            }
        }
    }

    /// <summary>ファイルの順番から、入っている入れ物の CAB の名前を引く。</summary>
    private static string? MediaOf(IReadOnlyList<MsiMedia> media, int sequence)
    {
        foreach (var m in media)
        {
            if (sequence <= m.LastSequence)
            {
                return m.Cabinet;
            }
        }

        return null;
    }

    /// <summary>MSI の隣に置かれたファイルを開く。MSI のフォルダーの外は読まない。</summary>
    private static FileStream OpenBeside(string folder, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', '\\')));

        if (!ArchiveExtractor.IsInside(folder, full))
        {
            throw new InvalidDataException();
        }

        return File.OpenRead(full);
    }

    /// <summary>入れ物の CAB を読む。<c>#</c> で始まれば MSI に埋め込まれたもの、そうでなければ隣のファイル。</summary>
    private static void ReadCabinet(
        string archivePath,
        string folder,
        string cabinet,
        IReadOnlySet<string> keys,
        Action<CabEntry, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        if (!cabinet.StartsWith('#'))
        {
            var beside = CabReader.SiblingOf(folder, cabinet) ?? throw new InvalidDataException();
            if (!File.Exists(beside))
            {
                throw new FileNotFoundException($"'{beside}'");
            }

            CabReader.Visit(() => File.OpenRead(beside), folder, keys, visit, cancellationToken);
            return;
        }

        // 埋め込まれた CAB は、いったん受けてから読ませる。cabinet.dll は同じ CAB を頭から読み直すことがある
        using var copy = ReadStream(archivePath, cabinet[1..], cancellationToken);
        CabReader.Visit(() => copy.OpenView(), siblingFolder: null, keys, visit, cancellationToken);
    }

    /// <summary>MSI に埋め込まれた流れ (_Streams 表) を受ける。</summary>
    /// <exception cref="InvalidDataException">その名前の流れが無い場合 (インストール後に控えられた MSI など)。</exception>
    private static Received ReadStream(string archivePath, string name, CancellationToken cancellationToken)
    {
        using var database = Native.OpenDatabase(archivePath);
        using var view = database.Query("SELECT `Data` FROM `_Streams` WHERE `Name` = ?", name);
        using var record = view.Fetch() ?? throw new InvalidDataException();

        var size = record.DataSize(1);
        var received = new Received(size <= InMemoryLimit ? null : Path.Combine(
            Path.GetTempPath(), $"Expzip-{Guid.NewGuid():N}.cab"));

        try
        {
            var buffer = new byte[64 * 1024];
            int got;
            while ((got = record.ReadStream(1, buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                received.Write(buffer, got);
            }

            received.Finish();
            return received;
        }
        catch
        {
            received.Dispose();
            throw;
        }
    }

    /// <summary>受けた CAB。小さければメモリ、大きければ一時ファイル。捨てるときに一時ファイルも消す。</summary>
    private sealed class Received(string? tempPath) : IDisposable
    {
        private readonly MemoryStream? _memory = tempPath is null ? new MemoryStream() : null;
        private FileStream? _file = tempPath is null ? null
            : new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        public void Write(byte[] buffer, int count)
        {
            if (_memory is not null)
            {
                _memory.Write(buffer, 0, count);
            }
            else
            {
                _file!.Write(buffer, 0, count);
            }
        }

        public void Finish()
        {
            _file?.Dispose();
            _file = null;
        }

        /// <summary>頭から読める、新しい流れを返す。</summary>
        public Stream OpenView()
            => _memory is not null
                ? new MemoryStream(_memory.GetBuffer(), 0, (int)_memory.Length, writable: false)
                : new FileStream(tempPath!, FileMode.Open, FileAccess.Read, FileShare.Read);

        public void Dispose()
        {
            _file?.Dispose();
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 消せなくても読み取りは済んでいる。一時フォルダーの掃除に任せる
                }
            }
        }
    }

    /// <summary>表を読んで、ファイルと入れ物の一覧を作る。</summary>
    /// <exception cref="InvalidDataException">MSI として読めない場合。</exception>
    public static (IReadOnlyList<MsiFile> Files, IReadOnlyList<MsiMedia> Media) ReadLayout(
        string path, CancellationToken cancellationToken = default)
    {
        using var database = Native.OpenDatabase(path);

        // 要約情報の「語数」の旗。1: 元の置き場所は短い名前、2: 既定で圧縮 (CAB に入っている)
        var flags = database.WordCount();
        var shortNames = (flags & 1) != 0;
        var compressedByDefault = (flags & 2) != 0;

        var directories = new Dictionary<string, (string? Parent, string DefaultDir)>(StringComparer.Ordinal);
        if (database.HasTable("Directory"))
        {
            using var view = database.Query("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`");
            while (view.Fetch() is { } record)
            {
                using (record)
                {
                    directories[record.GetString(1)] = (record.IsNull(2) ? null : record.GetString(2), record.GetString(3));
                }
            }
        }

        var components = new Dictionary<string, string>(StringComparer.Ordinal);
        if (database.HasTable("Component"))
        {
            using var view = database.Query("SELECT `Component`, `Directory_` FROM `Component`");
            while (view.Fetch() is { } record)
            {
                using (record)
                {
                    components[record.GetString(1)] = record.GetString(2);
                }
            }
        }

        var media = new List<MsiMedia>();
        if (database.HasTable("Media"))
        {
            using var view = database.Query("SELECT `LastSequence`, `Cabinet` FROM `Media` ORDER BY `LastSequence`");
            while (view.Fetch() is { } record)
            {
                using (record)
                {
                    media.Add(new MsiMedia(record.GetInteger(1), record.IsNull(2) ? string.Empty : record.GetString(2)));
                }
            }
        }

        var longPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var shortPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        var files = new List<MsiFile>();
        if (database.HasTable("File"))
        {
            using var view = database.Query(
                "SELECT `File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence` FROM `File`");

            while (view.Fetch() is { } record)
            {
                using (record)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var key = record.GetString(1);
                    var directory = components.GetValueOrDefault(record.GetString(2), string.Empty);
                    var name = record.GetString(3);
                    var attributes = record.IsNull(5) ? 0 : record.GetInteger(5);

                    // 1 ファイルごとの指定 (0x2000 は圧縮しない、0x4000 は圧縮する) が、全体の旗より先
                    var compressed = (attributes & 0x2000) != 0 ? false
                        : (attributes & 0x4000) != 0 || compressedByDefault;

                    var inside = Join(DirectoryPath(directories, directory, longPaths, useShort: false), LongName(name));
                    var source = Join(DirectoryPath(directories, directory, shortPaths, useShort: shortNames),
                        shortNames ? ShortName(name) : LongName(name));

                    files.Add(new MsiFile(inside, key, source, Math.Max(0, record.GetInteger(4)),
                        record.IsNull(6) ? 0 : record.GetInteger(6), compressed));
                }
            }
        }

        return (files, media);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary><c>短い名前|長い名前</c> の形から、長い名前を取る。</summary>
    private static string LongName(string value)
    {
        var bar = value.IndexOf('|');
        return bar < 0 ? value : value[(bar + 1)..];
    }

    private static string ShortName(string value)
    {
        var bar = value.IndexOf('|');
        return bar < 0 ? value : value[..bar];
    }

    /// <summary>
    /// フォルダーの、元の置き場所でのパスを組み立てる。根 (親の無いフォルダー) は書庫の根になる。
    /// </summary>
    /// <remarks>
    /// DefaultDir は <c>置き先[:元の置き場所]</c> の形で、それぞれが <c>短い名前|長い名前</c>。
    /// 元の置き場所が無ければ置き先と同じ。<c>.</c> なら親と同じフォルダー。
    /// 壊れた表で親を辿り続けないよう、深さに上限を設ける。
    /// </remarks>
    private static string DirectoryPath(
        Dictionary<string, (string? Parent, string DefaultDir)> directories,
        string id,
        Dictionary<string, string> cache,
        bool useShort,
        int depth = 0)
    {
        if (cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (depth > 64 || !directories.TryGetValue(id, out var row) || row.Parent is null || row.Parent == id)
        {
            return cache[id] = string.Empty;
        }

        var value = row.DefaultDir;
        var colon = value.IndexOf(':');
        var source = colon < 0 ? value : value[(colon + 1)..];
        var name = useShort ? ShortName(source) : LongName(source);

        var parent = DirectoryPath(directories, row.Parent, cache, useShort, depth + 1);
        var path = name is "." or "" ? parent : Join(parent, name);
        return cache[id] = path;
    }

    /// <summary><c>msi.dll</c> の関数 (msiquery.h)。</summary>
    private static class Native
    {
        private const uint Success = 0;
        private const uint MoreData = 234;
        private const uint NoMoreItems = 259;
        private const int NullInteger = int.MinValue;

        public static Handle OpenDatabase(string path)
        {
            // 2 番目の引数の 0 は「読み取りのみ」(MSIDBOPEN_READONLY)
            return MsiOpenDatabaseW(path, IntPtr.Zero, out var handle) == Success
                ? new Handle(handle)
                : throw new InvalidDataException();
        }

        /// <summary>msi.dll の持ち物を閉じる包み。</summary>
        public sealed class Handle(IntPtr value) : IDisposable
        {
            public IntPtr Value { get; private set; } = value;

            public void Dispose()
            {
                if (Value != IntPtr.Zero)
                {
                    MsiCloseHandle(Value);
                    Value = IntPtr.Zero;
                }
            }

            public bool HasTable(string table) => MsiDatabaseIsTablePersistentW(Value, table) == 1;

            /// <summary>表を問い合わせる。<paramref name="parameter"/> は <c>?</c> に入る値。</summary>
            public Handle Query(string sql, string? parameter = null)
            {
                if (MsiDatabaseOpenViewW(Value, sql, out var view) != Success)
                {
                    throw new InvalidDataException();
                }

                var handle = new Handle(view);
                using var arguments = parameter is null ? null : new Handle(MsiCreateRecord(1));
                if (arguments is not null)
                {
                    MsiRecordSetStringW(arguments.Value, 1, parameter!);
                }

                if (MsiViewExecute(view, arguments?.Value ?? IntPtr.Zero) != Success)
                {
                    handle.Dispose();
                    throw new InvalidDataException();
                }

                return handle;
            }

            /// <summary>次の行。無ければ <see langword="null"/>。</summary>
            public Handle? Fetch()
            {
                var result = MsiViewFetch(Value, out var record);
                return result == Success ? new Handle(record)
                    : result == NoMoreItems ? null
                    : throw new InvalidDataException();
            }

            public bool IsNull(uint field) => MsiRecordIsNull(Value, field);

            public int GetInteger(uint field)
            {
                var value = MsiRecordGetInteger(Value, field);
                return value == NullInteger ? 0 : value;
            }

            public string GetString(uint field)
            {
                var buffer = new char[256];
                var length = (uint)buffer.Length;
                var result = MsiRecordGetStringW(Value, field, buffer, ref length);

                if (result == MoreData)
                {
                    buffer = new char[++length];
                    result = MsiRecordGetStringW(Value, field, buffer, ref length);
                }

                return result == Success ? new string(buffer, 0, (int)length) : throw new InvalidDataException();
            }

            public long DataSize(uint field) => MsiRecordDataSize(Value, field);

            public int ReadStream(uint field, byte[] buffer)
            {
                var length = (uint)buffer.Length;
                return MsiRecordReadStream(Value, field, buffer, ref length) == Success
                    ? (int)length
                    : throw new InvalidDataException();
            }

            /// <summary>要約情報の「語数」(PID_WORDCOUNT)。読めなければ 0。</summary>
            public int WordCount()
            {
                if (MsiGetSummaryInformationW(Value, null, 0, out var summary) != Success)
                {
                    return 0;
                }

                using var handle = new Handle(summary);
                var text = new char[1];
                var length = 0u;
                const uint wordCount = 15;
                return MsiSummaryInfoGetPropertyW(summary, wordCount, out _, out var value, IntPtr.Zero, text, ref length) == Success
                    ? value
                    : 0;
            }
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr database);

        [DllImport("msi.dll")]
        private static extern uint MsiCloseHandle(IntPtr handle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiDatabaseIsTablePersistentW(IntPtr database, string table);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

        [DllImport("msi.dll")]
        private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

        [DllImport("msi.dll")]
        private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

        [DllImport("msi.dll")]
        private static extern IntPtr MsiCreateRecord(uint fields);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiRecordSetStringW(IntPtr record, uint field, string value);

        [DllImport("msi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MsiRecordIsNull(IntPtr record, uint field);

        [DllImport("msi.dll")]
        private static extern int MsiRecordGetInteger(IntPtr record, uint field);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiRecordGetStringW(IntPtr record, uint field, char[] value, ref uint length);

        [DllImport("msi.dll")]
        private static extern uint MsiRecordDataSize(IntPtr record, uint field);

        [DllImport("msi.dll")]
        private static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[] buffer, ref uint length);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiGetSummaryInformationW(
            IntPtr database, string? path, uint updateCount, out IntPtr summary);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiSummaryInfoGetPropertyW(
            IntPtr summary, uint property, out uint type, out int value, IntPtr fileTime,
            char[] text, ref uint length);
    }
}
