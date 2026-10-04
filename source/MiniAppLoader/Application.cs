using System.IO;
using System.Reflection;
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
///         Thứ tự trong <see cref="OnStartup"/> là cố ý: logger → host → ribbon → pane →
///         plugin. Ribbon dựng trước pane vì <c>RegisterDockablePane</c> là bước dễ hỏng nhất
///         ở đây, và plugin nạp sau cùng vì plugin <c>Kind=Application</c> tự dựng ribbon của
///         nó — phải để ribbon của loader chiếm chỗ trước thì ảnh chụp ribbon diff mới sạch.
///     </para>
///     <para>
///         <b>Mỗi giai đoạn được bọc riêng.</b> Nếu để exception thoát ra khỏi
///         <c>OnStartup</c>, Revit hiện "External Tool Failure" rồi disable TOÀN BỘ add-in —
///         mất luôn ribbon, và thông báo đó không nói lỗi ở đâu. Bọc riêng từng giai đoạn thì
///         pane hỏng vẫn còn ribbon để dùng, và log file nói chính xác giai đoạn nào chết.
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

        Log.Information("=== MiniAppLoader OnStartup, Revit {Version} ===", Application.ControlledApplication.VersionNumber);

        if (!RunStage("khởi tạo host", () =>
                PluginHost.Initialize(Application.ControlledApplication.VersionNumber, FindLegacyConfig()))) return;

        RunStage("dựng ribbon", CreateRibbon);
        RunStage("đăng ký dockable pane", () => CreateHubPane(logSink));
        RunStage("nạp plugin từ config", () => PluginHost.Current.LoadFromConfig(Application));

        Log.Information("MiniAppLoader sẵn sàng — {Slots} slot ribbon, config tại {Path}",
            SlotPool.MaxSlots, PluginHost.Current.ConfigPath);
    }

    public override void OnShutdown()
    {
        _hubView?.Teardown();
        PluginHost.Shutdown();
        Log.CloseAndFlush();
    }

    /// <summary>
    ///     Chạy một giai đoạn khởi động, ghi log rõ ràng và KHÔNG để exception thoát ra
    ///     <c>OnStartup</c>. Trả về <see langword="false"/> nếu giai đoạn đó hỏng.
    /// </summary>
    private static bool RunStage(string name, Action stage)
    {
        try
        {
            stage();
            Log.Debug("Giai đoạn '{Stage}' xong", name);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Giai đoạn '{Stage}' thất bại", name);
            return false;
        }
    }

    private void CreateRibbon()
    {
        var hubPanel = Application.CreatePanel("Hub", TabName);

        var hubButton = hubPanel.AddPushButton<ShowHubCommand>("Plugin\nHub")
            .SetImage("/MiniAppLoader;component/Resources/Icons/RibbonIcon16-light.png")
            .SetLargeImage("/MiniAppLoader;component/Resources/Icons/RibbonIcon32-light.png")
            .SetToolTip("Bật/tắt bảng quản lý plugin")
            .SetLongDescription("Thêm, nạp lại, chạy và gỡ plugin mà không cần khởi động lại Revit.");

        // Nút này luôn hiện nên post được; ShowHubCommand nhận thêm việc chạy command của plugin
        // Application khi có yêu cầu đang chờ.
        PluginHost.Current.Slots.UseAsDispatchCarrier(hubButton);

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
    ///     Bản v1 để <c>plugins.json</c> cạnh DLL đã deploy. Trả về đường dẫn đó để
    ///     <see cref="ConfigStore"/> mang danh sách plugin sang chỗ mới trong lần chạy đầu.
    /// </summary>
    private static string? FindLegacyConfig()
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(directory)) return null;

        var legacyPath = Path.Combine(directory!, "plugins.json");
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    /// <summary>
    ///     Log đi vào ba chỗ: khung nhật ký trong pane (<see cref="HubLogSink"/>, thay cho
    ///     kiểu bắn <c>TaskDialog</c> từng cái một của bản v1), Debug output cho IDE, và một
    ///     FILE — file là thứ duy nhất còn đọc được khi add-in chết ngay trong
    ///     <c>OnStartup</c>, lúc chưa có pane nào để mà hiện log.
    /// </summary>
    private static void CreateLogger(HubLogSink logSink)
    {
        const string outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Debug(LogEventLevel.Debug, outputTemplate)
            .WriteTo.Sink(logSink)
            .WriteTo.File(LogFilePath, outputTemplate: outputTemplate, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .MinimumLevel.Debug()
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = (Exception)args.ExceptionObject;
            Log.Fatal(exception, "Domain unhandled exception");
        };
    }

    /// <summary>Log nằm ở <c>%LocalAppData%\MiniAppLoader\logs\</c>, tách theo ngày.</summary>
    internal static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "logs", "miniapploader-.log");
}
