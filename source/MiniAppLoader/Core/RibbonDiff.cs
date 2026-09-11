using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Nice3point.Revit.Extensions.UI;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>Định danh một panel ribbon: tab + panel.</summary>
public readonly record struct RibbonPanelKey(string TabId, string PanelId);

/// <summary>
///     Phát hiện và gỡ ribbon do một plugin <c>Kind=Application</c> tự dựng, để reload được
///     nó mà không phải restart Revit.
///     <para>
///         <b>Vì sao là diff chứ không phải khai báo tay:</b> bản v1 bắt bạn liệt kê
///         <c>ribbonTabsToRemove</c>/<c>ribbonPanelsToRemove</c> trong plugins.json. Danh
///         sách đó chắc chắn lệch sau vài lần plugin đổi ribbon, và nếu ghi nhầm tên một
///         panel dùng chung thì bạn xoá mất UI của add-in KHÁC. Chụp ảnh ribbon ngay trước và
///         ngay sau <c>OnStartup</c> của plugin rồi chỉ gỡ đúng phần chênh lệch thì tự bảo
///         trì được, và về nguyên tắc không thể đụng vào panel mà plugin không tạo ra.
///     </para>
///     <para>
///         Việc gỡ dùng <c>RibbonPanel.RemovePanel()</c> của Nice3point.Revit.Extensions —
///         nó gỡ cả khỏi UI thật lẫn khỏi dictionary nội bộ của Revit (thiếu vế thứ hai thì
///         tạo lại panel cùng tên sẽ báo lỗi dù đã biến mất khỏi màn hình). Trên .NET 8+ nó
///         đi qua <c>UnsafeAccessor</c>; trên net48 vẫn là reflection vào field private, nên
///         đây vẫn là vùng không được Autodesk bảo đảm — mọi thứ đều bọc try/catch.
///     </para>
/// </summary>
internal static class RibbonDiff
{
    /// <summary>Ảnh chụp toàn bộ tab/panel đang có trên ribbon.</summary>
    public static IReadOnlyCollection<RibbonPanelKey> Snapshot()
    {
        var keys = new HashSet<RibbonPanelKey>();

        try
        {
            foreach (var tab in Autodesk.Windows.ComponentManager.Ribbon.Tabs)
            {
                foreach (var panel in tab.Panels)
                {
                    keys.Add(new RibbonPanelKey(tab.Id, panel.Source.Id));
                }
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Không chụp được ảnh ribbon — sẽ không tự gỡ ribbon của plugin được");
        }

        return keys;
    }

    /// <summary>
    ///     Gỡ mọi panel xuất hiện trong <paramref name="after"/> mà không có trong
    ///     <paramref name="before"/>, cộng thêm các panel khai tường minh trong
    ///     <paramref name="explicitPanels"/>/<paramref name="explicitTabs"/>.
    /// </summary>
    public static void RemoveAdded(
        UIControlledApplication application,
        IReadOnlyCollection<RibbonPanelKey> before,
        IReadOnlyCollection<RibbonPanelKey> after,
        IEnumerable<string> explicitTabs,
        IEnumerable<RibbonPanelRef> explicitPanels)
    {
        var added = after.Except(before).ToList();

        foreach (var panelRef in explicitPanels)
        {
            var key = new RibbonPanelKey(panelRef.TabName, panelRef.PanelName);
            if (!added.Contains(key)) added.Add(key);
        }

        foreach (var tabName in explicitTabs)
        {
            added.AddRange(after.Where(key => key.TabId == tabName && !added.Contains(key)));
        }

        foreach (var key in added)
        {
            RemovePanel(application, key);
        }

        // Tab tuỳ biến do plugin tự tạo: sau khi gỡ hết panel, tab rỗng vẫn nằm lại trên
        // thanh ribbon. Gỡ nốt để lần OnStartup sau dựng lại từ đầu không bị trùng.
        foreach (var tabId in added.Select(key => key.TabId).Distinct())
        {
            RemoveTabIfEmpty(tabId);
        }
    }

    private static void RemovePanel(UIControlledApplication application, RibbonPanelKey key)
    {
        try
        {
            var panel = application.GetRibbonPanels(key.TabId).FirstOrDefault(p => p.Name == key.PanelId);
            if (panel is null) return;

            panel.RemovePanel();
            Log.Debug("Đã gỡ panel ribbon {Tab}/{Panel}", key.TabId, key.PanelId);
        }
        catch (Exception exception)
        {
            // Ribbon cũ còn sót lại thì khó chịu nhưng không chết; đừng để nó chặn reload.
            Log.Warning(exception, "Không gỡ được panel ribbon {Tab}/{Panel}", key.TabId, key.PanelId);
        }
    }

    private static void RemoveTabIfEmpty(string tabId)
    {
        try
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            var tab = ribbon.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);

            if (tab is null || tab.Panels.Count > 0) return;

            ribbon.Tabs.Remove(tab);
            Log.Debug("Đã gỡ tab ribbon rỗng {Tab}", tabId);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Không gỡ được tab ribbon {Tab}", tabId);
        }
    }
}
