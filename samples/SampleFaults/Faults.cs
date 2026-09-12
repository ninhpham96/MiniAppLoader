using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SampleFaults;

internal static class Marker
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "sample-faults.log");

    public static void Write(string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
    }
}

/// <summary>
///     Ném exception ngay trong <c>Execute</c>. Loader phải bắt được, ghi vào
///     <c>slot.LastError</c>, đặt trạng thái Error — và Revit phải sống sót.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ThrowingCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Marker.Write("ThrowingCommand sap nem");
        throw new InvalidOperationException("Lỗi cố ý từ ThrowingCommand để thử nhánh xử lý lỗi của loader.");
    }
}

/// <summary>
///     Class thứ hai trong cùng DLL — để kiểm chứng <c>commandClassName</c> chọn đúng class
///     thay vì lấy bừa class đầu tiên reflection tìm thấy.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SecondCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Marker.Write("SecondCommand da chay (commandClassName hoat dong)");
        return Result.Succeeded;
    }
}
