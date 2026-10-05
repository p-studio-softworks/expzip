using System.Collections.Concurrent;

namespace Expzip.Inspection;

/// <summary>
/// 読み終えた中身を、コアの数だけ並べて対策ソフトに渡す (#199)。
/// </summary>
/// <remarks>
/// <para>
/// 中身の検査でいちばん時間がかかるのは、対策ソフトの判定を待つところ。1 本で順に待つと、
/// DLL 800 個で 9.7 秒、小さなファイル 1 個で 2.6 ms かかる。対策ソフトは同時に頼まれた判定を
/// 並べて調べるので、20 本にすると DLL 800 個が 0.85 秒になる。
/// </para>
/// <para>
/// 取り出す側は形式ごとの読み方のまま (7z は先頭から順に読むしかない) で、読み終えた中身を
/// ここへ置いていく。判定を待たずに次のエントリへ進めるので、どの形式でも効く。
/// </para>
/// <para>
/// <b>メモリーの上限。</b> 対策ソフトには中身を丸ごと渡すので、1 つで最大 256 MB になる。
/// 取り出す側は、中身を読む前に <see cref="Reserve"/> で場所を確保し、上限を超えるなら
/// 判定が終わって空くまで待つ。何も抱えていなければ、上限を超える 1 つでも受け付ける。
/// </para>
/// </remarks>
internal sealed class ScanPool : IDisposable
{
    /// <summary>判定を待つ中身として、同時に抱える量の上限。</summary>
    private const long HoldLimit = 512L * 1024 * 1024;

    private readonly BlockingCollection<Job> _queue = new();

    private readonly Task[] _workers;

    private readonly CancellationToken _cancellationToken;

    private readonly Lock _lock = new();

    private readonly List<(long Order, string Name)> _detected = [];

    /// <summary>抱えている量。<see cref="_budgetGate"/> で守る。</summary>
    private long _held;

    private readonly object _budgetGate = new();

    /// <param name="scanner">対策ソフトの受け口。</param>
    /// <param name="cancellationToken">中断用。</param>
    public ScanPool(AmsiScanner scanner, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;

        // 判定を待つ間はスレッドが止まるので、スレッドプールを使わずに専用のスレッドを立てる。
        // プールのスレッドを待たせると、判定そのものを走らせるスレッドが足りなくなる
        _workers = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => Task.Factory.StartNew(
                () => Work(scanner.OpenLane()), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
    }

    /// <summary>
    /// 中身を置く場所を確保する。上限を超えるなら、空くまで待つ。
    /// </summary>
    /// <exception cref="OperationCanceledException">待っている間に中断された場合。</exception>
    public void Reserve(long bytes)
    {
        lock (_budgetGate)
        {
            while (_held > 0 && _held + bytes > HoldLimit)
            {
                Monitor.Wait(_budgetGate, TimeSpan.FromMilliseconds(100));
                _cancellationToken.ThrowIfCancellationRequested();
            }

            _held += bytes;
        }
    }

    /// <summary>確保した場所を返す。渡さずに終わった分に使う。</summary>
    public void Release(long bytes)
    {
        lock (_budgetGate)
        {
            _held -= bytes;
            Monitor.PulseAll(_budgetGate);
        }
    }

    /// <summary>
    /// 中身を判定に回す。置き場は <see cref="Reserve"/> で確保しておき、判定が終わると返される。
    /// </summary>
    /// <param name="order">書庫の中でのエントリの順番。報告をこの順に並べる。</param>
    /// <param name="name">書庫内のパス。</param>
    /// <param name="data">中身。<paramref name="length"/> より長くてもよい。</param>
    /// <param name="length">実際に見てもらう長さ。</param>
    /// <param name="reserved">確保した量。</param>
    public void Post(long order, string name, byte[] data, int length, long reserved)
        => _queue.Add(new Job(order, name, data, length, reserved), _cancellationToken);

    /// <summary>
    /// 回したものがすべて終わるのを待ち、検出されたものを書庫の順に返す。
    /// </summary>
    /// <exception cref="OperationCanceledException">待っている間に中断された場合。</exception>
    public IReadOnlyList<(long Order, string Name)> Finish()
    {
        _queue.CompleteAdding();

        try
        {
            Task.WaitAll(_workers, _cancellationToken);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(_cancellationToken);
        }

        // 作業は中断に気づくと、残りを判定に掛けずに終わる。その結果は報告に使えない
        _cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return _detected.OrderBy(static d => d.Order).ToList();
        }
    }

    /// <summary>
    /// 判定を回す作業を止める。裏で走っている判定は、受け口を閉じるときに待つ (<see cref="AmsiScanner.Dispose"/>)。
    /// </summary>
    public void Dispose()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }

        // 中断のときは、作業は判定を待つのをやめてすぐ戻る
        try
        {
            Task.WaitAll(_workers);
        }
        catch (AggregateException)
        {
        }

        _queue.Dispose();
    }

    private void Work(AmsiScanner.Lane lane)
    {
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable(_cancellationToken))
            {
                try
                {
                    if (lane.Scan(job.Data, job.Length, job.Name, _cancellationToken))
                    {
                        lock (_lock)
                        {
                            _detected.Add((job.Order, job.Name));
                        }
                    }
                }
                finally
                {
                    Release(job.Reserved);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 中断。残りは判定に掛けない
        }
    }

    private readonly record struct Job(long Order, string Name, byte[] Data, int Length, long Reserved);
}
