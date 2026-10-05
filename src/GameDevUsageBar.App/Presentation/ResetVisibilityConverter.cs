using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GameDevUsageBar.App.Presentation;
public sealed class ResetVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is true && values[1] is true ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
