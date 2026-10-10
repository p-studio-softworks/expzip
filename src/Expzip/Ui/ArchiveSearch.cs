using System.Text;
using Expzip.Archives;

namespace Expzip.Ui;

/// <summary>
/// 書庫の中を名前で探す (#215)。
/// </summary>
/// <remarks>
/// <para>
/// 探す範囲は書庫全体。**探すのは名前だけ**で、中身は見ない。中身まで探すには取り出す必要があり、
/// 開いただけでは中身に触らない今の作りが崩れる。
/// </para>
/// <para>
/// 1 文字入れるごとに絞るので、名前の比べやすい形は書庫ごとに 1 度だけ作って覚えておく。
/// 前の文字に書き足しただけなら、前に見つかったものの中から探す。
/// </para>
/// </remarks>
internal sealed class ArchiveSearch
{
    private readonly List<Item> _items = [];

    private string _lastKey = string.Empty;

    private List<Item>? _lastHits;

    public ArchiveSearch(ArchiveFolder root)
    {
        Walk(root);

        void Walk(ArchiveFolder folder)
        {
            foreach (var child in folder.Folders)
            {
                _items.Add(new Item(Fold(child.Name), folder, child, null));
                Walk(child);
            }

            foreach (var file in folder.Files)
            {
                _items.Add(new Item(Fold(file.Name), folder, null, file));
            }
        }
    }

    /// <summary>探す相手の数。フォルダーとファイルの両方。</summary>
    public int Count => _items.Count;

    /// <summary>名前に <paramref name="text"/> を含む項目。フォルダーとファイルの両方。</summary>
    public IReadOnlyList<Item> Find(string text)
    {
        var key = Fold(text.Trim());
        if (key.Length == 0)
        {
            return [];
        }

        // 書き足しただけなら、前に見つかったものの外には無い
        var pool = _lastHits is not null && _lastKey.Length > 0
                   && key.Contains(_lastKey, StringComparison.Ordinal)
            ? _lastHits
            : _items;

        var hits = pool.Where(item => item.Key.Contains(key, StringComparison.Ordinal)).ToList();

        _lastKey = key;
        _lastHits = hits;
        return hits;
    }

    /// <summary>
    /// 比べるための形。大文字と小文字、全角と半角、濁点の付け方 (#203) の違いを無くす。
    /// </summary>
    /// <remarks>
    /// macOS で作った書庫は「が」を「か」と濁点の 2 文字で持っていることがあり、
    /// そのままでは打った「が」と一致しない。
    /// </remarks>
    private static string Fold(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            // 対になっていないサロゲートなど、正規化できない字を含む名前。そのまま比べる
            return text.ToUpperInvariant();
        }
    }

    /// <summary>見つかった項目 1 つ。</summary>
    /// <param name="Key">比べるための形にした名前。</param>
    /// <param name="Parent">入っているフォルダー。</param>
    /// <param name="Folder">フォルダーなら、そのフォルダー。</param>
    /// <param name="Entry">ファイルなら、そのファイル。</param>
    internal sealed record Item(string Key, ArchiveFolder Parent, ArchiveFolder? Folder, ArchiveEntry? Entry);
}
