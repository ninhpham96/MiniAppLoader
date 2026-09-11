using System;
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
    private readonly HubLogSink _logSink;

    public PluginHubViewModel(PluginHost host, HubLogSink logSink)
    {
        _host = host;
        _logSink = logSink;
        Log = logSink.Entries;

        // CollectionViewSource lọc tại chỗ: slot trống và slot không khớp ô tìm kiếm bị ẩn,
        // nhưng danh sách nguồn vẫn nguyên index nên nút ribbon không bị lệch slot.
        VisibleSlots = CollectionViewSource.GetDefaultView(host.Slots.Slots);
        VisibleSlots.Filter = OnFilter;

        foreach (var slot in host.Slots.Slots) slot.PropertyChanged += OnSlotChanged;
    }

    public ICollectionView VisibleSlots { get; }
    public ReadOnlyObservableCollection<HubLogEntry> Log { get; }

    /// <summary>Nút Chạy chỉ dùng được khi tra được ID lệnh nội bộ của mọi nút ribbon.</summary>
    public bool CanRunFromHub => _host.Slots.CanPostCommands;

    /// <summary>Giải thích hiện trên tooltip khi <see cref="CanRunFromHub"/> là false.</summary>
    public string RunUnavailableReason =>
        "Không tra được ID lệnh ribbon trên version Revit này, nên Hub không bấm hộ nút được. " +
        "Bấm trực tiếp nút của plugin trên panel Plugins.";

    public bool HasPlugins => _host.Slots.ActiveSlots().Any();

    public string ConfigPath => _host.ConfigPath;

    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => VisibleSlots.Refresh();

    /// <summary>Thêm một DLL làm plugin — dùng chung cho nút "+ Thêm" và cho kéo-thả file.</summary>
    public void AddPlugin(string dllPath)
    {
        var (success, message) = _host.AddPlugin(dllPath);

        if (success) Serilog.Log.Information("{Message}", message);
        else Serilog.Log.Warning("{Message}", message);

        Refresh();
    }

    [RelayCommand]
    private void Run(PluginSlot? slot)
    {
        if (slot is null) return;

        if (!_host.TryRunSlot(slot))
        {
            Serilog.Log.Warning("Không chạy được '{Id}' từ Hub — Revit đang bận, hoặc bấm thẳng nút trên ribbon", slot.Id);
        }
    }

    [RelayCommand]
    private void Reload(PluginSlot? slot)
    {
        if (slot is null) return;
        _host.ReloadPlugin(slot);
    }

    [RelayCommand]
    private void Remove(PluginSlot? slot)
    {
        if (slot is null) return;

        _host.RemovePlugin(slot);
        Refresh();
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "MiniAppLoader — chọn DLL plugin",
            Filter = "Assembly (*.dll)|*.dll",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true) AddPlugin(dialog.FileName);
    }

    [RelayCommand]
    private void ReloadAll() => _host.ReloadAll();

    [RelayCommand]
    private void ClearLog() => _logSink.Clear();

    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_host.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Không mở được {Path}", _host.ConfigPath);
        }
    }

    private void Refresh()
    {
        VisibleSlots.Refresh();
        OnPropertyChanged(nameof(HasPlugins));
    }

    private void OnSlotChanged(object? sender, PropertyChangedEventArgs args)
    {
        // Slot được gán hoặc được trả về pool -> khối "chưa có plugin nào" và bộ lọc phải
        // tính lại. Các property khác (Status, LastError…) tự binding, không cần đụng.
        if (sender is not PluginSlot slot) return;

        if (args.PropertyName == nameof(PluginSlot.DllPath)) Refresh();

        // Checkbox "auto" bind thẳng vào slot, nên bật/tắt watcher phải bám theo đây.
        if (args.PropertyName == nameof(PluginSlot.AutoReload) && !slot.IsFree)
        {
            _host.SetAutoReload(slot, slot.AutoReload);
        }
    }

    private bool OnFilter(object item)
    {
        if (item is not PluginSlot slot || slot.IsFree) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return slot.Id.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0
               || slot.DllPath.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
