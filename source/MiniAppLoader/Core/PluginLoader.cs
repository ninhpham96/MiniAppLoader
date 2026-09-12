using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using MiniAppLoader.Core.Runtime;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>
///     Hợp đồng tuỳ chọn cho plugin muốn TỰ chạy lại sau mỗi lần build (auto-reload).
///     Chỉ cần <see cref="UIApplication"/> — thứ duy nhất có sẵn trong
///     <c>IExternalEventHandler</c>.
/// </summary>
public interface IHotCommand
{
    void Execute(UIApplication application);
}

/// <summary>
///     Nạp / gỡ / khởi tạo entry point của plugin. Đây là nơi duy nhất đụng tới
///     <see cref="IPluginRuntime"/>; phần còn lại của loader không cần biết đang chạy trên
///     .NET Framework hay .NET Core.
/// </summary>
internal sealed class PluginLoader
{
#if NET8_0_OR_GREATER
    private readonly IPluginRuntime _runtime = new AlcPluginRuntime();
#else
    private readonly IPluginRuntime _runtime = new NetFxPluginRuntime();
#endif

    private readonly Dictionary<int, Assembly> _assemblies = [];

    /// <summary>
    ///     Instance <c>IExternalApplication</c> đang chạy của các slot Kind=Application, cùng
    ///     bản <see cref="UIControlledApplication"/> đã trao cho nó.
    ///     <para>
    ///         Phải giữ ĐÚNG một instance <c>UIControlledApplication</c> cho suốt đời một
    ///         plugin: <c>OnStartup</c> và <c>OnShutdown</c> của nó phải nhận cùng một
    ///         object, nếu không lệnh huỷ đăng ký sự kiện bên trong plugin sẽ nhắm vào
    ///         instance khác và để lại delegate treo vào assembly đã gỡ.
    ///     </para>
    /// </summary>
    private readonly Dictionary<int, (IExternalApplication Instance, UIControlledApplication Application)> _applications = [];

    /// <summary>Nạp lại DLL của slot. Ném nếu file hỏng/không đọc được — phía gọi ghi vào slot.LastError.</summary>
    public Assembly Reload(PluginSlot slot)
    {
        var stopwatch = Stopwatch.StartNew();

        // Phân biệt "không có file" với "file đang bị khoá" TRƯỚC khi chờ: FileNotFoundException
        // là con của IOException, nên nếu không tách ra thì vòng chờ sẽ thử 20 lần một file
        // không tồn tại rồi báo "đang bị ghi/khoá" — sai hoàn toàn, và đây là ca rất hay gặp
        // (đổi nhánh, clean solution, sửa dllPath lệch đường dẫn).
        if (!File.Exists(slot.DllPath))
        {
            throw new FileNotFoundException(
                $"Không tìm thấy '{slot.DllPath}'. Plugin đã được build chưa, hay đường dẫn trong " +
                "plugins.json đã cũ?", slot.DllPath);
        }

        if (!ShadowCopy.WaitUntilReadable(slot.DllPath))
        {
            throw new IOException($"'{slot.DllPath}' vẫn đang bị ghi/khoá sau nhiều lần thử — bỏ qua lần reload này.");
        }

        // PHẢI nhả tham chiếu tới assembly cũ TRƯỚC khi unload: một object Assembly giữ
        // sống chính AssemblyLoadContext sinh ra nó, nên chỉ cần dictionary này còn trỏ tới
        // bản cũ là ctx.Unload() + GC.Collect không bao giờ thu được — slot sẽ báo "Leaked"
        // dù plugin hoàn toàn sạch. Đã gặp thật khi test trên Revit 2026.
        _assemblies.Remove(slot.Index);

        _runtime.Unload(slot.Index);

        Assembly assembly;
        try
        {
            assembly = _runtime.Load(slot);
        }
        catch (BadImageFormatException exception)
        {
            // Thông báo gốc chỉ vào bản SHADOW ("…\~shadow\slot3.dll") — một file người dùng
            // chưa từng nghe tới, đi tìm cũng không hiểu gì. Nói tên DLL mà HỌ khai báo.
            throw new BadImageFormatException(
                $"'{slot.DllPath}' không phải assembly .NET hợp lệ (file hỏng, hoặc sai kiến trúc " +
                "x86/x64, hoặc thật ra là file khác bị đặt đuôi .dll).", slot.DllPath, exception);
        }

        _assemblies[slot.Index] = assembly;

        stopwatch.Stop();
        slot.LastLoadedAt = DateTime.Now;
        slot.LastLoadDuration = stopwatch.Elapsed;
        slot.Status = PluginStatus.Loaded;
        slot.LastError = null;

        // Kiểm tra LƯỜI, sau khi đã nạp xong: đếm xem bản cũ nào còn chưa được GC thu hồi.
        // Không ép GC ở đây — xem AlcPluginRuntime để biết vì sao.
        slot.StaleLoads = _runtime.CountStaleLoads(slot.Index);

        Log.Information("Đã nạp '{Id}' trong {Ms} ms{Stale}", slot.Id, stopwatch.ElapsedMilliseconds,
            slot.StaleLoads > 0 ? $" ({slot.StaleLoads} bản cũ chưa thu hồi)" : string.Empty);
        return assembly;
    }

    /// <summary>Tạo instance <c>IExternalCommand</c> của plugin từ assembly đang nạp.</summary>
    public IExternalCommand CreateCommand(PluginSlot slot, string? preferredClassName)
    {
        var assembly = _assemblies[slot.Index];
        var type = FindEntryPoint(assembly, typeof(IExternalCommand), preferredClassName)
                   ?? throw new InvalidOperationException(
                       $"Không tìm thấy class nào implement IExternalCommand trong {assembly.GetName().Name}.");

        return (IExternalCommand)Activator.CreateInstance(type)!;
    }

    /// <summary>Instance <see cref="IHotCommand"/> nếu plugin có, để tự chạy lại sau build.</summary>
    public IHotCommand? CreateHotCommand(PluginSlot slot)
    {
        var type = FindEntryPoint(_assemblies[slot.Index], typeof(IHotCommand), preferredClassName: null);
        return type is null ? null : (IHotCommand)Activator.CreateInstance(type)!;
    }

    /// <summary>
    ///     Nạp và gọi <c>OnStartup</c> cho plugin <c>Kind=Application</c>, đồng thời chụp ảnh
    ///     ribbon trước/sau để biết plugin đã thêm những gì.
    /// </summary>
    public void StartApplication(PluginSlot slot, PluginEntry entry, UIControlledApplication controlled)
    {
        Reload(slot);

        var assembly = _assemblies[slot.Index];
        var type = FindEntryPoint(assembly, typeof(IExternalApplication), entry.CommandClassName)
                   ?? throw new InvalidOperationException(
                       $"Không tìm thấy class nào implement IExternalApplication trong {assembly.GetName().Name}.");

        var instance = (IExternalApplication)Activator.CreateInstance(type)!;

        var before = RibbonDiff.Snapshot();
        instance.OnStartup(controlled);
        _ribbonSnapshots[slot.Index] = (before, RibbonDiff.Snapshot());

        _applications[slot.Index] = (instance, controlled);
    }

    /// <summary>
    ///     Gọi <c>OnShutdown</c> và (tuỳ chọn) gỡ ribbon plugin đã tạo. Không ném: đang trên
    ///     đường reload hoặc đang đóng Revit, nuốt lỗi tốt hơn là kẹt giữa chừng.
    /// </summary>
    /// <param name="removeRibbon">
    ///     Chỉ đúng khi RELOAD hoặc GỠ plugin — lúc đó ribbon cũ phải biến mất để bản mới dựng
    ///     lại được. Lúc ĐÓNG Revit thì bỏ qua: Revit đang tự tháo dỡ UI của nó, và
    ///     <c>ComponentManager.Ribbon</c> có thể đã null, nên gỡ vừa vô nghĩa vừa ném
    ///     NullReferenceException vào log mỗi lần thoát.
    /// </param>
    public void StopApplication(PluginSlot slot, PluginEntry entry, bool removeRibbon = true)
    {
        if (!_applications.TryGetValue(slot.Index, out var running)) return;

        try
        {
            running.Instance.OnShutdown(running.Application);
        }
        catch (Exception exception)
        {
            slot.LastError = "OnShutdown của plugin ném lỗi: " + exception.Message;
            Log.Warning(exception, "OnShutdown của '{Id}' ném lỗi", slot.Id);
        }

        if (removeRibbon && _ribbonSnapshots.TryGetValue(slot.Index, out var snapshot))
        {
            RibbonDiff.RemoveAdded(running.Application, snapshot.Before, snapshot.After,
                entry.RibbonTabsToRemove, entry.RibbonPanelsToRemove);
            _ribbonSnapshots.Remove(slot.Index);
        }

        _applications.Remove(slot.Index);
    }

    /// <summary>Gỡ hẳn slot (khi người dùng bấm Gỡ, hoặc lúc Revit đóng).</summary>
    public UnloadResult Unload(PluginSlot slot)
    {
        _assemblies.Remove(slot.Index);
        return _runtime.Unload(slot.Index);
    }

    /// <summary>Ảnh chụp ribbon trước/sau <c>OnStartup</c> của từng slot Kind=Application.</summary>
    private readonly Dictionary<int, (RibbonSnapshot Before, RibbonSnapshot After)> _ribbonSnapshots = [];

    /// <summary>
    ///     Tìm class cụ thể implement <paramref name="contract"/>. Ưu tiên
    ///     <paramref name="preferredClassName"/> khi DLL có nhiều ứng viên.
    /// </summary>
    private static Type? FindEntryPoint(Assembly assembly, Type contract, string? preferredClassName)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Vài type không nạp được (thường do thiếu dependency) -> vẫn dùng những type
            // nạp thành công thay vì hỏng cả lần nạp.
            types = exception.Types.Where(type => type is not null).ToArray()!;
        }

        var candidates = types
            .Where(type => contract.IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .ToList();

        if (!string.IsNullOrEmpty(preferredClassName))
        {
            return candidates.FirstOrDefault(type => type.FullName == preferredClassName) ?? candidates.FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }
}
