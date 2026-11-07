using System.Collections.Concurrent;
using Expzip.Archives;

// アクセス権やリンクなど、Windows の働きを確かめるテスト
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Expzip.Tests;

/// <summary>
/// <see cref="AtomicFile.Trace"/> の知らせを集める。テストごとに自分のフォルダーを作り、その下の知らせだけを残す。
/// </summary>
/// <remarks>
/// 知らせの先はプロセス全体で 1 つなので、これを使うテストは <see cref="TraceCollection"/> に入れて 1 つずつ走らせる。
/// 既にある差し替え口が入っていると、配るときと違う道を通るので、どれも空であることを確かめる。
/// </remarks>
internal sealed class TraceLog : IDisposable
{
    private readonly ConcurrentQueue<(string What, string Path)> _events = new();

    public TraceLog()
    {
        Assert.Null(AtomicFile.Trace);
        Assert.Null(AtomicFile.TemporaryPathOverride);
        Assert.Null(AtomicFile.IsProtectedOverride);
        Assert.Null(ZipArchiveWriter.WorkFilePathOverride);
        Assert.Null(MarkOfTheWeb.Override);

        Root = Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        AtomicFile.Trace = Record;
    }

    /// <summary>このテストのフォルダー。</summary>
    public string Root { get; }

    /// <summary>知らせを受けたときに、テストが割り込むための処理。</summary>
    public Action<string, string>? OnEvent { get; set; }

    /// <summary>受けた知らせ。</summary>
    public IReadOnlyList<(string What, string Path)> Events => [.. _events];

    /// <summary>受けた知らせの名前だけを、受けた順に並べる。</summary>
    public string Names => string.Join(" ", _events.Select(static e => e.What));

    public void Clear() => _events.Clear();

    public string PathOf(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        AtomicFile.Trace = null;
        try
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せないものは一時フォルダーに残るだけ
        }
    }

    private void Record(string what, string path)
    {
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _events.Enqueue((what, path));
        OnEvent?.Invoke(what, path);
    }
}

/// <summary><see cref="AtomicFile.Trace"/> を使うテストのまとまり。ほかと並べて走らせない。</summary>
[CollectionDefinition(nameof(TraceCollection), DisableParallelization = true)]
public sealed class TraceCollection
{
}
