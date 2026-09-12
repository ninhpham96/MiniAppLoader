using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SamplePlugin;

/// <summary>
///     Plugin mẫu tối giản để kiểm chứng hot-reload của MiniAppLoader.
///     <para>
///         Cố ý GHI RA FILE thay vì bật <c>TaskDialog</c>: dialog là modal, nó sẽ chặn Revit
///         và khiến mọi kiểm chứng tự động (MCP) treo. Ghi file thì đọc lại được và so sánh
///         được giá trị trước/sau — "không có exception" không chứng minh được gì.
///     </para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SampleCommand : IExternalCommand
{
    /// <summary>Đổi hằng số này rồi build lại để xác nhận loader thật sự nạp bản mới.</summary>
    private const string Version = "VERSION 6 - alc isolation";

    public static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "sample-plugin-run.log");

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);

        var assembly = typeof(SampleCommand).Assembly;

        // Chạm vào Newtonsoft.Json — dependency NuGet RIÊNG của plugin, MiniAppLoader không
        // hề tham chiếu. Nếu loader phân giải dependency sai thì dòng này ném ngay, và đó
        // chính là điều cần kiểm chứng.
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { version = Version });

        File.AppendAllText(MarkerPath,
            $"{DateTime.Now:HH:mm:ss.fff} {Version} " +
            $"tfm={GetTargetFramework()} " +
            $"json={json} " +
            $"newtonsoft={typeof(Newtonsoft.Json.JsonConvert).Assembly.Location} " +
            $"doc={commandData.Application.ActiveUIDocument?.Document.Title ?? "<none>"} " +
            $"location={assembly.Location}{Environment.NewLine}");

        return Result.Succeeded;
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
