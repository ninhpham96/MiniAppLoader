using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MiniAppLoader.Core.Runtime;
using Nice3point.Revit.Toolkit;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>
///     Điểm quy tụ state của loader trong một phiên Revit.
///     <para>
///         Cố ý là INSTANCE chứ không phải static class, và chỉ lộ ra đúng một static
///         <see cref="Current"/>. Lý do: static field nào trỏ (dù gián tiếp) tới type của
///         plugin sẽ giữ chặt AssemblyLoadContext của plugin đó, khiến <c>Unload()</c> thất
///         bại IM LẶNG và bạn chạy code cũ mà không biết. State nằm trong instance thì gán
///         <see langword="null"/> được, và kiểm chứng được.
///     </para>
///     <para>
///         Vẫn phải có một static locator vì <c>GenericCommandNN</c> và
///         <c>IDockablePaneProvider</c> đều do Revit tự khởi tạo bằng constructor rỗng —
///         không có DI container nào với tới được chúng.
///     </para>
/// </summary>
public sealed class PluginHost
{
    /// <summary>ID của dockable pane Plugin Hub. Cố định để Revit nhớ được vị trí neo.</summary>
    public static readonly DockablePaneId HubPaneId = new(new Guid("6F8E1B42-2C55-4A7E-93B6-9C2D1A5E4F30"));

    private static PluginHost? _current;

    private readonly ConfigStore _config;

    /// <summary>
    ///     Chặn ghi config trong lúc ĐANG đọc config.
    ///     <para>
    ///         <see cref="Bind"/> gán <c>slot.AutoReload</c>, việc đó bắn PropertyChanged, và
    ///         Plugin Hub hiểu nhầm đó là người dùng vừa tick vào ô "auto" nên gọi
    ///         <see cref="SetAutoReload"/> — tức là GHI ĐÈ plugins.json ngay giữa lúc đang đọc
    ///         nó. Hậu quả thật đã gặp: sửa tay file config rồi mở Revit thì thấy file bị viết
    ///         lại theo định dạng của loader.
    ///     </para>
    /// </summary>
    private bool _loadingConfig;
    private readonly PluginLoader _loader = new();

    /// <summary>Thư mục có thể còn rác shadow cần dọn ở phiên sau — xem <see cref="LoadFromConfigCore"/>.</summary>
    private readonly HashSet<string> _shadowDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReloadPipeline _pipeline;

    private PluginHost(ConfigStore config)
    {
        _config = config;
        _pipeline = new ReloadPipeline(ResolveSlot, OnWatcherReload);
    }

    /// <summary>Instance của phiên Revit hiện tại.</summary>
    public static PluginHost Current =>
        _current ?? throw new InvalidOperationException(
            "PluginHost chưa được khởi tạo — Application.OnStartup phải chạy trước.");

    public SlotPool Slots { get; } = new();

    /// <summary>Đường dẫn file config, hiện trong Plugin Hub để mở ra sửa tay khi cần.</summary>
    public string ConfigPath => _config.ConfigPath;

    internal static void Initialize(string revitVersion, string? legacyConfigPath)
        => _current = new PluginHost(new ConfigStore(revitVersion, legacyConfigPath));

    internal static void Shutdown()
    {
        if (_current is null) return;

        foreach (var slot in _current.Slots.ActiveSlots().Where(slot => slot.Kind == PluginKind.Application))
        {
            // Revit đang đóng: cho plugin cơ hội dọn state của nó, nhưng KHÔNG đụng vào
            // ribbon — Revit tự lo phần đó, và UI của nó có thể đã tháo dỡ xong.
            _current._loader.StopApplication(slot, EntryOf(slot), removeRibbon: false);
        }

        _current._pipeline.Dispose();
        _current = null;
    }

    // ---------------------------------------------------------------- khởi tạo từ config

    /// <summary>
    ///     Nạp danh sách plugin từ <c>plugins.json</c> và dựng state ban đầu.
    ///     <para>
    ///         Gọi từ <c>OnStartup</c> với <see cref="UIControlledApplication"/> THẬT của
    ///         Revit — plugin <c>Kind=Application</c> phải được <c>OnStartup</c> ngay lúc này
    ///         mới kịp dựng ribbon của nó.
    ///     </para>
    /// </summary>
    internal void LoadFromConfig(UIControlledApplication application)
    {
        _loadingConfig = true;
        try
        {
            LoadFromConfigCore(application);
        }
        finally
        {
            _loadingConfig = false;
        }
    }

    private void LoadFromConfigCore(UIControlledApplication application)
    {
        var entries = _config.Load();

        if (entries.Count > SlotPool.MaxSlots)
        {
            Log.Warning("plugins.json có {Count} plugin nhưng chỉ có {Max} slot — {Extra} plugin cuối bị bỏ qua",
                entries.Count, SlotPool.MaxSlots, entries.Count - SlotPool.MaxSlots);
            entries = entries.Take(SlotPool.MaxSlots).ToList();
        }

        // Dọn rác shadow của PHIÊN TRƯỚC: lúc này chưa nạp gì nên chắc chắn không file nào
        // đang bị phiên hiện tại khoá — đây là cơ hội duy nhất dọn được rác net48 để lại.
        // Quét cả thư mục của plugin ĐÃ GỠ khỏi config: rác của chúng bị khoá lúc gỡ nên nằm
        // lại, và nếu chỉ quét theo config thì không bao giờ có ai dọn.
        var activeDirectories = entries
            .Select(entry => Path.GetDirectoryName(entry.DllPath))
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _shadowDirectories.Clear();
        _shadowDirectories.UnionWith(activeDirectories);

        foreach (var directory in _config.LoadShadowDirectories().Concat(activeDirectories).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stillDirty = ShadowCopy.CleanupDirectory(directory);

            // Thư mục không còn plugin nào và đã sạch thì thôi theo dõi.
            if (stillDirty) _shadowDirectories.Add(directory);
        }

        _config.SaveShadowDirectories(_shadowDirectories);

        foreach (var entry in entries)
        {
            var slot = Slots.TakeFreeSlot();
            if (slot is null) break;

            Bind(slot, entry);

            if (slot.Kind == PluginKind.Application)
            {
                // Plugin tự dựng ribbon riêng -> không chiếm nút trên panel Plugins, và phải
                // chạy OnStartup ngay bây giờ như một add-in độc lập thật sự.
                TryStartApplication(slot, entry, application);
            }
            else
            {
                Slots.ShowButton(slot);
            }

            if (slot.AutoReload) _pipeline.Watch(slot);
        }

        Log.Information("Đã nạp {Count} plugin từ {Path}", entries.Count, _config.ConfigPath);
    }

    // ---------------------------------------------------------------- nút ribbon của plugin

    /// <summary>
    ///     Nút ribbon của một plugin được bấm: nạp lại DLL rồi chuyển tiếp <c>Execute</c>
    ///     sang command thật của plugin.
    ///     <para>
    ///         Đang chạy bên trong <c>IExternalCommand.Execute</c> của
    ///         <c>GenericCommandNN</c>, nên đã ở đúng API context và đúng thread — đây là lý
    ///         do plugin phải chạy qua nút (hoặc qua <c>PostCommand</c>) chứ không gọi thẳng
    ///         từ dockable pane được.
    ///     </para>
    /// </summary>
    public Result ExecuteSlot(int index, ExternalCommandData data, ref string message, ElementSet elements)
    {
        var slot = Slots.Slots[index];

        if (slot.IsFree)
        {
            TaskDialog.Show("MiniAppLoader", "Slot này đã được gỡ. Mở Plugin Hub để gán plugin khác.");
            return Result.Cancelled;
        }

        try
        {
            // Chỉ nạp lại khi DLL đã đổi (hoặc chưa nạp lần nào): nạp vô điều kiện mỗi lần bấm
            // thì mỗi click sinh thêm một bản shadow + một ALC mới.
            if (_loader.NeedsReload(slot)) _loader.Reload(slot);
            return _loader.CreateCommand(slot, EntryOf(slot).CommandClassName).Execute(data, ref message, elements);
        }
        catch (Exception exception)
        {
            slot.Status = PluginStatus.Error;
            slot.LastError = exception.ToString();
            message = exception.Message;

            Log.Error(exception, "Chạy '{Id}' thất bại", slot.Id);
            return Result.Failed;
        }
    }

    /// <summary>
    ///     Chạy plugin từ Plugin Hub, đi qua đúng pipeline command của Revit.
    ///     <para>
    ///         <c>PostCommand</c> đẩy lệnh vào hàng đợi để Revit tự dựng
    ///         <c>ExternalCommandData</c> hợp lệ rồi gọi <c>Execute</c> — y hệt khi người
    ///         dùng bấm chuột. KHÔNG được thay bằng cách giữ lại một
    ///         <c>ExternalCommandData</c> cũ: nó là wrapper quanh state native chỉ sống trong
    ///         đúng một lần invoke, dùng lại sẽ giết Revit bằng access violation chứ không
    ///         ném exception bắt được.
    ///     </para>
    /// </summary>
    public bool TryRunSlot(PluginSlot slot)
    {
        var commandId = Slots.GetCommandId(slot.Index);
        if (commandId is null)
        {
            Log.Warning("'{Id}': không tra được ID lệnh ribbon trên version Revit này", slot.Id);
            return false;
        }

        try
        {
            RevitContext.UiApplication.PostCommand(RevitCommandId.LookupCommandId(commandId));
            return true;
        }
        catch (Exception exception)
        {
            // Revit ném nếu đang có command khác chạy dở.
            Log.Warning(exception, "Không post được lệnh cho '{Id}' — Revit đang bận?", slot.Id);
            return false;
        }
    }

    // ---------------------------------------------------------------- thêm / gỡ / nạp lại

    /// <summary>Thêm một DLL làm plugin cố định: kiểm tra, gán slot, hiện nút, ghi config.</summary>
    public (bool Success, string Message) AddPlugin(string dllPath)
    {
        if (!File.Exists(dllPath)) return (false, $"Không thấy file: {dllPath}");

        var existing = Slots.ActiveSlots()
            .FirstOrDefault(slot => string.Equals(slot.DllPath, dllPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return (false, $"'{existing.Id}' đã được nạp trước đó rồi.");

        var slot = Slots.TakeFreeSlot();
        if (slot is null) return (false, $"Đã dùng hết {SlotPool.MaxSlots} slot. Gỡ bớt một plugin trước.");

        var id = Path.GetFileNameWithoutExtension(dllPath);
        var entry = new PluginEntry { Id = id, DllPath = dllPath, ButtonText = id };
        Bind(slot, entry);

        // Ghi nhớ thư mục TRƯỚC khi nạp: nạp là tạo shadow, và nếu sau đó plugin bị gỡ hay Revit
        // đóng thì phần rác bị khoá chỉ dọn được ở phiên sau, qua danh sách này.
        if (Path.GetDirectoryName(dllPath) is { Length: > 0 } dllDirectory && _shadowDirectories.Add(dllDirectory))
        {
            _config.SaveShadowDirectories(_shadowDirectories);
        }

        (bool HasApplication, IReadOnlyList<(string FullName, string Name)> Commands) found;
        try
        {
            // Nạp thử để biết DLL có hợp lệ và có entry point hay không, TRƯỚC khi ghi vào
            // config — tránh để lại entry hỏng mà lần mở Revit sau vẫn cố nạp.
            _loader.Reload(slot);
            found = _loader.Discover(slot);

            if (!found.HasApplication && found.Commands.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Không tìm thấy class nào implement IExternalCommand hay IExternalApplication trong {id}.");
            }
        }
        catch (Exception exception)
        {
            _loader.Unload(slot);
            Release(slot);
            Log.Error(exception, "Không nạp được '{Path}'", dllPath);
            return (false, exception.Message);
        }

        // DLL có IExternalApplication tự dựng tab/panel riêng -> chạy OnStartup của nó, không
        // chiếm nút trên panel Plugins. Việc dựng ribbon phải nằm trong API context nên đi
        // qua external event của pipeline (đúng đường mà reload vẫn dùng), không làm ngay ở đây.
        if (found.HasApplication)
        {
            slot.Kind = PluginKind.Application;
            entry.Kind = nameof(PluginKind.Application);
            slot.Status = PluginStatus.Pending;

            _pipeline.Watch(slot);
            _pipeline.RequestReload(slot.Index);
            SaveConfig();

            Log.Information("Đã thêm plugin '{Id}' (Application) vào slot {Slot}", id, slot.Index);
            return (true, $"Đã thêm '{id}' — đang dựng tab ribbon riêng của plugin.");
        }

        // Nhiều IExternalCommand trong một DLL -> mỗi command một nút (một slot). Đủ slot mới
        // làm, để không dừng giữa chừng với một nửa số nút.
        var extraNeeded = found.Commands.Count - 1;
        var freeSlots = SlotPool.MaxSlots - Slots.ActiveSlots().Count();
        if (freeSlots < extraNeeded)
        {
            _loader.Unload(slot);
            Release(slot);
            return (false, $"'{id}' có {found.Commands.Count} command nhưng không đủ slot trống ({SlotPool.MaxSlots} slot). Gỡ bớt plugin trước.");
        }

        if (found.Commands.Count == 1)
        {
            Slots.ShowButton(slot);
            _pipeline.Watch(slot);
            SaveConfig();

            Log.Information("Đã thêm plugin '{Id}' vào slot {Slot}", id, slot.Index);
            return (true, $"Đã thêm '{id}' — nút đã có trên panel Plugins.");
        }

        for (var i = 0; i < found.Commands.Count; i++)
        {
            var (fullName, name) = found.Commands[i];
            var target = i == 0 ? slot : Slots.TakeFreeSlot()!;

            Bind(target, new PluginEntry
            {
                Id = $"{id}.{name}",
                DllPath = dllPath,
                ButtonText = name,
                CommandClassName = fullName
            });

            try
            {
                if (i > 0) _loader.Reload(target);
                else target.Status = PluginStatus.Loaded;
            }
            catch (Exception exception)
            {
                target.Status = PluginStatus.Error;
                target.LastError = exception.ToString();
                Log.Error(exception, "Không nạp được command '{Name}' của '{Path}'", name, dllPath);
            }

            Slots.ShowButton(target);
            _pipeline.Watch(target);
        }

        SaveConfig();

        Log.Information("Đã thêm '{Id}': {Count} command, mỗi command một nút", id, found.Commands.Count);
        return (true, $"Đã thêm '{id}' — {found.Commands.Count} command, mỗi command một nút trên panel Plugins.");
    }

    /// <summary>Gỡ plugin: dừng theo dõi, gỡ khỏi bộ nhớ, ẩn nút, trả slot về pool, ghi config.</summary>
    public void RemovePlugin(PluginSlot slot)
    {
        if (slot.IsFree) return;

        var id = slot.Id;

        if (slot.Kind == PluginKind.Application) _loader.StopApplication(slot, EntryOf(slot));

        _pipeline.Unwatch(slot.Index);
        _loader.Unload(slot);
        Slots.HideButton(slot);
        Release(slot);
        SaveConfig();

        Log.Information("Đã gỡ plugin '{Id}'", id);
    }

    /// <summary>Nạp lại một plugin theo yêu cầu thủ công từ Plugin Hub.</summary>
    public void ReloadPlugin(PluginSlot slot)
    {
        if (slot.IsFree) return;

        slot.Status = PluginStatus.Pending;
        _pipeline.RequestReload(slot.Index);
    }

    /// <summary>Nạp lại toàn bộ plugin đang hoạt động.</summary>
    public void ReloadAll()
    {
        foreach (var slot in Slots.ActiveSlots().ToList()) ReloadPlugin(slot);
    }

    /// <summary>Bật/tắt tự động nạp lại cho một plugin.</summary>
    public void SetAutoReload(PluginSlot slot, bool enabled)
    {
        if (_loadingConfig) return;

        if (enabled) _pipeline.Watch(slot);
        else _pipeline.Unwatch(slot.Index);

        SaveConfig();
    }

    // ---------------------------------------------------------------- nội bộ

    /// <summary>Được <see cref="ReloadPipeline"/> gọi trên main thread khi DLL đổi.</summary>
    private void OnWatcherReload(PluginSlot slot, UIApplication application)
    {
        if (slot.Kind == PluginKind.Application)
        {
            var entry = EntryOf(slot);

            // Gọi OnShutdown bản cũ + gỡ đúng phần ribbon nó đã thêm, rồi nạp và chạy lại.
            var controlled = application.AsControlledApplication();

            _loader.StopApplication(slot, entry);
            _loader.StartApplication(slot, entry, controlled);
            RedirectCommands(slot, controlled);
            return;
        }

        _loader.Reload(slot);

        // Plugin nào implement IHotCommand thì tự chạy lại luôn sau build — vòng lặp
        // sửa-code / thấy-kết-quả không cần chạm chuột. Một DLL nhiều command chiếm nhiều
        // slot cùng reload một lúc: chỉ slot đầu tiên chạy hot command, kẻo chạy N lần.
        var isFirstOfDll = !Slots.ActiveSlots().Any(other =>
            other.Index < slot.Index && string.Equals(other.DllPath, slot.DllPath, StringComparison.OrdinalIgnoreCase));
        if (isFirstOfDll) _loader.CreateHotCommand(slot)?.Execute(application);
    }

    /// <summary>
    ///     Plugin Application: chuyển hướng nút ribbon của nó sang bản assembly mới nhất — xem
    ///     <c>RibbonCommandRedirect</c> để biết vì sao Revit tự chạy thì mãi bản cũ.
    /// </summary>
    private void RedirectCommands(PluginSlot slot, UIControlledApplication application)
    {
#if NET8_0_OR_GREATER
        try
        {
            var location = _loader.LoadedLocation(slot);
            if (location is null || Slots.DispatchCommandId is null) return;

            var count = RibbonCommandRedirect.Attach(application, location, className => DispatchApplicationCommand(slot, className));

            if (count > 0) Log.Information("'{Id}': {Count} nút ribbon chạy code mới nhất sau mỗi lần build", slot.Id, count);
            else Log.Debug("'{Id}': không tìm thấy nút ribbon nào để chuyển hướng", slot.Id);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "'{Id}': không gắn được bộ chuyển hướng nút ribbon", slot.Id);
        }
#endif
    }

    /// <summary>Slot và tên class đang chờ chạy — đặt ngay trước <c>PostCommand</c>.</summary>
    private (int Slot, string ClassName)? _pendingDispatch;

    /// <summary>Có command của plugin Application đang chờ được chạy qua nút Plugin Hub.</summary>
    public bool HasPendingDispatch => _pendingDispatch is not null;

    /// <summary>
    ///     Nút ribbon của plugin Application vừa được bấm: nhờ Revit gọi <c>ShowHubCommand</c> (nút
    ///     luôn hiện) để có <c>ExternalCommandData</c> hợp lệ. Trả về <see langword="false"/> nếu không post được,
    ///     khi đó nút rơi về đường chạy mặc định của Revit.
    /// </summary>
    private bool DispatchApplicationCommand(PluginSlot slot, string className)
    {
        var commandId = Slots.DispatchCommandId;
        if (commandId is null) return false;

        _pendingDispatch = (slot.Index, className);

        try
        {
            RevitContext.UiApplication.PostCommand(RevitCommandId.LookupCommandId(commandId));
            return true;
        }
        catch (Exception exception)
        {
            _pendingDispatch = null;
            Log.Warning(exception, "Không post được lệnh điều phối cho '{Id}' — Revit đang bận?", slot.Id);
            return false;
        }
    }

    /// <summary>Chạy command mà <see cref="DispatchApplicationCommand"/> đã đặt, từ assembly mới nhất của plugin.</summary>
    public Result ExecuteDispatched(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var pending = _pendingDispatch;
        _pendingDispatch = null;
        if (pending is null) return Result.Cancelled;

        var slot = Slots.Slots[pending.Value.Slot];
        if (slot.IsFree) return Result.Cancelled;

        try
        {
            return _loader.CreateCommand(slot, pending.Value.ClassName).Execute(data, ref message, elements);
        }
        catch (Exception exception)
        {
            slot.LastError = exception.ToString();
            message = exception.Message;

            Log.Error(exception, "Chạy '{Class}' của '{Id}' thất bại", pending.Value.ClassName, slot.Id);
            return Result.Failed;
        }
    }

    private void TryStartApplication(PluginSlot slot, PluginEntry entry, UIControlledApplication application)
    {
        try
        {
            _loader.StartApplication(slot, entry, application);
            RedirectCommands(slot, application);
        }
        catch (Exception exception)
        {
            slot.Status = PluginStatus.Error;
            slot.LastError = exception.ToString();
            Log.Error(exception, "Khởi động plugin '{Id}' (Kind=Application) thất bại", entry.Id);
        }
    }

    private static void Bind(PluginSlot slot, PluginEntry entry)
    {
        slot.Entry = entry;
        slot.Id = entry.Id;
        slot.DllPath = entry.DllPath;
        slot.ButtonText = entry.ButtonText ?? entry.Id;
        slot.AutoReload = entry.AutoReload;
        slot.Kind = string.Equals(entry.Kind, nameof(PluginKind.Application), StringComparison.OrdinalIgnoreCase)
            ? PluginKind.Application
            : PluginKind.Command;
        slot.Status = PluginStatus.Idle;
        slot.LastError = null;
    }

    private static void Release(PluginSlot slot)
    {
        slot.Entry = null;
        slot.Id = string.Empty;
        slot.DllPath = string.Empty;
        slot.ButtonText = string.Empty;
        slot.LastError = null;
        slot.Status = PluginStatus.Removed;
    }

    private static PluginEntry EntryOf(PluginSlot slot) => slot.Entry ?? new PluginEntry { Id = slot.Id, DllPath = slot.DllPath };

    private PluginSlot? ResolveSlot(int index) => index >= 0 && index < SlotPool.MaxSlots ? Slots.Slots[index] : null;

    private void SaveConfig()
    {
        var entries = new List<PluginEntry>();

        foreach (var slot in Slots.ActiveSlots())
        {
            var entry = EntryOf(slot);
            entry.AutoReload = slot.AutoReload;
            entry.ButtonText = slot.ButtonText;
            entries.Add(entry);
        }

        _config.Save(entries);
    }
}
