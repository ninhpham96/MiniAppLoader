using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using MiniAppLoader.Core;
using Nice3point.Revit.Toolkit.External;

namespace MiniAppLoader.Commands;

/// <summary>
///     Bật/tắt dockable pane "Plugin Hub".
///     <para>
///         Pane đã được đăng ký sẵn trong <c>OnStartup</c> (Revit chỉ cho
///         <c>RegisterDockablePane</c> ở đó), nên ở đây chỉ lấy ra và Show/Hide.
///     </para>
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class ShowHubCommand : ExternalCommand
{
    public override void Execute()
    {
        var pane = Application.GetDockablePane(PluginHost.HubPaneId);

        if (pane.IsShown()) pane.Hide();
        else pane.Show();
    }
}
