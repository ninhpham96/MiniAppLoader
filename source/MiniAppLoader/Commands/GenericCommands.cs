using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MiniAppLoader.Core;

namespace MiniAppLoader.Commands;

// Pool cố định các IExternalCommand mà Revit phân giải qua tên class lúc tạo nút ribbon.
// Revit không cho truyền tham số vào class này, nên mỗi nút cần đúng một class riêng —
// đây là cách chuẩn để một loader duy nhất phục vụ N plugin.
//
// FILE NÀY SINH TỰ ĐỘNG theo SlotPool.MaxSlots (xem tools/gen-generic-commands.py).
// Cần nhiều slot hơn: đổi MaxSlots rồi chạy lại script.

[Transaction(TransactionMode.Manual)]
public class GenericCommand00 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(0, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand01 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(1, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand02 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(2, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand03 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(3, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand04 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(4, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand05 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(5, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand06 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(6, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand07 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(7, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand08 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(8, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand09 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(9, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand10 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(10, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand11 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(11, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand12 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(12, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand13 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(13, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand14 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(14, data, ref message, elements);
}

[Transaction(TransactionMode.Manual)]
public class GenericCommand15 : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => PluginHost.Current.ExecuteSlot(15, data, ref message, elements);
}

/// <summary>
///     Thêm nút của một slot vào stack panel.
///     <para>
///         Cần bảng switch tường minh vì <c>IRibbonStackPanel.AddPushButton</c> chỉ có
///         overload GENERIC — không có overload nhận <see cref="System.Type"/> lúc chạy như
///         <c>RibbonPanel.AddPushButton</c>. Bù lại, compiler kiểm tra được là số case luôn
///         khớp số class ở trên.
///     </para>
/// </summary>
internal static class GenericCommandButtons
{
    public static PushButton Add(IRibbonStackPanel stack, int index, string buttonText)
        => index switch
        {
            0 => stack.AddPushButton<GenericCommand00>(buttonText),
            1 => stack.AddPushButton<GenericCommand01>(buttonText),
            2 => stack.AddPushButton<GenericCommand02>(buttonText),
            3 => stack.AddPushButton<GenericCommand03>(buttonText),
            4 => stack.AddPushButton<GenericCommand04>(buttonText),
            5 => stack.AddPushButton<GenericCommand05>(buttonText),
            6 => stack.AddPushButton<GenericCommand06>(buttonText),
            7 => stack.AddPushButton<GenericCommand07>(buttonText),
            8 => stack.AddPushButton<GenericCommand08>(buttonText),
            9 => stack.AddPushButton<GenericCommand09>(buttonText),
            10 => stack.AddPushButton<GenericCommand10>(buttonText),
            11 => stack.AddPushButton<GenericCommand11>(buttonText),
            12 => stack.AddPushButton<GenericCommand12>(buttonText),
            13 => stack.AddPushButton<GenericCommand13>(buttonText),
            14 => stack.AddPushButton<GenericCommand14>(buttonText),
            15 => stack.AddPushButton<GenericCommand15>(buttonText),
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(index), index, $"Chỉ có 16 slot; SlotPool.MaxSlots và GenericCommands.cs bị lệch nhau.")
        };
}
