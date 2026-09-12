using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MiniAppLoader.Core;

namespace MiniAppLoader.Views;

/// <summary>
///     Ánh xạ <see cref="PluginStatus"/> sang brush của chấm trạng thái.
///     <para>
///         Tra key trong resource của chính view (<see cref="ResourceHost"/>) chứ không
///         dùng brush cứng, nên đổi theme Revit là chấm đổi màu theo.
///     </para>
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    /// <summary>View sở hữu palette. Gán một lần trong code-behind của PluginHubView.</summary>
    public FrameworkElement? ResourceHost { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as PluginStatus? ?? PluginStatus.Idle;
        return ResourceHost?.TryFindResource($"Hub.Status.{status}") as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
///     <see langword="true"/> thành <see cref="Visibility.Collapsed"/>. Dùng cho khối
///     "chưa có plugin nào": hiện đúng khi danh sách rỗng.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Số dương thành <see cref="Visibility.Visible"/>; 0 thì ẩn hẳn.</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
///     Chỉ hiện với plugin <see cref="PluginKind.Command"/>. Plugin kiểu
///     <see cref="PluginKind.Application"/> tự dựng ribbon riêng và không có
///     <c>IExternalCommand</c> nào để loader gọi hộ.
/// </summary>
public sealed class CommandKindToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PluginKind.Command
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
