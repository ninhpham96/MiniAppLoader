using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;
using Serilog;
using ExternalEvent = Nice3point.Revit.Toolkit.External.ExternalEvent;

namespace MiniAppLoader.Core;

/// <summary>
///     Theo dõi file DLL của các plugin và đẩy yêu cầu reload về đúng thread của Revit.
///     <para>
///         <b>Một ExternalEvent duy nhất + tập "bẩn", không phải một handler cho mỗi slot.</b>
///         <c>ExternalEvent.Raise()</c> của Revit có tính GỘP: raise hai lần trước khi Revit
///         kịp xử lý thì chỉ có MỘT lần <c>Execute</c>. Bản v1 dùng một handler riêng cho mỗi
///         slot nên khi bạn build hai plugin trong cùng một lần MSBuild, một trong hai lần
///         reload bị nuốt mất. Ở đây watcher chỉ ghi slot vào
///         <see cref="_dirty"/> rồi raise; <c>Execute</c> vét sạch tập đó, nên gộp bao nhiêu
///         lần cũng không mất việc.
///     </para>
///     <para>
///         <b>Luật threading:</b> callback của <see cref="FileSystemWatcher"/> chạy trên
///         ThreadPool và chỉ được phép chạm vào <see cref="_dirty"/> + <c>Raise()</c>. Mọi
///         thứ khác (state của slot, Revit API) nằm trong <see cref="Drain"/>, vốn luôn được
///         Revit gọi trên main thread.
///     </para>
/// </summary>
internal sealed class ReloadPipeline : IDisposable
{
    /// <summary>
    ///     Một lần build ghi DLL nhiều nhịp liên tiếp. 500 ms thay vì 300 ms của bản v1, và
    ///     dù sao <see cref="Runtime.ShadowCopy.WaitUntilReadable"/> vẫn còn chờ mở được file
    ///     — chỉ debounce theo thời gian là không đủ.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<int, List<FileSystemWatcher>> _watchers = [];
    private readonly Dictionary<int, DateTime> _lastRaised = [];
    private readonly ConcurrentDictionary<int, byte> _dirty = [];
    private readonly Action<PluginSlot, UIApplication> _onReload;
    private readonly Func<int, PluginSlot?> _resolveSlot;
    private readonly ExternalEvent _reloadEvent;

    public ReloadPipeline(Func<int, PluginSlot?> resolveSlot, Action<PluginSlot, UIApplication> onReload)
    {
        _resolveSlot = resolveSlot;
        _onReload = onReload;

        // Phải tạo trên main thread lúc khởi động — đó là ràng buộc của Revit ExternalEvent.
        _reloadEvent = new ExternalEvent(Drain);
    }

    /// <summary>Bắt đầu theo dõi DLL của slot. Gọi lại được: tự dừng watcher cũ trước.</summary>
    public void Watch(PluginSlot slot)
    {
        Unwatch(slot.Index);

        var directory = Path.GetDirectoryName(slot.DllPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            // Hay gặp khi plugin chưa build lần nào, hoặc vừa clean solution. Không tự hồi
            // phục được vì FileSystemWatcher cần một thư mục có thật, nên nói rõ cách xử lý.
            Log.Warning("Không theo dõi được '{Id}': chưa có thư mục '{Dir}'. Build plugin rồi bấm " +
                        "\"Nạp lại\" trong Plugin Hub để bật lại theo dõi.", slot.Id, directory);
            return;
        }

        var watchers = new List<FileSystemWatcher>();

        // Theo dõi cả .deps.json: trên .NET Core nó quyết định dependency nào được phân giải,
        // và MSBuild có thể ghi nó SAU file .dll.
        foreach (var fileName in new[] { Path.GetFileName(slot.DllPath), Path.GetFileNameWithoutExtension(slot.DllPath) + ".deps.json" })
        {
            var watcher = new FileSystemWatcher(directory!, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, _) => OnFileTouched(slot.Index);
            watcher.Created += (_, _) => OnFileTouched(slot.Index);
            watcher.Renamed += (_, _) => OnFileTouched(slot.Index);

            watchers.Add(watcher);
        }

        _watchers[slot.Index] = watchers;
    }

    public void Unwatch(int slotIndex)
    {
        if (!_watchers.Remove(slotIndex, out var watchers)) return;

        foreach (var watcher in watchers) watcher.Dispose();
        _dirty.TryRemove(slotIndex, out _);
    }

    /// <summary>Yêu cầu reload thủ công (nút "Nạp lại" trong Plugin Hub).</summary>
    public void RequestReload(int slotIndex)
    {
        _dirty[slotIndex] = 0;
        _reloadEvent.Raise();
    }

    public void Dispose()
    {
        foreach (var watchers in _watchers.Values)
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    /// <summary>Chạy trên ThreadPool — TUYỆT ĐỐI không đụng Revit API hay state của slot ở đây.</summary>
    private void OnFileTouched(int slotIndex)
    {
        lock (_lastRaised)
        {
            var now = DateTime.UtcNow;
            if (_lastRaised.TryGetValue(slotIndex, out var previous) && now - previous < Debounce) return;
            _lastRaised[slotIndex] = now;
        }

        _dirty[slotIndex] = 0;
        _reloadEvent.Raise();
    }

    /// <summary>Chạy trên main thread của Revit, khi không có command/edit mode nào đang hoạt động.</summary>
    private void Drain(UIApplication application)
    {
        foreach (var slotIndex in _dirty.Keys)
        {
            if (!_dirty.TryRemove(slotIndex, out _)) continue;

            var slot = _resolveSlot(slotIndex);
            if (slot is null || slot.IsFree) continue;

            try
            {
                _onReload(slot, application);
            }
            catch (Exception exception)
            {
                // Một plugin hỏng không được phép chặn các plugin còn lại trong cùng lượt vét.
                slot.Status = PluginStatus.Error;
                slot.LastError = exception.ToString();
                Log.Error(exception, "Reload '{Id}' thất bại", slot.Id);
            }
        }
    }
}
