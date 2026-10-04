using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MiniAppLoader.Core;

namespace MiniAppLoader.Commands;

/// <summary>
///     Bật/tắt dockable pane "Plugin Hub".
///     <para>
///         Pane đã được đăng ký sẵn trong <c>OnStartup</c> (Revit chỉ cho
///         <c>RegisterDockablePane</c> ở đó), nên ở đây chỉ lấy ra và Show/Hide.
///     </para>
///     <para>
///         Command này còn là "xe chở" để chạy command của plugin <c>Kind=Application</c> từ bản
///         mới nhất: <see cref="PluginHost.HasPendingDispatch"/> là true thì chạy command đó thay vì
///         bật/tắt pane. Mượn nút này vì nó luôn hiện — Revit không cho post tới nút ẩn.
///     </para>
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ShowHubCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var host = PluginHost.Current;
        if (host.HasPendingDispatch) return host.ExecuteDispatched(data, ref message, elements);

        var pane = data.Application.GetDockablePane(PluginHost.HubPaneId);

        if (pane.IsShown()) pane.Hide();
        else pane.Show();

        return Result.Succeeded;
    }
}
