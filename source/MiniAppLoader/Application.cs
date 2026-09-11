using Autodesk.Revit.UI;
using MiniAppLoader.Commands;
using MiniAppLoader.Core;
using MiniAppLoader.ViewModels;
using MiniAppLoader.Views;
using Nice3point.Revit.Extensions.UI;
using Nice3point.Revit.Toolkit.Decorators;
using Nice3point.Revit.Toolkit.External;
using Serilog;
using Serilog.Events;

namespace MiniAppLoader;

/// <summary>
///     Điểm vào của add-in.
///     <para>
///         Thứ tự trong <see cref="OnStartup"/> là cố ý và không đảo được:
///         logger → host → ribbon → dockable pane. Ribbon phải xong TRƯỚC pane, vì
///         <c>RegisterDockablePane</c> là thứ dễ hỏng nhất ở đây; nếu nó ném exception thì
///         Revit disable cả add-in — ít nhất phải đảm bảo lỗi đó không kéo theo việc mất
///         luôn ribbon nếu sau này ta bọc try/catch quanh nó.
///     </para>
/// </summary>
[UsedImplicitly]
public class Application : ExternalApplication
{
    private const string TabName = "MiniApps";

    private PluginHubView? _hubView;

    public override void OnStartup()
    {
        var logSink = new HubLogSink();
        CreateLogger(logSink);

        PluginHost.Initialize();

        CreateRibbon();
        PluginHost.Current.SeedDemoSlots(); // Phase 2 only - bỏ khi ConfigStore vào ở Phase 3
        CreateHubPane(logSink);

        Log.Information("MiniAppLoader khởi động — {Slots} slot ribbon sẵn sàng", SlotPool.MaxSlots);
    }

    public override void OnShutdown()
    {
        _hubView?.Teardown();
        PluginHost.Shutdown();
        Log.CloseAndFlush();
    }

    private void CreateRibbon()
    {
        var hubPanel = Application.CreatePanel("Hub", TabName);

        hubPanel.AddPushButton<ShowHubCommand>("Plugin\nHub")
            .SetImage("/MiniAppLoader;component/Resources/Icons/RibbonIcon16-light.png")
            .SetLargeImage("/MiniAppLoader;component/Resources/Icons/RibbonIcon32-light.png")
            .SetToolTip("Bật/tắt bảng quản lý plugin")
            .SetLongDescription("Thêm, nạp lại, chạy và gỡ plugin mà không cần khởi động lại Revit.");

        // Panel nút plugin: toàn bộ nút được tạo sẵn rồi ẩn, không tạo thêm lúc chạy.
        // Xem SlotPool để biết vì sao.
        var pluginsPanel = Application.CreatePanel("Plugins", TabName);
        PluginHost.Current.Slots.Build(pluginsPanel);
    }

    private void CreateHubPane(HubLogSink logSink)
    {
        var viewModel = new PluginHubViewModel(PluginHost.Current, logSink);
        _hubView = new PluginHubView(viewModel);

        DockablePaneProvider
            .Register(Application)
            .SetId(PluginHost.HubPaneId)
            .SetTitle("Plugin Hub")
            .SetConfiguration(data => data.FrameworkElement = _hubView);
    }

    /// <summary>
    ///     Log đi vào <see cref="HubLogSink"/> (khung nhật ký trong pane) thay vì bắn
    ///     <c>TaskDialog</c> từng cái một như bản v1, và song song ra Debug output cho IDE.
    /// </summary>
    private static void CreateLogger(HubLogSink logSink)
    {
        const string outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Debug(LogEventLevel.Debug, outputTemplate)
            .WriteTo.Sink(logSink)
            .MinimumLevel.Debug()
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = (Exception)args.ExceptionObject;
            Log.Fatal(exception, "Domain unhandled exception");
        };
    }
}
