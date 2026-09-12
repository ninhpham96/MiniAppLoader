using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SampleApplication;

/// <summary>
///     Plugin mẫu kiểu <c>Kind="Application"</c>: tự dựng TAB RIÊNG + panel + nút trong
///     <c>OnStartup</c>, đúng như một add-in độc lập được Revit nạp qua <c>.addin</c>.
///     <para>
///         Đây là thứ dùng để kiểm chứng phần khó nhất của loader: reload một
///         <c>IExternalApplication</c> mà không restart Revit, tức là phải gọi
///         <c>OnShutdown</c>, gỡ đúng ribbon nó đã tạo (và KHÔNG đụng vào ribbon của ai
///         khác), rồi gọi lại <c>OnStartup</c> của bản mới.
///     </para>
///     <para>
///         Ghi mốc ra file thay vì bật dialog — dialog là modal, sẽ chặn Revit và treo mọi
///         kiểm chứng tự động.
///     </para>
/// </summary>
public class SampleApplication : IExternalApplication
{
    /// <summary>Đổi hằng số này rồi build lại để xác nhận loader nạp đúng bản mới.</summary>
    private const string Version = "APP VERSION 21";

    /// <summary>Tab riêng của plugin — loader phải tự phát hiện và gỡ đúng cái này khi reload.</summary>
    private const string TabName = "Sample App Tab";

    private const string PanelName = "Sample Panel";

    /// <summary>
    ///     Panel mà plugin thêm vào tab DÙNG CHUNG "Add-Ins". Gỡ nhầm ở đây là xoá mất UI của
    ///     add-in khác, nên đây là ca khó nhất cho ribbon diff.
    /// </summary>
    private const string SharedTabPanelName = "Sample Shared Panel";

    public static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "sample-application.log");

    /// <summary>
    ///     Công tắc cho thí nghiệm: nếu file này tồn tại thì plugin KHÔNG dựng ribbon nào cả.
    ///     Dùng để tách bạch xem việc ALC không thu hồi được là do ribbon plugin tạo ra, hay
    ///     do bản thân việc nạp một IExternalApplication.
    /// </summary>
    private static string NoRibbonSwitch => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniAppLoader", "sample-app-no-ribbon");

    public Result OnStartup(UIControlledApplication application)
    {
        Mark("OnStartup " + Version);

        // Công tắc thứ hai: ném ngay trong OnStartup, để kiểm chứng loader ghi nhận lỗi và
        // VẪN nạp tiếp các plugin còn lại thay vì bỏ dở cả vòng.
        if (File.Exists(NoRibbonSwitch + "-throw"))
        {
            throw new InvalidOperationException("Lỗi cố ý từ OnStartup của SampleApplication.");
        }

        if (File.Exists(NoRibbonSwitch))
        {
            Mark("  (bo qua ribbon)");
            return Result.Succeeded;
        }

        application.CreateRibbonTab(TabName);
        var panel = application.CreateRibbonPanel(TabName, PanelName);
        panel.AddItem(new PushButtonData(
            "SampleAppButton", Version, typeof(SampleApplication).Assembly.Location, typeof(SampleAppCommand).FullName));

        // Panel thứ hai, nằm trên tab Add-Ins dùng chung.
        var sharedPanel = application.CreateRibbonPanel(SharedTabPanelName);
        sharedPanel.AddItem(new PushButtonData(
            "SampleSharedButton", "Shared", typeof(SampleApplication).Assembly.Location, typeof(SampleAppCommand).FullName));

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Mark("OnShutdown " + Version);
        return Result.Succeeded;
    }

    internal static void Mark(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.AppendAllText(MarkerPath, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
    }
}

/// <summary>Lệnh của nút do plugin tự tạo — chỉ để nút trỏ vào đâu đó hợp lệ.</summary>
[Transaction(TransactionMode.Manual)]
public class SampleAppCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        SampleApplication.Mark("SampleAppCommand executed");
        return Result.Succeeded;
    }
}
