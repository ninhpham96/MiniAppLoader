using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniAppLoader.Core;
using Serilog;

namespace MiniAppLoader.ViewModels;

/// <summary>
///     ViewModel của dockable pane "Plugin Hub".
///     <para>
///         Được dựng trong <c>OnStartup</c>, TRƯỚC khi có document nào — vì Revit chỉ cho
///         <c>RegisterDockablePane</c> ở đó. Vì vậy constructor tuyệt đối không được gọi API
///         Revit nào.
///     </para>
/// </summary>
public sealed partial class PluginHubViewModel : ObservableObject
{
    private readonly PluginHost _host;

    public PluginHubViewModel(PluginHost host, HubLogSink logSink)
    {
        _host = host;
        Log = logSink.Entries;
        LogSink = logSink;

        // CollectionViewSource lọc tại chỗ: slot trống và slot không khớp ô tìm kiếm bị ẩn,
        // nhưng danh sách nguồn vẫn nguyên index nên nút ribbon không bị lệch slot.
        VisibleSlots = CollectionViewSource.GetDefaultView(host.Slots.Slots);
        VisibleSlots.Filter = OnFilter;
    }

    public ICollectionView VisibleSlots { get; }
    public ReadOnlyObservableCollection<HubLogEntry> Log { get; }
    private HubLogSink LogSink { get; }

    /// <summary>Nút Run chỉ dùng được khi tra được ID lệnh nội bộ của mọi nút ribbon.</summary>
    public bool CanRunFromHub => _host.Slots.CanPostCommands;

    /// <summary>Lý do hiển thị trên tooltip khi <see cref="CanRunFromHub"/> là false.</summary>
    public string RunUnavailableReason =>
        "Không tra được ID lệnh ribbon trên version Revit này, nên Hub không bấm hộ nút được. " +
        "Bấm trực tiếp nút của plugin trên panel Plugins.";

    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => VisibleSlots.Refresh();

    public bool HasPlugins => _host.Slots.ActiveSlots().Any();

    [RelayCommand]
    private void Run(PluginSlot? slot)
    {
        if (slot is null) return;
        if (!_host.TryRunSlot(slot)) Log_Warning(slot);
    }

    [RelayCommand]
    private void Reload(PluginSlot? slot)
    {
        if (slot is null) return;
        Serilog.Log.Information("Reload thủ công: {Id} (lõi loader nối ở Phase 3)", slot.Id);
    }

    [RelayCommand]
    private void Remove(PluginSlot? slot)
    {
        if (slot is null) return;
        Serilog.Log.Information("Gỡ plugin: {Id} (lõi loader nối ở Phase 3)", slot.Id);
    }

    [RelayCommand]
    private void AddPlugin() => Serilog.Log.Information("Thêm plugin (lõi loader nối ở Phase 3)");

    [RelayCommand]
    private void ReloadAll() => Serilog.Log.Information("Reload toàn bộ (lõi loader nối ở Phase 3)");

    [RelayCommand]
    private void ClearLog() => LogSink.Clear();

    private static void Log_Warning(PluginSlot slot) =>
        Serilog.Log.Warning("Không chạy được '{Id}' từ Hub — Revit đang bận hoặc không tra được ID lệnh", slot.Id);

    private bool OnFilter(object item)
    {
        if (item is not PluginSlot slot || slot.IsFree) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return slot.Id.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase)
               || slot.DllPath.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase);
    }
}
