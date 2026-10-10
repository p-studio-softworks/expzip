using System.Buffers.Binary;
using System.IO;
using Expzip.Inspection;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Expzip.Archives;

/// <summary>取り戻せなかったファイルの、取り戻せなかった訳 (#217)。</summary>
internal enum SalvageLoss
{
    /// <summary>中身の途中で書庫が切れている。</summary>
    Cut,

    /// <summary>中身を最後まで読めたが、CRC や大きさが合わない。</summary>
    Damaged,

    /// <summary>パスワード付き。合言葉なしでは中身を確かめられない。</summary>
    Encrypted,

    /// <summary>中身を確かめられない圧縮方式。</summary>
    UnsupportedMethod,

    /// <summary>大きさが後ろに書かれる形で、どこで終わるのか分からない。</summary>
    UnknownSize,
}

/// <summary>取り戻せなかったファイル 1 個。</summary>
/// <param name="Name">書庫の中の名前。</param>
/// <param name="Reason">取り戻せなかった訳。</param>
internal readonly record struct SalvageLost(string Name, SalvageLoss Reason);

/// <summary>書き直しの結果 (#217)。</summary>
/// <param name="Recovered">新しい書庫に入れたファイルの数。フォルダーは数えない。</param>
/// <param name="Lost">中身を見つけたが入れなかったもの。</param>
/// <param name="ReachedEnd">
/// 中央ディレクトリまでたどり着けたか。偽なら、途中で切れた所より後ろにあったものは名前も分からない。
/// </param>
internal sealed record SalvageResult(int Recovered, IReadOnlyList<SalvageLost> Lost, bool ReachedEnd);

/// <summary>
/// 末尾が欠けて開けない ZIP から、無事なファイルだけを集めて新しい書庫に書き直す (#217)。
/// </summary>
/// <remarks>
/// <para>
/// ZIP は、どのファイルがどこにあるかの一覧 (中央ディレクトリと終端レコード) を最後に置く。
/// ダウンロードやコピーが途中で切れると一覧ごと失われ、中身のほとんどが無事でも開けなくなる。
/// 各ファイルの頭にあるローカルヘッダを先頭から順にたどれば、切れた所より前のファイルは取り戻せる。
/// </para>
/// <para>
/// 取り戻したファイルは、ローカルヘッダと中身をバイトのまま写し、一覧を新しく作って後ろに付ける。
/// 展開し直さないので、名前の文字コードや日時、拡張フィールドがそのまま残る。
/// 入れる前に中身を展開して CRC と大きさを確かめ、合わないものは入れない。
/// </para>
/// <para>
/// 先頭がローカルヘッダで始まるものだけを扱う。自己解凍書庫は先頭にプログラムが付いていて、
/// 末尾が欠けると ZIP と見分けられないため、対象にしない。
/// </para>
/// </remarks>
internal static class ZipSalvage
{
    private const uint LocalSignature = 0x04034b50;
    private const uint DirectorySignature = 0x02014b50;
    private const uint DescriptorSignature = 0x08074b50;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;

    private const int LocalHeaderLength = 30;
    private const ushort Zip64ExtraId = 0x0001;

    private const ushort FlagEncrypted = 0x0001;
    private const ushort FlagDescriptor = 0x0008;

    private const ushort MethodStored = 0;
    private const ushort MethodDeflated = 8;

    /// <summary>ZIP64 を読み書きするのに要る仕様のバージョン (4.5)。</summary>
    private const ushort Zip64Version = 45;

    private const int BufferSize = 81920;

    /// <summary>
    /// 開けなかった書庫が、末尾の欠けた ZIP に見えるか (#217)。
    /// </summary>
    /// <remarks>
    /// 先頭がローカルヘッダで、終端レコードが見つからない (または一覧がファイルの外まで伸びている) ときだけ。
    /// 一覧はあるのにローカルヘッダと食い違うものは、細工された書庫の手口でもあるので提案しない。
    /// 分割された書庫 (#61) は繋いだものを読んでいるので、ここでは扱わない。
    /// </remarks>
    public static bool LooksTruncated(string path)
    {
        // 拡張子の分からないもの (.docx など中身が ZIP の文書) は ZIP として開いているので含める。
        // MSIX は署名ごと壊れているので、書き直しても入れられない
        if (ArchiveFormats.Detect(path) is not (ArchiveFormat.Zip or ArchiveFormat.Unknown)
            || SplitVolumes.IsFirstVolume(path))
        {
            return false;
        }

        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            Span<byte> head = stackalloc byte[4];
            if (stream.Length < LocalHeaderLength || stream.Read(head) != head.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(head) != LocalSignature)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var tail = ZipTail.Read(path);
        return !tail.Found || tail.CentralDirectoryTruncated;
    }

    /// <summary>
    /// 書き直した書庫の名前の案。元の書庫の隣に、まだ無い名前で作る。
    /// </summary>
    /// <param name="path">元の書庫。</param>
    /// <param name="label">名前に添える言葉 (「修復」)。</param>
    public static string SuggestPath(string path, string label)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? label : $"{label} {number}";
            var candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 無事なファイルだけで新しい書庫を作る。ファイルを 1 個も取り戻せなかったときは何も作らない。
    /// </summary>
    /// <param name="source">末尾の欠けた書庫。書き換えない。</param>
    /// <param name="destination">作る書庫。既にあれば失敗する。</param>
    /// <param name="progress">0〜100 の進み具合。</param>
    /// <param name="cancellationToken">中断用。中断したら作りかけの書庫は消す。</param>
    /// <exception cref="IOException">読めない、または書けない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static SalvageResult Salvage(
        string source, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var input = ArchiveFile.OpenRead(source);

        var (entries, lost, reachedEnd) = Scan(input, progress, cancellationToken);

        // フォルダーしか残っていなければ、作っても役に立たない
        var files = entries.Count(static e => !e.IsDirectory);

        if (files > 0)
        {
            try
            {
                Write(input, entries, destination, cancellationToken);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }

            // 出所の印は元の書庫から引き継ぐ (#12、#211)。ネットから落としたものを書き直しただけで
            // 印が消えると、取り出したファイルに SmartScreen などが働かなくなる
            MarkOfTheWeb.TryApply(destination, MarkOfTheWeb.TryRead(source));
        }

        progress?.Report(100);
        return new SalvageResult(files, lost, reachedEnd);
    }

    /// <summary>取り戻せると確かめたファイル 1 個の、元の書庫での位置と一覧に書く値。</summary>
    private sealed class Found
    {
        public required long Offset { get; init; }
        public required long Length { get; init; }
        public required ushort VersionNeeded { get; init; }
        public required ushort Flags { get; init; }
        public required ushort Method { get; init; }
        public required ushort Time { get; init; }
        public required ushort Date { get; init; }
        public required uint Crc { get; init; }
        public required long CompressedSize { get; init; }
        public required long Size { get; init; }
        public required byte[] Name { get; init; }
        public required byte[] Extra { get; init; }
        public required bool IsDirectory { get; init; }

        /// <summary>ローカルヘッダに ZIP64 拡張フィールドがあったか。</summary>
        public required bool Zip64 { get; init; }
    }

    /// <summary>ローカルヘッダを先頭から順にたどり、取り戻せるファイルを集める。</summary>
    private static (List<Found> Entries, List<SalvageLost> Lost, bool ReachedEnd) Scan(
        Stream input, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<Found>();
        var lost = new List<SalvageLost>();
        var length = input.Length;
        var header = new byte[LocalHeaderLength];
        var position = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(length == 0 ? 0 : position * 95.0 / length);

            if (position + 4 > length)
            {
                return (entries, lost, false);
            }

            input.Position = position;
            var available = (int)Math.Min(LocalHeaderLength, length - position);
            input.ReadExactly(header, 0, available);

            var signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (signature != LocalSignature)
            {
                // 一覧の始まりに着いたなら、ファイルはすべて見終えている
                return (entries, lost, signature is DirectorySignature or EndSignature or Zip64EndSignature);
            }

            if (available < LocalHeaderLength)
            {
                return (entries, lost, false);
            }

            var versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var time = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
            var date = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
            long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));

            var dataStart = position + LocalHeaderLength + nameLength + extraLength;
            if (dataStart > length)
            {
                // 名前の途中で切れている。何のファイルだったかも分からない
                return (entries, lost, false);
            }

            var name = new byte[nameLength];
            input.ReadExactly(name);
            var extra = new byte[extraLength];
            input.ReadExactly(extra);

            var shown = ZipArchiveReader.EntryNameEncoding.GetString(name);
            var isDirectory = shown.EndsWith('/') || shown.EndsWith('\\');

            var zip64 = ReadZip64Sizes(extra, ref size, ref compressedSize);
            var hasDescriptor = (flags & FlagDescriptor) != 0;

            // 大きさが後ろに書かれる形で、中身を展開しないと終わりが分からないもの
            var sizeUnknown = hasDescriptor && compressedSize == 0 && size == 0;

            if ((flags & FlagEncrypted) != 0)
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.Encrypted));
                if (sizeUnknown)
                {
                    return (entries, lost, false);
                }

                position = SkipDescriptor(input, dataStart + compressedSize, length, hasDescriptor, zip64);
                if (position < 0)
                {
                    return (entries, lost, false);
                }

                continue;
            }

            if (sizeUnknown && method != MethodDeflated)
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.UnknownSize));
                return (entries, lost, false);
            }

            if (method is not (MethodStored or MethodDeflated))
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.UnsupportedMethod));
                if (sizeUnknown)
                {
                    return (entries, lost, false);
                }

                position = SkipDescriptor(input, dataStart + compressedSize, length, hasDescriptor, zip64);
                if (position < 0)
                {
                    return (entries, lost, false);
                }

                continue;
            }

            if (!sizeUnknown && dataStart + compressedSize > length)
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.Cut));
                return (entries, lost, false);
            }

            input.Position = dataStart;
            var check = Verify(input, method, sizeUnknown ? -1 : compressedSize, length - dataStart, cancellationToken);

            if (check.Cut)
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.Cut));
                return (entries, lost, false);
            }

            if (check.Damaged)
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.Damaged));

                // 大きさが分かっていれば次のファイルへ進める。分からなければここで終わり
                if (sizeUnknown)
                {
                    return (entries, lost, false);
                }

                position = SkipDescriptor(input, dataStart + compressedSize, length, hasDescriptor, zip64);
                if (position < 0)
                {
                    return (entries, lost, false);
                }

                continue;
            }

            var dataEnd = dataStart + check.CompressedSize;
            var next = dataEnd;

            if (hasDescriptor)
            {
                var descriptor = ReadDescriptor(input, dataEnd, length, check.CompressedSize, check.Size);
                if (descriptor is null)
                {
                    // 中身は読めたが、後ろに書かれた CRC と大きさまで届いていない
                    lost.Add(new SalvageLost(shown, dataEnd + 12 > length ? SalvageLoss.Cut : SalvageLoss.Damaged));
                    return (entries, lost, false);
                }

                crc = descriptor.Value.Crc;
                next = dataEnd + descriptor.Value.Length;
            }

            // 大きさが後ろに書かれる形では、大きさは読み方を決めるときに照合済み
            if (check.Crc != crc || !sizeUnknown && check.Size != size)
            {
                lost.Add(new SalvageLost(shown, SalvageLoss.Damaged));
                position = next;
                continue;
            }

            entries.Add(new Found
            {
                Offset = position,
                Length = next - position,
                VersionNeeded = versionNeeded,
                Flags = flags,
                Method = method,
                Time = time,
                Date = date,
                Crc = crc,
                CompressedSize = check.CompressedSize,
                Size = check.Size,
                Name = name,
                Extra = extra,
                IsDirectory = isDirectory,
                Zip64 = zip64,
            });

            position = next;
        }
    }

    /// <summary>中身を展開して確かめた結果。</summary>
    private readonly record struct Check(bool Cut, bool Damaged, uint Crc, long CompressedSize, long Size);

    /// <summary>
    /// 中身を読んで CRC と大きさを求める。
    /// </summary>
    /// <param name="input">中身の始まりに位置を合わせた書庫。</param>
    /// <param name="method">圧縮方式。無圧縮か Deflate。</param>
    /// <param name="compressedSize">圧縮後の大きさ。分からないときは -1 で、Deflate の終わりまで読む。</param>
    /// <param name="remaining">書庫の残りの長さ。</param>
    private static Check Verify(
        Stream input, ushort method, long compressedSize, long remaining, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var crc = new Crc32();

        if (method == MethodStored)
        {
            var left = compressedSize;
            while (left > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0)
                {
                    return new Check(true, false, 0, 0, 0);
                }

                crc.Update(new ArraySegment<byte>(buffer, 0, read));
                left -= read;
            }

            return new Check(false, false, (uint)crc.Value, compressedSize, compressedSize);
        }

        // Deflate は展開して確かめる。SharpZipLib の Inflater は、どこまで入力を使ったかを
        // 正確に返すので、大きさが後ろに書かれる形でも中身の終わりが分かる
        var inflater = new Inflater(noHeader: true);
        var output = new byte[BufferSize];
        var limit = compressedSize < 0 ? remaining : Math.Min(compressedSize, remaining);
        var fed = 0L;
        var size = 0L;

        try
        {
            while (!inflater.IsFinished)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (inflater.IsNeedingInput)
                {
                    if (fed >= limit)
                    {
                        // 大きさのとおりに読んでも終わらない。書庫が切れているか、中身が壊れている
                        return compressedSize >= 0 && compressedSize <= remaining
                            ? new Check(false, true, 0, 0, 0)
                            : new Check(true, false, 0, 0, 0);
                    }

                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - fed));
                    if (read <= 0)
                    {
                        return new Check(true, false, 0, 0, 0);
                    }

                    fed += read;
                    inflater.SetInput(buffer, 0, read);
                }

                if (inflater.IsNeedingDictionary)
                {
                    return new Check(false, true, 0, 0, 0);
                }

                int produced;
                while ((produced = inflater.Inflate(output, 0, output.Length)) > 0)
                {
                    crc.Update(new ArraySegment<byte>(output, 0, produced));
                    size += produced;
                }
            }
        }
        catch (Exception ex) when (ex is ICSharpCode.SharpZipLib.SharpZipBaseException or InvalidOperationException)
        {
            return new Check(false, true, 0, 0, 0);
        }

        var used = inflater.TotalIn;
        if (compressedSize >= 0 && used != compressedSize)
        {
            return new Check(false, true, 0, 0, 0);
        }

        return new Check(false, false, (uint)crc.Value, used, size);
    }

    /// <summary>後ろに書かれた CRC と大きさ (データディスクリプタ)。</summary>
    private readonly record struct Descriptor(uint Crc, int Length);

    /// <summary>
    /// データディスクリプタを読む。署名は付くことも付かないこともあり、大きさは 4 バイトか 8 バイト。
    /// 展開して分かった大きさと合う読み方を探す。
    /// </summary>
    private static Descriptor? ReadDescriptor(Stream input, long at, long length, long compressedSize, long size)
    {
        var buffer = new byte[24];
        var available = (int)Math.Min(buffer.Length, Math.Max(0, length - at));
        input.Position = at;
        input.ReadExactly(buffer, 0, available);

        foreach (var signed in new[] { true, false })
        {
            var start = signed ? 4 : 0;
            if (signed && (available < 4 || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != DescriptorSignature))
            {
                continue;
            }

            if (start + 12 <= available
                && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start + 4)) == (uint)compressedSize
                && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start + 8)) == (uint)size
                && compressedSize <= uint.MaxValue && size <= uint.MaxValue)
            {
                return new Descriptor(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start)), start + 12);
            }

            if (start + 20 <= available
                && BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(start + 4)) == compressedSize
                && BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(start + 12)) == size)
            {
                return new Descriptor(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start)), start + 20);
            }
        }

        return null;
    }

    /// <summary>
    /// 入れないファイルを飛ばして、次のファイルの位置を返す。たどれないときは -1。
    /// </summary>
    private static long SkipDescriptor(Stream input, long dataEnd, long length, bool hasDescriptor, bool zip64)
    {
        if (dataEnd > length)
        {
            return -1;
        }

        if (!hasDescriptor)
        {
            return dataEnd;
        }

        // 中身を確かめていないので大きさで照合できない。署名の有無だけで読み方を決める
        var buffer = new byte[4];
        if (dataEnd + 4 > length)
        {
            return -1;
        }

        input.Position = dataEnd;
        input.ReadExactly(buffer);

        var signed = BinaryPrimitives.ReadUInt32LittleEndian(buffer) == DescriptorSignature;
        var next = dataEnd + (signed ? 4 : 0) + (zip64 ? 20 : 12);

        return next > length ? -1 : next;
    }

    /// <summary>
    /// ローカルヘッダの ZIP64 拡張フィールドから本当の大きさを読む。
    /// </summary>
    /// <returns>ZIP64 拡張フィールドがあったか。</returns>
    private static bool ReadZip64Sizes(byte[] extra, ref long size, ref long compressedSize)
    {
        for (var at = 0; at + 4 <= extra.Length;)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at));
            var fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at + 2));
            var body = at + 4;

            if (body + fieldLength > extra.Length)
            {
                return false;
            }

            if (id == Zip64ExtraId)
            {
                // ローカルヘッダでは、元の大きさと圧縮後の大きさの両方を、この順で書く決まり
                var read = body;
                if (size == uint.MaxValue && read + 8 <= body + fieldLength)
                {
                    size = BinaryPrimitives.ReadInt64LittleEndian(extra.AsSpan(read));
                    read += 8;
                }
                else if (fieldLength >= 16)
                {
                    read += 8;
                }

                if (compressedSize == uint.MaxValue && read + 8 <= body + fieldLength)
                {
                    compressedSize = BinaryPrimitives.ReadInt64LittleEndian(extra.AsSpan(read));
                }

                return true;
            }

            at = body + fieldLength;
        }

        return false;
    }

    /// <summary>拡張フィールドから ZIP64 のものを除く。一覧には書き直したものを付けるため。</summary>
    private static byte[] WithoutZip64(byte[] extra)
    {
        using var kept = new MemoryStream();

        for (var at = 0; at + 4 <= extra.Length;)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at));
            var fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(at + 2));
            var end = at + 4 + fieldLength;

            if (end > extra.Length)
            {
                break;
            }

            if (id != Zip64ExtraId)
            {
                kept.Write(extra, at, end - at);
            }

            at = end;
        }

        return kept.ToArray();
    }

    /// <summary>取り戻したファイルを写し、一覧を新しく作って書庫にする。</summary>
    private static void Write(Stream input, List<Found> entries, string destination, CancellationToken cancellationToken)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var offsets = new long[entries.Count];
        var buffer = new byte[BufferSize];

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            offsets[i] = output.Position;

            input.Position = entry.Offset;
            var left = entry.Length;
            while (left > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                output.Write(buffer, 0, read);
                left -= read;
            }
        }

        var directoryStart = output.Position;
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                WriteDirectoryRecord(writer, entries[i], offsets[i]);
            }

            var directoryEnd = output.Position;
            WriteEnd(writer, entries.Count, directoryStart, directoryEnd - directoryStart);
        }

        output.Flush(flushToDisk: true);
    }

    /// <summary>中央ディレクトリの 1 件を書く。4 GB を超える値は ZIP64 拡張フィールドに書く。</summary>
    private static void WriteDirectoryRecord(BinaryWriter writer, Found entry, long offset)
    {
        // ローカルヘッダが ZIP64 なら、一覧も ZIP64 で書く。読む側は一覧を見て、
        // 後ろに書かれた大きさ (データディスクリプタ) が 8 バイトかどうかを決める
        var bigSize = entry.Zip64 || entry.Size >= uint.MaxValue;
        var bigCompressed = entry.Zip64 || entry.CompressedSize >= uint.MaxValue;
        var bigOffset = offset >= uint.MaxValue;
        var zip64Length = (bigSize ? 8 : 0) + (bigCompressed ? 8 : 0) + (bigOffset ? 8 : 0);

        var extra = WithoutZip64(entry.Extra);
        if (zip64Length > 0 && extra.Length + 4 + zip64Length > ushort.MaxValue)
        {
            // 入りきらないときは、元の拡張フィールドより ZIP64 を優先する。無いと読めない
            extra = [];
        }

        var version = zip64Length > 0 ? Math.Max(entry.VersionNeeded, Zip64Version) : entry.VersionNeeded;

        writer.Write(DirectorySignature);
        writer.Write((ushort)Math.Max(version, (ushort)20)); // 作った環境は MS-DOS (上位バイト 0)
        writer.Write(version);
        writer.Write(entry.Flags);
        writer.Write(entry.Method);
        writer.Write(entry.Time);
        writer.Write(entry.Date);
        writer.Write(entry.Crc);
        writer.Write(bigCompressed ? uint.MaxValue : (uint)entry.CompressedSize);
        writer.Write(bigSize ? uint.MaxValue : (uint)entry.Size);
        writer.Write((ushort)entry.Name.Length);
        writer.Write((ushort)(extra.Length + (zip64Length > 0 ? 4 + zip64Length : 0)));
        writer.Write((ushort)0); // コメント
        writer.Write((ushort)0); // 始まりのディスク
        writer.Write((ushort)0); // 内部属性
        writer.Write(entry.IsDirectory ? 0x10u : 0u); // 外部属性。フォルダーの印だけ付ける
        writer.Write(bigOffset ? uint.MaxValue : (uint)offset);
        writer.Write(entry.Name);

        if (zip64Length > 0)
        {
            writer.Write(Zip64ExtraId);
            writer.Write((ushort)zip64Length);
            if (bigSize)
            {
                writer.Write(entry.Size);
            }

            if (bigCompressed)
            {
                writer.Write(entry.CompressedSize);
            }

            if (bigOffset)
            {
                writer.Write(offset);
            }
        }

        writer.Write(extra);
    }

    /// <summary>終端レコードを書く。件数や位置が収まらないときは ZIP64 の終端レコードも書く。</summary>
    private static void WriteEnd(BinaryWriter writer, int count, long directoryStart, long directorySize)
    {
        var needsZip64 = count >= ushort.MaxValue
                         || directoryStart >= uint.MaxValue
                         || directorySize >= uint.MaxValue;

        if (needsZip64)
        {
            var zip64End = writer.BaseStream.Position;

            writer.Write(Zip64EndSignature);
            writer.Write(44L); // この後ろの長さ
            writer.Write(Zip64Version);
            writer.Write(Zip64Version);
            writer.Write(0u); // このディスクの番号
            writer.Write(0u); // 中央ディレクトリの始まりのディスク
            writer.Write((long)count);
            writer.Write((long)count);
            writer.Write(directorySize);
            writer.Write(directoryStart);

            writer.Write(Zip64LocatorSignature);
            writer.Write(0u);
            writer.Write(zip64End);
            writer.Write(1u); // ディスクの総数
        }

        writer.Write(EndSignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(needsZip64 ? ushort.MaxValue : (ushort)count);
        writer.Write(needsZip64 ? ushort.MaxValue : (ushort)count);
        writer.Write(needsZip64 ? uint.MaxValue : (uint)directorySize);
        writer.Write(needsZip64 ? uint.MaxValue : (uint)directoryStart);
        writer.Write((ushort)0); // コメント
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても、失敗したことは呼び出し側が知らせる
        }
    }
}
