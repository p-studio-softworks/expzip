using System.Diagnostics;
using System.Windows.Threading;

namespace Expzip.Ui;

/// <summary>
/// 待っている間、経過した秒を出し続ける (#76)。
/// </summary>
/// <remarks>
/// AI の応答は、考えるモデルだと分単位になる。待ち時間の上限を延ばした結果、
/// 表示が動かないままの時間が長くなり、**動いているのか固まったのかを
/// 利用者が区別できなくなった。**秒が進んでいれば、待てばよいと分かる。
/// <para>
/// 進み具合そのものは分からない。相手が答え終わるまで何も返らないためで、
/// ここで出せるのは「こちらはまだ待っている」ということだけ。
/// </para>
/// </remarks>
internal sealed class WaitTicker : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <param name="text">経過した秒を受け取り、出す文字列を返す。</param>
    /// <param name="show">出す先。作った時点で 0 秒の文字列を一度入れる。</param>
    public WaitTicker(Func<int, string> text, Action<string> show)
    {
        show(text(0));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => show(text((int)_clock.Elapsed.TotalSeconds));
        _timer.Start();
    }

    /// <summary>止める。出した文字列はそのまま残るので、呼んだ側が入れ替える。</summary>
    public void Dispose() => _timer.Stop();
}
