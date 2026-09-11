using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MiniAppLoader.ViewModels;

namespace MiniAppLoader.Views;

/// <summary>
///     Nội dung của dockable pane "Plugin Hub".
///     <para>
///         View này được dựng MỘT LẦN trong <c>OnStartup</c> và sống suốt phiên Revit —
///         Revit chỉ cho <c>RegisterDockablePane</c> ở đó. Nó không bao giờ bị huỷ, kể cả khi
///         người dùng đóng pane (chỉ ẩn đi).
///     </para>
/// </summary>
public partial class PluginHubView : UserControl
{
    private readonly PluginHubViewModel _viewModel;
    private Action? _unsubscribeTheme;

    public PluginHubView(PluginHubViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Converter tra brush theo palette của chính view, nên phải biết view là ai.
        ((StatusBrushConverter)Resources["StatusBrush"]).ResourceHost = this;

        ThemeManager.Apply(this);

        // Khung nhật ký phải tự bám dòng mới nhất — log mà người dùng phải tự cuộn tay
        // xuống mỗi lần có sự kiện thì coi như không đọc được lúc đang sửa code.
        ((INotifyCollectionChanged)viewModel.Log).CollectionChanged += OnLogChanged;

        Loaded += OnLoaded;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Huỷ đăng ký sự kiện theme. Gọi từ <c>Application.OnShutdown</c>.</summary>
    public void Teardown()
    {
        _unsubscribeTheme?.Invoke();
        _unsubscribeTheme = null;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) LogScroll.ScrollToEnd();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Revit bắn Unloaded khi pane bị ẩn chứ không phải khi huỷ hẳn, nên KHÔNG huỷ đăng
        // ký theme ở đó — pane hiện lại vẫn phải đổi màu đúng. Việc dọn nằm ở Teardown().
        _unsubscribeTheme ??= ThemeManager.Subscribe(() => ThemeManager.Apply(this));
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedAssemblies(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        foreach (var path in GetDroppedAssemblies(e)) _viewModel.AddPlugin(path);
        e.Handled = true;
    }

    private static string[] GetDroppedAssemblies(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];

        return ((string[])e.Data.GetData(DataFormats.FileDrop))
            .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
