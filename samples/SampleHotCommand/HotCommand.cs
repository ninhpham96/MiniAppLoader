using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MiniAppLoader.Core;

namespace SampleHotCommand;

/// <summary>
///     Plugin mẫu implement CẢ <see cref="IExternalCommand"/> LẪN <see cref="IHotCommand"/>.
///     <para>
///         <see cref="IExternalCommand.Execute"/> chạy khi bấm nút trên ribbon.
///         <see cref="IHotCommand.Execute"/> chạy TỰ ĐỘNG ngay sau mỗi lần loader nạp lại
///         DLL này — đó là vòng lặp "sửa code, build, thấy kết quả" không cần chạm chuột.
///     </para>
///     <para>
///         Mỗi lần chạy ghi thêm một dòng vào <see cref="MarkerPath"/> kèm nguồn gọi
///         (<c>hot</c> hay <c>button</c>), nên phân biệt được tự chạy với bấm tay — nếu chỉ
///         đếm số dòng thì không chứng minh được gì.
///     </para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public class HotCommand : IExternalCommand, IHotCommand
{
    /// <summary>Đổi hằng số này rồi build lại để xác nhận loader thật sự nạp bản mới.</summary>
    private const string Version = "HOT 1";

    public static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "sample-hotcommand.log");

    /// <summary>Bấm nút trên ribbon.</summary>
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Write("button", commandData.Application.ActiveUIDocument?.Document.Title);
        return Result.Succeeded;
    }

    /// <summary>Loader gọi ngay sau khi nạp lại DLL — không ai bấm gì cả.</summary>
    public void Execute(UIApplication application)
    {
        Write("hot", application.ActiveUIDocument?.Document.Title);
    }

    private static void Write(string source, string? document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);

        File.AppendAllText(MarkerPath,
            $"{DateTime.Now:HH:mm:ss.fff} {Version} source={source} " +
            $"tfm={GetTargetFramework()} " +
            $"doc={document ?? "<none>"} " +
            $"location={typeof(HotCommand).Assembly.Location}{Environment.NewLine}");
    }

    private static string GetTargetFramework()
    {
#if NETFRAMEWORK
        return "net48";
#else
        return "net8.0-windows";
#endif
    }
}
