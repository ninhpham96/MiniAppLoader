using System.IO;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SamplePlugin;

/// <summary>
///     Plugin mẫu tối giản để kiểm chứng hot-reload của MiniAppLoader.
///     <para>
///         Cố ý GHI RA FILE thay vì bật <c>TaskDialog</c>: dialog là modal, nó sẽ chặn
///         Revit và khiến mọi kiểm chứng tự động (MCP) treo. Ghi file thì đọc lại được và so
///         sánh được giá trị trước/sau — "không có exception" không chứng minh được gì.
///     </para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SampleCommand : IExternalCommand
{
    /// <summary>Đổi hằng số này rồi build lại để xác nhận loader thật sự nạp bản mới.</summary>
    private const string Version = "VERSION 4 - hot reload trong cung 1 phien Revit";

    public static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "sample-plugin-run.log");

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);

        var assembly = typeof(SampleCommand).Assembly;
        File.AppendAllText(MarkerPath,
            $"{DateTime.Now:HH:mm:ss.fff} {Version} " +
            $"doc={commandData.Application.ActiveUIDocument?.Document.Title ?? "<none>"} " +
            $"location={assembly.Location}{Environment.NewLine}");

        return Result.Succeeded;
    }
}
