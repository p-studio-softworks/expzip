using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Expzip.Configuration;

/// <summary>
/// このバージョンが知らない項目 (<c>JsonExtensionData</c> で読んだもの) を、書き戻せる形に整える (#409、#421)。設定とルールのファイルで使う。
/// </summary>
/// <remarks>
/// <para>
/// 新しいバージョンの Expzip が足した項目を、古いバージョンで保存・適用したときに消さないために、読んだまま持って書き戻す。
/// 書き戻せないものを持っていると、書くたびに失敗して、知っている項目も書けなくなる。ここで捨てる。
/// </para>
/// <para>
/// 設定は裏のスレッドで、ルールは UI のスレッドで読むので、状態を持たない (名前の一覧の覚えだけは、どちらから読んでも同じ)。
/// </para>
/// </remarks>
internal static class UnknownItems
{
    /// <summary>知らない項目の数の上限。ファイル全体で数える。超えたら知らない項目をすべて捨てる。</summary>
    /// <remarks>ふつうのファイルは数 KB で、新しいバージョンが足す項目も数個のはず。</remarks>
    internal const int MaxCount = 1000;

    /// <summary>
    /// 知らない項目の、読んだときの大きさ (名前と値の UTF-8 のバイト数) の合計の上限。ファイル全体で数える。超えたら知らない項目をすべて捨てる。
    /// </summary>
    /// <remarks>
    /// 書き出すときの字下げで、深い入れ子の小さな値は 60 倍ほどに膨らむ。256 KB なら、膨らんでも 16 MB 前後で済む。
    /// </remarks>
    internal const int MaxBytes = 256 * 1024;

    private static readonly ConcurrentDictionary<(JsonSerializerOptions, Type), HashSet<string>> KnownNameCache = new();

    /// <summary>知らない項目の数と大きさを数える。</summary>
    public static (int Count, long Bytes) Measure(IEnumerable<Dictionary<string, JsonElement>?> sets)
    {
        var count = 0;
        long bytes = 0;
        foreach (var unknown in sets)
        {
            foreach (var (name, value) in unknown ?? [])
            {
                count++;
                bytes += Encoding.UTF8.GetByteCount(name) + JsonMarshal.GetRawUtf8Value(value).Length;
            }
        }

        return (count, bytes);
    }

    /// <summary>多すぎるか大きすぎるか (<see cref="MaxCount"/>、<see cref="MaxBytes"/>)。</summary>
    public static bool TooMany((int Count, long Bytes) measured) => measured.Count > MaxCount || measured.Bytes > MaxBytes;

    /// <summary>その型の知っている項目の名前。大文字・小文字を区別せずに比べる。</summary>
    public static HashSet<string> KnownNames(JsonSerializerOptions options, Type type) => KnownNameCache.GetOrAdd((options, type), key => key.Item1.GetTypeInfo(key.Item2).Properties
        .Where(p => !p.IsExtensionData)
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 1 つの型の知らない項目を整える。書き出せないものと、知っている項目と大文字・小文字だけ違う名前のものを捨てる。残すものが無ければ null。
    /// </summary>
    /// <remarks>
    /// 大文字・小文字だけ違う名前 (<c>language</c>) は、大文字・小文字を区別する読み方では知らない項目に入る。
    /// 書き戻すと知っている項目の後ろに残り続け、大文字・小文字を区別しない読み手は後ろの値を使うか、読めなくなる。
    /// </remarks>
    /// <param name="discarded">1 つでも捨てたら true にする。捨てなければ変えない。</param>
    public static Dictionary<string, JsonElement>? Tidy(
        Dictionary<string, JsonElement>? unknown, HashSet<string> known, JsonSerializerOptions options, ref bool discarded)
    {
        if (unknown is null || unknown.Count == 0)
        {
            return null;
        }

        var kept = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in unknown)
        {
            if (!known.Contains(name) && CanWrite(name, value, options))
            {
                kept[name] = value;
            }
            else
            {
                discarded = true;
            }
        }

        return kept.Count == 0 ? null : kept;
    }

    /// <summary>知らない項目を 1 つ、名前ごと書き出せるか。</summary>
    private static bool CanWrite(string name, JsonElement value, JsonSerializerOptions options)
    {
        try
        {
            JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [name] = value }, options);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                   or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
