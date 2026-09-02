using System.Globalization;
using System.Windows.Data;

namespace Expzip.Ui;

/// <summary>
/// 一覧の残り幅を、列どうしで分け合わせる (#77)。
/// </summary>
/// <remarks>
/// <para>
/// <c>GridViewColumn</c> の幅には <c>*</c> が無く、数値しか置けない。窓を広げても
/// 列は元の幅のままで、**中身の長い列が切れたままになる。**読ませるための一覧で
/// 中身が読めないので、一覧の実幅から、幅の決まっている列の分を引き、
/// 残りを割合で配る。
/// </para>
/// <para>
/// <see cref="Fixed"/> には、幅を固定している列の合計に、
/// 縦スクロール棒と余白の分を足した値を入れる。
/// </para>
/// </remarks>
internal sealed class ShareWidth : IValueConverter
{
    /// <summary>幅の決まっている列の合計 (スクロール棒と余白を含む)。</summary>
    public double Fixed { get; set; }

    /// <summary>これより狭くはしない。狭すぎると見出しすら読めなくなる。</summary>
    public double Least { get; set; } = 80;

    /// <param name="value">一覧の実幅 (<c>ActualWidth</c>)。</param>
    /// <param name="parameter">配る割合。残り幅に掛ける。</param>
    public object Convert(
        object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double actual
            || !double.TryParse(
                parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var share))
        {
            return Least;
        }

        return Math.Max(Least, (actual - Fixed) * share);
    }

    public object ConvertBack(
        object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
