using System;
using System.Windows;
using System.Windows.Controls;
using MiniAppLoader.ViewModels;

namespace MiniAppLoader.Views;

/// <summary>
///     Nội dung của dockable pane "Plugin Hub".
///     <para>
///         View này được dựng MỘT LẦN trong <c>OnStartup</c> và sống suốt phiên Revit —
///         Revit chỉ cho <c>RegisterDockablePane</c> ở đó. Nó không bao giờ bị unload, kể cả
///         khi người dùng đóng pane (chỉ ẩn đi).
///     </para>
/// </summary>
public partial class PluginHubView : UserControl
{
    private Action? _unsubscribeTheme;

    public PluginHubView(PluginHubViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Converter tra brush theo palette của chính view, nên phải biết view là ai.
        ((StatusBrushConverter)Resources["StatusBrush"]).ResourceHost = this;

        ThemeManager.Apply(this);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _unsubscribeTheme ??= ThemeManager.Subscribe(() => ThemeManager.Apply(this));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Revit bắn Unloaded khi pane bị ẩn chứ không phải khi huỷ hẳn, nên KHÔNG huỷ
        // đăng ký theme ở đây — pane hiện lại vẫn phải đổi màu đúng. Việc dọn nằm ở
        // Application.OnShutdown.
    }

    /// <summary>Huỷ đăng ký sự kiện theme. Gọi từ <c>Application.OnShutdown</c>.</summary>
    public void Teardown()
    {
        _unsubscribeTheme?.Invoke();
        _unsubscribeTheme = null;
    }
}
