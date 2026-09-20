using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Expzip.Ui;

/// <summary>
/// <see cref="BooleanToVisibilityConverter"/> の逆。true のときに隠す。
/// 名前の変更中だけ表示を入力欄に入れ替えるために使う (#15)。
/// </summary>
internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
