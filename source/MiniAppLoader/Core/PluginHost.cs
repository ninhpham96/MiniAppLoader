using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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
    private static PluginHost? _current;

    /// <summary>Instance của phiên Revit hiện tại.</summary>
    public static PluginHost Current =>
        _current ?? throw new InvalidOperationException(
            "PluginHost chưa được khởi tạo — Application.OnStartup phải chạy trước.");

    public SlotPool Slots { get; } = new();

    /// <summary>ID của dockable pane Plugin Hub. Cố định để Revit nhớ được vị trí neo.</summary>
    public static readonly DockablePaneId HubPaneId = new(new Guid("6F8E1B42-2C55-4A7E-93B6-9C2D1A5E4F30"));

    internal static void Initialize() => _current = new PluginHost();

    /// <summary>
    ///     TẠM THỜI (Phase 2): nạp vài slot giả để kiểm chứng trong Revit thật rằng
    ///     card layout render đúng, nút ribbon ẩn/hiện được, và stack panel không để lỗ
    ///     hổng khi một phần nút đang ẩn. Phase 3 thay bằng ConfigStore đọc plugins.json.
    /// </summary>
    internal void SeedDemoSlots()
    {
        var demo = new[]
        {
            ("SamplePlugin", @"D:\dev\SamplePluginin\Debug
et8.0-windows\SamplePlugin.dll", PluginKind.Command, PluginStatus.Loaded),
            ("Damper.Revit.App", @"D:\dev\Damper\Outcome\Damper.Revit.App.dll", PluginKind.Application, PluginStatus.Pending),
            ("BrokenPlugin", @"D:\dev\Brokenin\Broken.dll", PluginKind.Command, PluginStatus.Error)
        };

        foreach (var (id, path, kind, status) in demo)
        {
            var slot = Slots.TakeFreeSlot();
            if (slot is null) break;

            slot.Id = id;
            slot.DllPath = path;
            slot.ButtonText = id;
            slot.Kind = kind;
            slot.Status = status;
            if (status == PluginStatus.Error) slot.LastError = "Demo: chưa nạp được DLL (dữ liệu giả của Phase 2).";

            // Kind=Application tự dựng ribbon riêng nên KHÔNG chiếm nút trên panel Plugins.
            if (kind == PluginKind.Command) Slots.ShowButton(slot);
        }
    }

    internal static void Shutdown() => _current = null;

    /// <summary>
    ///     Nút ribbon của một plugin được bấm: reload DLL rồi chuyển tiếp
    ///     <c>Execute</c> sang command thật của plugin.
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

        // Phase 3 sẽ thay bằng reload + Activator.CreateInstance command thật của plugin.
        Log.Information("Slot {Index} ({Id}) được bấm", index, slot.Id);
        TaskDialog.Show("MiniAppLoader", $"Slot {index:D2} — '{slot.Id}'.\nLõi loader sẽ được nối ở Phase 3.");
        return Result.Succeeded;
    }

    /// <summary>
    ///     Chạy plugin từ Plugin Hub, đi qua đúng pipeline command của Revit.
    ///     <para>
    ///         <c>PostCommand</c> đẩy lệnh vào hàng đợi để Revit tự dựng
    ///         <c>ExternalCommandData</c> hợp lệ rồi gọi <c>Execute</c> — y hệt khi người
    ///         dùng bấm chuột. KHÔNG được thay bằng cách giữ lại một
    ///         <c>ExternalCommandData</c> cũ: nó là wrapper quanh state native chỉ sống
    ///         trong đúng một lần invoke, dùng lại sẽ giết Revit bằng access violation chứ
    ///         không ném exception bắt được.
    ///     </para>
    /// </summary>
    public bool TryRunSlot(PluginSlot slot)
    {
        var commandId = Slots.GetCommandId(slot.Index);
        if (commandId is null)
        {
            Log.Warning("Slot {Index}: không tra được RevitCommandId, không Run từ Hub được", slot.Index);
            return false;
        }

        try
        {
            RevitContext.UiApplication.PostCommand(RevitCommandId.LookupCommandId(commandId));
            return true;
        }
        catch (Exception exception)
        {
            // Revit ném InvalidOperationException nếu đang có command khác chạy dở.
            Log.Warning(exception, "Không post được lệnh cho slot {Index}", slot.Index);
            return false;
        }
    }
}
