using System;
using System.Windows;
using Serilog;

namespace MiniAppLoader.Views;

/// <summary>
///     Đổi palette của Plugin Hub theo theme sáng/tối của Revit.
///     <para>
///         Dictionary được merge vào Resources của CHÍNH UserControl, không phải
///         <c>Application.Current.Resources</c> — <c>Application.Current</c> trong tiến trình
///         này là của Revit, ghi vào đó là đụng vào UI của Revit và của mọi add-in khác.
///     </para>
///     <para>
///         <c>UIThemeManager</c> chỉ có từ Revit 2024; các version cũ hơn không có khái niệm
///         theme nên luôn dùng palette sáng.
///     </para>
/// </summary>
internal static class ThemeManager
{
    // URI PHẢI ở dạng pack tuyệt đối có tên assembly. Dạng rút gọn
    // ("Views/Themes/Light.xaml") chỉ hoạt động khi WPF suy ra được assembly từ
    // Assembly.GetEntryAssembly() — mà trong Revit nó là NULL (Revit.exe là host native,
    // không phải app WPF), nên dạng rút gọn ném IOException ngay trong OnStartup.
    private const string LightUri = "pack://application:,,,/MiniAppLoader;component/Views/Themes/Light.xaml";
    private const string DarkUri = "pack://application:,,,/MiniAppLoader;component/Views/Themes/Dark.xaml";

    /// <summary>Merge palette đúng theme hiện tại vào <paramref name="target"/>.</summary>
    public static void Apply(FrameworkElement target)
    {
        try
        {
            var dictionary = new ResourceDictionary { Source = new Uri(IsDarkTheme() ? DarkUri : LightUri, UriKind.Absolute) };

            target.Resources.MergedDictionaries.Clear();
            target.Resources.MergedDictionaries.Add(dictionary);
        }
        catch (Exception exception)
        {
            // Không có palette thì pane xấu chứ không chết — đừng để nó kéo theo cả pane.
            Log.Error(exception, "Không nạp được palette cho Plugin Hub");
        }
    }

    /// <summary>
    ///     Gọi <paramref name="onChanged"/> mỗi khi người dùng đổi theme Revit (2024+).
    ///     Trả về action để huỷ đăng ký.
    /// </summary>
    public static Action Subscribe(Action onChanged)
    {
#if REVIT2024_OR_GREATER
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => onChanged();

        UIFramework.ApplicationTheme.CurrentTheme.PropertyChanged += Handler;
        return () => UIFramework.ApplicationTheme.CurrentTheme.PropertyChanged -= Handler;
#else
        return () => { };
#endif
    }

    private static bool IsDarkTheme()
    {
#if REVIT2024_OR_GREATER
        return Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark;
#else
        return false;
#endif
    }
}
