using System.Buffers.Binary;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Expzip.Archives;

/// <summary>WiX Burn でまとめた exe の中の 1 ファイル。</summary>
/// <param name="Path">一覧に出す、書庫の中のパス。</param>
/// <param name="Container">入っている入れ物の番号。0 は画面の部品と目録の入れ物。</param>
/// <param name="SourceName">入れ物 (CAB) の中での名前 (<c>u0</c>、<c>a0</c> など)。</param>
/// <param name="Length">展開後の大きさ。</param>
internal readonly record struct BurnPayload(string Path, int Container, string SourceName, long Length);

/// <summary>
/// WiX Burn でまとめた exe を読む (#182)。読み取りのみ。
/// </summary>
/// <remarks>
/// <para>
/// WiX Burn は、複数のインストーラー (MSI、exe など) と、それらを入れる画面を 1 つの exe にまとめる仕組み。
/// Python、.NET、Visual C++ ランタイムなどのインストーラーがこの作り。
/// </para>
/// <para>
/// exe の <c>.wixburn</c> 区画に、入れ物 (CAB) の数と大きさが書いてある。入れ物は exe の後ろに付け足されている。
/// 最初の入れ物 (番号 0) には、画面の部品と、中身の一覧を書いた目録 (XML) が入っている。
/// 2 つ目からの入れ物に、インストーラー本体が入っている。入れ物の中では名前ではなく
/// <c>u0</c>、<c>a0</c> のような記号で呼ばれていて、本当の名前は目録に書いてある。
/// </para>
/// <para>
/// 最初の入れ物は元の exe の直後にある。2 つ目からは、元の exe に電子署名があればその後ろ、
/// 無ければ最初の入れ物の直後に並ぶ (実物で確認)。インストール後に控えられた exe は、
/// 2 つ目からの入れ物が抜かれている。
/// </para>
/// <para>
/// 目録で「ダウンロードする」とされているもの (Packaging="external") は exe の中に無いので出さない。
/// </para>
/// </remarks>
internal static class BurnReader
{
    /// <summary>画面の部品を並べるフォルダーの名前。WiX での呼び名。</summary>
    public const string UxFolder = "UX";

    /// <summary>目録を並べるときの名前。目録の一番外の要素の名前。</summary>
    private const string ManifestName = "BurnManifest.xml";

    /// <summary><c>.wixburn</c> 区画のしるし。</summary>
    private const uint Magic = 0x00F14300;

    /// <summary>WiX Burn でまとめた exe か。</summary>
    public static bool IsBurn(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            return ReadContainers(stream) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>入れ物の位置と大きさを読む。WiX Burn でなければ <see langword="null"/>。</summary>
    private static (long Offset, long Length)[]? ReadContainers(Stream stream)
    {
        if (PeReader.FindSection(stream, ".wixburn") is not { } section || section.Length < 48)
        {
            return null;
        }

        var head = new byte[Math.Min(section.Length, 4096)];
        stream.Position = section.Offset;
        stream.ReadExactly(head);

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic)
        {
            return null;
        }

        // しるし、版、束の ID (16 バイト) の後に、元の exe の大きさ、元の照合値、
        // 元の署名の位置と大きさ、形式 (1 は CAB)、入れ物の数、入れ物ごとの大きさ
        var stub = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(24));
        var signatureOffset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32));
        var signatureSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(36));
        var format = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(40));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(44));

        if (format != 1 || count == 0 || count > 1000 || head.Length < 48 + count * 4)
        {
            return null;
        }

        var containers = new (long, long)[count];
        long next = stub;

        for (var i = 0; i < count; i++)
        {
            var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(48 + i * 4));

            // 2 つ目からは、元の exe の署名の後ろに並ぶ
            if (i == 1 && signatureSize > 0)
            {
                next = (long)signatureOffset + signatureSize;
            }

            containers[i] = (next, length);
            next += length;
        }

        return containers;
    }

    /// <summary>中身を一覧にする。</summary>
    /// <exception cref="InvalidDataException">WiX Burn として読めない場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var payloads = ReadLayout(path, cancellationToken);
        var builder = new ArchiveTreeBuilder(path);

        foreach (var payload in payloads)
        {
            builder.AddFile(payload.Path, payload.Length, compressedLength: 0,
                compressedLengthKnown: false, lastWriteTime: default);
        }

        progress?.Report(new OpenProgress(payloads.Count, payloads.Count));

        return builder.Build(path, ArchiveFormat.Burn, totalCompressedLength: new FileInfo(path).Length);
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
            Visit(archivePath, sourceNames, (payload, time, open) =>
            {
                state.TotalBytes += payload.Length;
                state.Write(payload.Path, payload.Length, time, open, cancellationToken);
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
    /// 読めなかったものは、開くと例外を投げる流れで渡す (MSI と同じ)。
    /// </summary>
    public static void Visit(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        Action<BurnPayload, DateTime?, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        (long Offset, long Length)[] containers;
        long fileLength;
        using (var stream = ArchiveFile.OpenRead(archivePath))
        {
            containers = ReadContainers(stream) ?? throw new InvalidDataException();
            fileLength = stream.Length;
        }

        var payloads = ReadLayout(archivePath, cancellationToken)
            .Where(p => sourceNames is null || sourceNames.Contains(p.Path));

        foreach (var group in payloads.GroupBy(static p => p.Container))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bySource = group.ToDictionary(static p => p.SourceName, StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);
            Exception? failure = null;

            try
            {
                var (offset, length) = containers[group.Key];

                // インストール後に控えられた exe は、2 つ目からの入れ物が抜かれている
                if (offset + length > fileLength)
                {
                    throw new ContentsRemovedException();
                }

                CabReader.Visit(() => new OffsetStream(ArchiveFile.OpenRead(archivePath), offset),
                    siblingFolder: null, bySource.Keys.ToHashSet(StringComparer.Ordinal), (entry, open) =>
                    {
                        if (bySource.TryGetValue(entry.Name, out var payload) && done.Add(entry.Name))
                        {
                            visit(payload, entry.LastWriteTime == default ? null : entry.LastWriteTime, open);
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

            foreach (var (source, payload) in bySource)
            {
                if (!done.Contains(source))
                {
                    var reason = failure ?? new InvalidDataException();
                    visit(payload, null, () => throw reason);
                }
            }
        }
    }

    /// <summary>目録を読んで、並べるものの一覧を作る。</summary>
    /// <exception cref="InvalidDataException">WiX Burn として読めない場合。</exception>
    public static IReadOnlyList<BurnPayload> ReadLayout(string path, CancellationToken cancellationToken = default)
    {
        long uxOffset;
        using (var stream = ArchiveFile.OpenRead(path))
        {
            var containers = ReadContainers(stream) ?? throw new InvalidDataException();
            uxOffset = containers[0].Offset;
        }

        // 最初の入れ物を読み、目録 (名前は 0) と、画面の部品の大きさを受ける
        byte[]? manifest = null;
        var uxSizes = new Dictionary<string, long>(StringComparer.Ordinal);

        CabReader.Visit(() => new OffsetStream(ArchiveFile.OpenRead(path), uxOffset), siblingFolder: null,
            sourceNames: null, (entry, open) =>
            {
                uxSizes[entry.Name] = entry.Length;
                if (entry.Name == "0")
                {
                    using var content = open();
                    using var copy = new MemoryStream();
                    content.CopyTo(copy);
                    manifest = copy.ToArray();
                }
            }, cancellationToken);

        if (manifest is null)
        {
            throw new InvalidDataException();
        }

        XDocument document;
        try
        {
            // 外の定義 (DTD) は読まない。既定の設定がそうなっている
            using var reader = XmlReader.Create(new MemoryStream(manifest),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        var root = document.Root ?? throw new InvalidDataException();
        var payloads = new List<BurnPayload>
        {
            new($"{UxFolder}/{ManifestName}", 0, "0", manifest.Length),
        };

        // 画面の部品 (UX の下の Payload)
        foreach (var payload in Children(Children(root, "UX").FirstOrDefault(), "Payload"))
        {
            var source = (string?)payload.Attribute("SourcePath");
            var file = (string?)payload.Attribute("FilePath");
            if (source is null || file is null || !uxSizes.TryGetValue(source, out var length))
            {
                continue;
            }

            payloads.Add(new BurnPayload($"{UxFolder}/{Normalize(file)}", 0, source, length));
        }

        // インストーラー本体。入れ物の ID から番号を引く
        var indexes = Children(root, "Container")
            .Select(c => ((string?)c.Attribute("Id"), (string?)c.Attribute("AttachedIndex")))
            .Where(static c => c.Item1 is not null && int.TryParse(c.Item2, out _))
            .ToDictionary(static c => c.Item1!, static c => int.Parse(c.Item2!), StringComparer.Ordinal);

        // 同じ中身を別の ID で指すことがある (すべての利用者向けと自分だけ向け、など)。1 つにまとめる
        var seen = new HashSet<(int, string)>();

        foreach (var payload in Children(root, "Payload"))
        {
            var packaging = (string?)payload.Attribute("Packaging");
            var container = (string?)payload.Attribute("Container");
            var source = (string?)payload.Attribute("SourcePath");
            var file = (string?)payload.Attribute("FilePath");

            if (packaging != "embedded" || container is null || source is null || file is null
                || !indexes.TryGetValue(container, out var index) || index <= 0
                || !seen.Add((index, source)))
            {
                continue;
            }

            _ = long.TryParse((string?)payload.Attribute("FileSize"), out var length);
            payloads.Add(new BurnPayload(Normalize(file), index, source, length));
        }

        return payloads;
    }

    /// <summary>名前空間を問わず、名前で子の要素を探す。WiX の版で名前空間が違う。</summary>
    private static IEnumerable<XElement> Children(XElement? parent, string name)
        => parent?.Elements().Where(e => e.Name.LocalName == name) ?? [];

    private static string Normalize(string path) => path.Replace('\\', '/');
}

/// <summary>
/// インストーラーの中身が抜かれている (#182)。インストール後に Windows が控えた exe や MSI。
/// </summary>
/// <remarks>壊れているのではないので、<see cref="InvalidDataException"/> とは分ける。</remarks>
internal sealed class ContentsRemovedException() : IOException(Localization.Strings.ContentsRemoved);
