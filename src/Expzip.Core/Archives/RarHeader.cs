using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Expzip.Archives;

/// <summary>RAR の項目のリンクの種類 (#336)。</summary>
internal enum RarLinkKind
{
    /// <summary>リンクではない。</summary>
    None,

    /// <summary>RAR5 の Unix のシンボリックリンク。</summary>
    UnixSymbolicLink,

    /// <summary>RAR5 の Windows のシンボリックリンク。</summary>
    WindowsSymbolicLink,

    /// <summary>RAR5 のジャンクション。</summary>
    Junction,

    /// <summary>RAR5 のハードリンク。指す先は同じ書庫の中のファイル。</summary>
    HardLink,

    /// <summary>RAR5 のファイルのコピー。同じ中身を 1 回だけ入れたもの。指す先は同じ書庫の中のファイル。</summary>
    FileCopy,

    /// <summary>RAR4 の Unix のシンボリックリンク。指す先は項目の中身 (伸長したデータ) にある。</summary>
    LegacyUnixSymbolicLink,
}

/// <summary>見出しから読んだ 1 個の項目 (#336)。分割で次の分割ファイルへ続くものは、最初の部分だけを数える。</summary>
/// <param name="Name">名前。RAR5 だけ読む (UTF-8)。RAR4 は SharpCompress の読んだ名前を使うので <see langword="null"/>。</param>
/// <param name="IsDirectory">フォルダーか。</param>
/// <param name="Link">リンクの種類。</param>
/// <param name="LinkTarget">指す先 (RAR5 のリダイレクト)。区切りは書庫に入っているまま。</param>
/// <param name="LinkToFolder">フォルダーを指すか (RAR5 のリダイレクトの印)。</param>
/// <param name="Encrypted">中身が暗号化されているか。</param>
/// <param name="SizeUnknown">展開後の大きさが書かれていないか (標準入力から作った書庫など)。SharpCompress はとても大きな値を返す。</param>
internal sealed record RarItem(
    string? Name, bool IsDirectory, RarLinkKind Link, string? LinkTarget, bool LinkToFolder, bool Encrypted,
    bool SizeUnknown = false);

/// <summary>RAR の見出しを先に読んだ結果 (#336)。</summary>
internal sealed class RarLayout
{
    /// <summary>RAR5 か。偽なら RAR 1.5〜4。</summary>
    public bool IsRar5 { get; init; }

    /// <summary>まとめて圧縮した (ソリッド) 書庫か。</summary>
    public bool IsSolid { get; init; }

    /// <summary>分割ファイル。最初のものから順に。</summary>
    public required IReadOnlyList<string> Volumes { get; init; }

    /// <summary>自己解凍の RAR のとき、本体が始まる位置。ふつうの RAR では 0。</summary>
    public long Offset { get; init; }

    /// <summary>項目。見出しの順。</summary>
    public required IReadOnlyList<RarItem> Items { get; init; }

    /// <summary>項目の中でいちばん大きい辞書の大きさ。</summary>
    public long MaxDictionary { get; init; }

    /// <summary>圧縮していない書庫のコメント。無いか、圧縮していて読めなければ <see langword="null"/>。</summary>
    public byte[]? Comment { get; init; }

    /// <summary>コメントが UTF-8 か (RAR5)。偽なら名前と同じ読み方で読む (RAR4)。</summary>
    public bool CommentIsUtf8 { get; init; }
}

/// <summary>RAR の見出しを、SharpCompress に渡す前に自分で読む (#336)。</summary>
/// <remarks>
/// <para>
/// SharpCompress の RAR を読む処理は、見出しに書かれた辞書の大きさをそのまま確保し (RAR5 は最大 4 GB)、
/// 分割の途中が欠けていても黙ってそこで読むのをやめる。リンクの種類も外に見せない。受け取った書庫を開いただけで
/// メモリーを使い切ったり、欠けた中身を正しいものとして見せたりしないよう、先に次を確かめる (#294 の 7z と同じ考え方)。
/// </para>
/// <list type="bullet">
/// <item><description>項目の数 (一覧に使うメモリー)</description></item>
/// <item><description>辞書の大きさ (すべての分割ファイルのすべての項目)</description></item>
/// <item><description>名前まで暗号化した書庫か</description></item>
/// <item><description>分割の最後まで揃っているか</description></item>
/// <item><description>リンクの種類と指す先、フォルダーを指すか</description></item>
/// </list>
/// <para>
/// 読むのは見出しだけで、中身は読み飛ばす。見出しの CRC が合わないもの、長さが書庫を超えるものは壊れているとする。
/// </para>
/// </remarks>
internal static class RarHeader
{
    /// <summary>RAR5 のしるし。</summary>
    private static ReadOnlySpan<byte> Rar5Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    /// <summary>RAR 1.5〜4 のしるし。</summary>
    private static ReadOnlySpan<byte> Rar4Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>自己解凍の RAR で、しるしを探す範囲。7z と同じ。</summary>
    private const int SearchLimit = 4 * 1024 * 1024;

    /// <summary>RAR5 の見出し 1 個の大きさの上限 (RAR5 の決まりでは 2 MB)。</summary>
    private const int MaxRar5HeaderSize = 2 * 1024 * 1024;

    /// <summary>
    /// 自己解凍の RAR で、本体の始まりかを確かめる見出しの長さの上限。RAR4 の見出しはもともと 64 KB まで。
    /// RAR5 の最初の見出し (書庫の見出し) もふつうは数十バイトで、これを超えるものは本体の始まりと見ない。
    /// </summary>
    private const int MaxProbeHeaderSize = 64 * 1024 + 16;

    /// <summary>自己解凍の RAR で、しるしの並びを確かめる数の上限。本物の自己解凍書庫の頭にこれほど並ぶことは無い。</summary>
    private const int MaxSignatureCandidates = 1024;

    /// <summary>たどる分割ファイルの数の上限。</summary>
    private const int MaxVolumes = 65536;

    /// <summary>
    /// 辞書の大きさの上限 (#336 で決めた)。1 GB、かつ PC のメモリーの 4 分の 1 まで。
    /// </summary>
    public static long DictionaryLimit
        => Math.Min(1L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);

    /// <summary>RAR のしるしで始まるか。読む位置は変えない。</summary>
    public static bool StartsWithSignature(Stream stream)
    {
        Span<byte> head = stackalloc byte[8];
        var start = stream.Position;
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = start;
        return (read >= 8 && head.SequenceEqual(Rar5Signature)) || (read >= 7 && head[..7].SequenceEqual(Rar4Signature));
    }

    /// <summary>
    /// 自己解凍の RAR (exe) の中で、RAR の本体が始まる位置。見つからなければ -1。
    /// </summary>
    /// <remarks>しるしの後ろの最初の見出しの CRC が合うものだけを数える。プログラムの中にたまたま同じ並びがあることがある。</remarks>
    public static long FindOffset(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            var window = (int)Math.Min(stream.Length, SearchLimit);
            var buffer = new byte[window];
            stream.ReadExactly(buffer);

            // しるしは 7 バイトの並びで、共通の頭は 6 バイト ("Rar!\x1A\x07")。頭を IndexOf でまとめて探す
            var candidates = 0;
            for (var at = 0; at + 7 <= window;)
            {
                var found = buffer.AsSpan(at, window - at).IndexOf(Rar4Signature[..6]);
                if (found < 0)
                {
                    break;
                }

                at += found;
                var span = buffer.AsSpan(at, window - at);
                var rar5 = span.Length >= 8 && span[..8].SequenceEqual(Rar5Signature);
                var rar4 = !rar5 && span.Length >= 7 && span[..7].SequenceEqual(Rar4Signature);
                if (rar5 || rar4)
                {
                    // 細工した exe でしるしを並べられても時間がかからないよう、確かめる数に上限を付ける
                    if (++candidates > MaxSignatureCandidates)
                    {
                        break;
                    }

                    if (FirstHeaderIsValid(buffer, at + (rar5 ? 8 : 7), window, rar5))
                    {
                        return at;
                    }
                }

                at++;
            }

            return -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return -1;
        }
    }

    /// <summary>
    /// しるしの直後の見出しが正しいか。読み込み済みの頭の部分だけで確かめ、見出しは <see cref="MaxProbeHeaderSize"/> までしか読まない
    /// (しるしのたびにファイルを読み直したり、大きな見出しの CRC を計算し直したりしない)。
    /// </summary>
    private static bool FirstHeaderIsValid(byte[] buffer, int position, int window, bool rar5)
    {
        try
        {
            using var stream = new MemoryStream(
                buffer, position, Math.Min(window - position, MaxProbeHeaderSize), writable: false);
            var reader = new HeaderReader(stream, CancellationToken.None);
            // RAR4 は、しるしの直後が書庫の見出し (0x73) で、その CRC が合うものだけ。CRC を確かめない種類の見出しでは、
            // たまたま同じ並びを含むだけの exe を RAR と見てしまう
            return rar5
                ? reader.TryReadRar5Header() is not null
                : reader.TryReadRar4Header() is { Type: 0x73 };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>
    /// 見出しを読んで確かめる。
    /// </summary>
    /// <param name="firstVolume">最初の分割ファイル (分割していなければ書庫そのもの)。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="DamagedDataException">見出しが壊れている場合。</exception>
    /// <exception cref="ArchiveTooLargeException">項目が多すぎる、辞書が大きすぎる場合。</exception>
    /// <exception cref="EncryptedHeaderException">名前まで暗号化した書庫の場合。</exception>
    /// <exception cref="MissingVolumeException">分割の途中が欠けている場合。</exception>
    public static RarLayout Read(string firstVolume, CancellationToken cancellationToken)
    {
        var volumes = new List<string> { firstVolume };
        var items = new List<RarItem>();
        long maxDictionary = 0;
        long nameBytes = 0;
        byte[]? comment = null;
        var rar5 = false;
        var solid = false;
        var offset = 0L;

        for (var index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volume = volumes[index];

            using var stream = ArchiveFile.OpenRead(volume);
            if (index == 0 && !StartsWithSignature(stream))
            {
                // 自己解凍の RAR。分割した自己解凍の RAR は扱わない (続きの名前の決まりが分からない)
                offset = FindOffset(volume);
                if (offset < 0)
                {
                    throw new DamagedDataException(new InvalidDataException("RAR signature not found."));
                }
            }

            stream.Position = index == 0 ? offset : 0;
            var reader = new HeaderReader(stream, cancellationToken);
            var result = reader.ReadVolume(items, ref maxDictionary, ref nameBytes, ref comment);

            if (index == 0)
            {
                rar5 = result.IsRar5;
                solid = result.Solid;
            }
            else if (result.IsRar5 != rar5)
            {
                throw new DamagedDataException(new InvalidDataException("RAR volumes differ in version."));
            }

            if (result.EncryptedHeaders)
            {
                throw new EncryptedHeaderException(new InvalidDataException("RAR headers are encrypted."));
            }

            CheckBudget(items.Count, nameBytes);

            if (maxDictionary > DictionaryLimit)
            {
                throw new ArchiveTooLargeException(ArchiveLimit.Memory);
            }

            if (!result.MoreVolumes)
            {
                break;
            }

            if (offset > 0)
            {
                throw new UnsupportedRarException(UnsupportedRar.SplitSelfExtracting);
            }

            var next = RarVolumes.NextVolume(firstVolume, index + 1, result.IsRar5 || result.NewNaming);
            if (next is null)
            {
                // 続きの分割ファイルの名前の決まりに当たらない名前
                throw new UnsupportedRarException(UnsupportedRar.UnknownVolumeNaming);
            }

            if (!File.Exists(next))
            {
                throw new MissingVolumeException(next);
            }

            if (volumes.Count >= MaxVolumes)
            {
                throw new ArchiveTooLargeException(ArchiveLimit.Entries);
            }

            volumes.Add(next);
        }

        return new RarLayout
        {
            IsRar5 = rar5,
            IsSolid = solid,
            Volumes = volumes,
            Offset = offset,
            Items = items,
            MaxDictionary = maxDictionary,
            Comment = comment,
            CommentIsUtf8 = rar5,
        };
    }

    /// <summary>一覧に使うメモリーの見積もりが上限を超えていれば止める。</summary>
    private static void CheckBudget(int count, long nameBytes)
    {
        if ((long)count * ArchiveLimits.BytesPerEntry + nameBytes * 2 > ArchiveLimits.MemoryBudget)
        {
            throw new ArchiveTooLargeException(ArchiveLimit.Entries);
        }
    }

    /// <summary>1 個の分割ファイルを読んだ結果。</summary>
    private readonly record struct VolumeResult(bool IsRar5, bool Solid, bool EncryptedHeaders, bool MoreVolumes, bool NewNaming);

    /// <summary>見出しを順に読む。</summary>
    private sealed class HeaderReader(Stream stream, CancellationToken cancellationToken)
    {
        private readonly long _length = stream.Length;

        /// <summary>しるしから始まる 1 個の分割ファイルを読む。</summary>
        public VolumeResult ReadVolume(List<RarItem> items, ref long maxDictionary, ref long nameBytes, ref byte[]? comment)
        {
            Span<byte> head = stackalloc byte[8];
            stream.ReadExactly(head[..7]);
            if (head[..7].SequenceEqual(Rar4Signature))
            {
                return ReadRar4(items, ref maxDictionary, ref nameBytes, ref comment);
            }

            stream.ReadExactly(head[7..]);
            if (head.SequenceEqual(Rar5Signature))
            {
                return ReadRar5(items, ref maxDictionary, ref nameBytes, ref comment);
            }

            // RAR 1.4 以前 (しるし RE~^) は扱わない
            if (head[..4].SequenceEqual("RE~^"u8))
            {
                throw new UnsupportedRarException(UnsupportedRar.TooOld);
            }

            throw new DamagedDataException(new InvalidDataException("Unsupported RAR signature."));
        }

        // ---------------------------------------------------------------- RAR5

        /// <summary>RAR5 の見出し 1 個の生のデータ。種類から後ろ (CRC と長さは除く)。</summary>
        public sealed record Rar5Header(byte[] Data, int Type, ulong Flags, ulong DataSize, int Body, int ExtraStart, int ExtraLength);

        /// <summary>RAR5 の見出しを 1 個読む。終わりなら <see langword="null"/>。</summary>
        public Rar5Header? TryReadRar5Header()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Position >= _length)
            {
                return null;
            }

            Span<byte> crcBytes = stackalloc byte[4];
            stream.ReadExactly(crcBytes);
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);

            // 長さ (vint) は CRC の対象に入る
            var sizeStart = stream.Position;
            var size = ReadStreamVint();
            if (size == 0 || size > MaxRar5HeaderSize || stream.Position + (long)size > _length)
            {
                throw Damaged("RAR5 header size is invalid.");
            }

            var sizeLength = (int)(stream.Position - sizeStart);
            var all = new byte[sizeLength + (int)size];
            stream.Position = sizeStart;
            stream.ReadExactly(all);

            if (Crc(all) != expected)
            {
                throw Damaged("RAR5 header CRC mismatch.");
            }

            var data = all[sizeLength..];
            var at = 0;
            var type = (int)ReadVint(data, ref at);
            var flags = ReadVint(data, ref at);
            var extraLength = (flags & 0x1) != 0 ? ReadVint(data, ref at) : 0;
            var dataSize = (flags & 0x2) != 0 ? ReadVint(data, ref at) : 0;

            if (extraLength > (ulong)data.Length)
            {
                throw Damaged("RAR5 extra area is too long.");
            }

            var extraStart = data.Length - (int)extraLength;
            if (extraStart < at)
            {
                throw Damaged("RAR5 extra area overlaps the header.");
            }

            return new Rar5Header(data, type, flags, dataSize, at, extraStart, (int)extraLength);
        }

        private VolumeResult ReadRar5(List<RarItem> items, ref long maxDictionary, ref long nameBytes, ref byte[]? comment)
        {
            var solid = false;
            var more = false;
            var sawEnd = false;
            var lastSplitAfter = false;

            while (TryReadRar5Header() is { } header)
            {
                switch (header.Type)
                {
                    case 1: // 書庫の見出し
                    {
                        var at = header.Body;
                        var archiveFlags = ReadVint(header.Data, ref at);
                        solid = (archiveFlags & 0x4) != 0;
                        break;
                    }

                    case 2: // ファイル
                    case 3: // サービス (コメント、NTFS の代替データストリーム、アクセス権など)
                    {
                        var file = ParseRar5File(header);
                        if (header.Type == 3)
                        {
                            // 項目としては出さない (SharpCompress も出さない)。圧縮していないコメントだけを読む
                            if (file.Name == "CMT" && file.Method == 0 && !file.Encrypted && comment is null
                                && header.DataSize <= MaxCommentBytes)
                            {
                                comment = ReadData((int)header.DataSize);
                                continue;
                            }

                            SkipData(header.DataSize);
                            continue;
                        }

                        // 前の分割ファイルから続く部分は、最初の部分で数えてある
                        if (!file.SplitBefore)
                        {
                            items.Add(new RarItem(
                                file.Name, file.IsDirectory, file.Link, file.LinkTarget, file.LinkToFolder, file.Encrypted,
                                file.SizeUnknown));
                            nameBytes += file.Name.Length;
                        }

                        if (!file.IsDirectory)
                        {
                            maxDictionary = Math.Max(maxDictionary, file.Dictionary);
                        }

                        lastSplitAfter = file.SplitAfter;
                        SkipData(header.DataSize);
                        break;
                    }

                    case 4: // 暗号化の見出し。これより後ろは暗号化されている
                        return new VolumeResult(true, solid, EncryptedHeaders: true, MoreVolumes: false, NewNaming: true);

                    case 5: // 書庫の終わり
                    {
                        var at = header.Body;
                        more = (ReadVint(header.Data, ref at) & 0x1) != 0;
                        sawEnd = true;
                        break;
                    }

                    default:
                        SkipData(header.DataSize);
                        break;
                }

                if (sawEnd)
                {
                    break;
                }
            }

            return new VolumeResult(true, solid, false, more || (!sawEnd && lastSplitAfter), NewNaming: true);
        }

        /// <summary>RAR5 のファイルとサービスの見出しの中身。</summary>
        private readonly record struct Rar5File(
            string Name, bool IsDirectory, bool Encrypted, int Method, long Dictionary, bool SplitBefore, bool SplitAfter,
            RarLinkKind Link, string? LinkTarget, bool LinkToFolder, bool SizeUnknown);

        private static Rar5File ParseRar5File(Rar5Header header)
        {
            var data = header.Data;
            var at = header.Body;
            var fileFlags = ReadVint(data, ref at);
            ReadVint(data, ref at); // 展開後の大きさ
            ReadVint(data, ref at); // 属性
            if ((fileFlags & 0x2) != 0)
            {
                at += 4; // 更新日時
            }

            if ((fileFlags & 0x4) != 0)
            {
                at += 4; // CRC32
            }

            var compression = ReadVint(data, ref at);
            ReadVint(data, ref at); // 作った OS
            var nameLength = ReadVint(data, ref at);
            if (at < 0 || (ulong)at + nameLength > (ulong)header.ExtraStart)
            {
                throw Damaged("RAR5 file name is too long.");
            }

            var name = Encoding.UTF8.GetString(data, at, (int)nameLength);
            var isDirectory = (fileFlags & 0x1) != 0;

            // 辞書の大きさ: ビット 10〜14 で 128 KB の 2 の n 乗倍、ビット 15〜19 で 32 分の何倍かを足す (RAR 7)
            var power = (int)((compression >> 10) & 0x1F);
            var fraction = (long)((compression >> 15) & 0x1F);
            var dictionary = power >= 40 ? long.MaxValue : (128L * 1024) << power;
            if (dictionary != long.MaxValue)
            {
                dictionary += dictionary / 32 * fraction;
            }

            var method = (int)((compression >> 7) & 0x7);
            var encrypted = false;
            var link = RarLinkKind.None;
            string? target = null;
            var toFolder = false;

            // 拡張の並び: 大きさ (vint)、種類 (vint)、中身
            var extra = header.ExtraStart;
            var end = header.ExtraStart + header.ExtraLength;
            while (extra < end)
            {
                var recordSize = ReadVint(data, ref extra);
                var recordStart = extra;
                if (recordSize == 0 || (ulong)recordStart + recordSize > (ulong)end)
                {
                    throw Damaged("RAR5 extra record is invalid.");
                }

                var recordEnd = recordStart + (int)recordSize;
                var recordType = ReadVint(data, ref extra);
                switch (recordType)
                {
                    case 1: // 暗号化
                        encrypted = true;
                        break;

                    case 5: // リダイレクト (リンク)
                    {
                        var redirect = ReadVint(data, ref extra);
                        var redirectFlags = ReadVint(data, ref extra);
                        var targetLength = ReadVint(data, ref extra);
                        if ((ulong)extra + targetLength > (ulong)recordEnd)
                        {
                            throw Damaged("RAR5 link target is too long.");
                        }

                        target = Encoding.UTF8.GetString(data, extra, (int)targetLength);
                        toFolder = (redirectFlags & 0x1) != 0;
                        link = redirect switch
                        {
                            1 => RarLinkKind.UnixSymbolicLink,
                            2 => RarLinkKind.WindowsSymbolicLink,
                            3 => RarLinkKind.Junction,
                            4 => RarLinkKind.HardLink,
                            5 => RarLinkKind.FileCopy,
                            _ => throw Damaged("RAR5 link type is unknown."),
                        };
                        break;
                    }
                }

                extra = recordEnd;
            }

            return new Rar5File(
                name, isDirectory, encrypted, method, dictionary,
                SplitBefore: (header.Flags & 0x8) != 0, SplitAfter: (header.Flags & 0x10) != 0,
                link, target, toFolder, SizeUnknown: (fileFlags & 0x8) != 0);
        }

        // ---------------------------------------------------------------- RAR4

        /// <summary>RAR4 の見出し 1 個。</summary>
        public sealed record Rar4Header(int Type, int Flags, byte[] Data, long DataSize);

        /// <summary>RAR4 の見出しを 1 個読む。終わりなら <see langword="null"/>。</summary>
        public Rar4Header? TryReadRar4Header()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Position + 7 > _length)
            {
                return null;
            }

            Span<byte> fixedPart = stackalloc byte[7];
            stream.ReadExactly(fixedPart);
            var crc = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart);
            var type = fixedPart[2];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[3..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[5..]);

            if (size < 7 || stream.Position - 7 + size > _length)
            {
                throw Damaged("RAR4 header size is invalid.");
            }

            var rest = new byte[size - 7];
            stream.ReadExactly(rest);

            // CRC は種類から後ろ全体の下位 16 ビット。古い書庫の終わりの見出しなどは合わないことがあるので、
            // 書庫の見出し (自己解凍の RAR で本体の始まりを見分けるのに使う) とファイルの見出しだけ確かめる
            if (type is 0x73 or 0x74)
            {
                var all = new byte[size - 2];
                fixedPart[2..].CopyTo(all);
                rest.CopyTo(all, 5);
                if ((Crc(all) & 0xFFFF) != crc)
                {
                    throw Damaged("RAR4 header CRC mismatch.");
                }
            }

            long dataSize = 0;
            if (type == 0x74 || type == 0x7A)
            {
                if (rest.Length < 4)
                {
                    throw Damaged("RAR4 file header is too short.");
                }

                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(rest);
                if ((flags & 0x100) != 0 && rest.Length >= 29)
                {
                    dataSize |= (long)BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(25)) << 32;
                }
            }
            else if ((flags & 0x8000) != 0 && rest.Length >= 4)
            {
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            }

            return new Rar4Header(type, flags, rest, dataSize);
        }

        private VolumeResult ReadRar4(List<RarItem> items, ref long maxDictionary, ref long nameBytes, ref byte[]? comment)
        {
            var solid = false;
            var more = false;
            var sawEnd = false;
            var lastSplitAfter = false;
            var newNaming = false;

            while (TryReadRar4Header() is { } header)
            {
                switch (header.Type)
                {
                    case 0x73: // 書庫の見出し
                        solid = (header.Flags & 0x0008) != 0;
                        newNaming = (header.Flags & 0x0010) != 0;
                        if ((header.Flags & 0x0080) != 0)
                        {
                            return new VolumeResult(false, solid, EncryptedHeaders: true, MoreVolumes: false, newNaming);
                        }

                        break;

                    case 0x74: // ファイル
                    {
                        var data = header.Data;
                        if (data.Length < 25)
                        {
                            throw Damaged("RAR4 file header is too short.");
                        }

                        var host = data[8];
                        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(19));
                        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(21));
                        var nameStart = (header.Flags & 0x100) != 0 ? 33 : 25;
                        if (nameStart + nameSize > data.Length)
                        {
                            throw Damaged("RAR4 file name is too long.");
                        }

                        var isDirectory = (header.Flags & 0xE0) == 0xE0;
                        var splitBefore = (header.Flags & 0x01) != 0;
                        var splitAfter = (header.Flags & 0x02) != 0;

                        // Unix で作った書庫で、属性の上位が S_IFLNK ならシンボリックリンク。指す先は中身にある
                        var link = host == 3 && (attributes & 0xF000) == 0xA000 ? RarLinkKind.LegacyUnixSymbolicLink : RarLinkKind.None;

                        if (!splitBefore)
                        {
                            // 展開後の大きさが不明なものは、下位 (と大きい書庫では上位) がすべて 1 (UnRAR と同じ見方)
                            var sizeUnknown = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) == uint.MaxValue
                                && ((header.Flags & 0x100) == 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(29)) == uint.MaxValue);
                            items.Add(new RarItem(null, isDirectory, link, null, false, (header.Flags & 0x04) != 0, sizeUnknown));
                            nameBytes += nameSize;
                        }

                        // RAR4 以前の辞書は最大 4 MB
                        if (!isDirectory)
                        {
                            maxDictionary = Math.Max(maxDictionary, 64L * 1024 << ((header.Flags >> 5) & 0x7));
                        }

                        lastSplitAfter = splitAfter;
                        SkipData(header.DataSize);
                        break;
                    }

                    case 0x7A: // サービス (コメント、アクセス権、NTFS の代替データストリームなど)
                    {
                        var data = header.Data;
                        if (data.Length >= 25)
                        {
                            var method = data[18];
                            var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(19));
                            var nameStart = (header.Flags & 0x100) != 0 ? 33 : 25;
                            var isComment = nameStart + nameSize <= data.Length && nameSize == 3
                                            && data.AsSpan(nameStart, 3).SequenceEqual("CMT"u8);

                            // 圧縮していないコメントだけを読む (0x30 が格納)
                            if (isComment && method == 0x30 && (header.Flags & 0x04) == 0 && comment is null
                                && header.DataSize <= MaxCommentBytes)
                            {
                                comment = ReadData((int)header.DataSize);
                                break;
                            }
                        }

                        SkipData(header.DataSize);
                        break;
                    }

                    case 0x7B: // 書庫の終わり
                        more = (header.Flags & 0x0001) != 0;
                        sawEnd = true;
                        break;

                    default:
                        SkipData(header.DataSize);
                        break;
                }

                if (sawEnd)
                {
                    break;
                }
            }

            return new VolumeResult(false, solid, false, more || (!sawEnd && lastSplitAfter), newNaming);
        }

        // ---------------------------------------------------------------- 共通

        private void SkipData(ulong size) => SkipData(size > long.MaxValue ? long.MaxValue : (long)size);

        private void SkipData(long size)
        {
            if (size < 0 || stream.Position + size > _length || stream.Position + size < 0)
            {
                throw Damaged("RAR data runs past the end.");
            }

            stream.Position += size;
        }

        private byte[] ReadData(int size)
        {
            if (stream.Position + size > _length)
            {
                throw Damaged("RAR data runs past the end.");
            }

            var data = new byte[size];
            stream.ReadExactly(data);
            return data;
        }

        private ulong ReadStreamVint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException();
                }

                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw Damaged("RAR5 number is too long.");
        }
    }

    /// <summary>RAR5 の数 (vint)。7 ビットずつ、上のビットが続きの印。10 バイトまで。</summary>
    private static ulong ReadVint(byte[] data, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (at >= data.Length)
            {
                throw Damaged("RAR5 header ends in a number.");
            }

            var b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw Damaged("RAR5 number is too long.");
    }

    private static DamagedDataException Damaged(string message) => new(new InvalidDataException(message));

    private static uint Crc(byte[] data)
    {
        var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc.Update(data);
        return (uint)crc.Value;
    }

    /// <summary>読む書庫のコメントの大きさの上限。ZIP のコメントの上限 (64 KB) と同じ。</summary>
    internal const int MaxCommentBytes = 65535;
}

/// <summary>扱わない RAR の種類 (#336)。</summary>
internal enum UnsupportedRar
{
    /// <summary>分割した自己解凍の RAR。</summary>
    SplitSelfExtracting,

    /// <summary>続きの分割ファイルの名前の決まりに当たらない名前の、分割した RAR。</summary>
    UnknownVolumeNaming,

    /// <summary>RAR 1.4 以前の書庫。</summary>
    TooOld,
}

/// <summary>扱わない種類の RAR (#336)。壊れているのではないので、壊れているとは言わない。</summary>
internal sealed class UnsupportedRarException(UnsupportedRar kind) : IOException($"Unsupported RAR: {kind}")
{
    /// <summary>どの種類か。</summary>
    public UnsupportedRar Kind { get; } = kind;
}

/// <summary>分割した RAR の途中の分割ファイルが無い (#336)。</summary>
/// <param name="missing">見つからなかった分割ファイルのパス。</param>
internal sealed class MissingVolumeException(string missing)
    : IOException($"RAR volume not found: {missing}")
{
    /// <summary>見つからなかった分割ファイルのパス。</summary>
    public string Missing { get; } = missing;
}
