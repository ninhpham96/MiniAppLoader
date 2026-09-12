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
        foreach (var directory in entries.Select(entry => Path.GetDirectoryName(entry.DllPath)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ShadowCopy.CleanupDirectory(directory);
        }

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
            _loader.Reload(slot);
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

        try
        {
            // Nạp thử để biết DLL có hợp lệ và có entry point hay không, TRƯỚC khi ghi vào
            // config — tránh để lại entry hỏng mà lần mở Revit sau vẫn cố nạp.
            _loader.Reload(slot);
            _loader.CreateCommand(slot, preferredClassName: null);
        }
        catch (Exception exception)
        {
            Release(slot);
            Log.Error(exception, "Không nạp được '{Path}'", dllPath);
            return (false, exception.Message);
        }

        Slots.ShowButton(slot);
        _pipeline.Watch(slot);
        SaveConfig();

        Log.Information("Đã thêm plugin '{Id}' vào slot {Slot}", id, slot.Index);
        return (true, $"Đã thêm '{id}' — nút đã có trên panel Plugins.");
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
            _loader.StopApplication(slot, entry);
            _loader.StartApplication(slot, entry, application.AsControlledApplication());
            return;
        }

        _loader.Reload(slot);

        // Plugin nào implement IHotCommand thì tự chạy lại luôn sau build — vòng lặp
        // sửa-code / thấy-kết-quả không cần chạm chuột.
        _loader.CreateHotCommand(slot)?.Execute(application);
    }

    private void TryStartApplication(PluginSlot slot, PluginEntry entry, UIControlledApplication application)
    {
        try
        {
            _loader.StartApplication(slot, entry, application);
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
