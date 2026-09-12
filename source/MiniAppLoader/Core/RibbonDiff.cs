using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Nice3point.Revit.Extensions.UI;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>
///     Định danh một panel ribbon.
///     <para>
///         Giữ CẢ <paramref name="PanelId"/> lẫn <paramref name="PanelTitle"/> là cố ý:
///         <c>Autodesk.Windows</c> định danh panel bằng Id (<c>CustomCtrl_%Tab%Tiêu đề</c>)
///         nên diff phải dùng Id, nhưng <c>Autodesk.Revit.UI.RibbonPanel.Name</c> lại là TIÊU
///         ĐỀ. Lúc đầu chỉ giữ Id rồi đem so với <c>Name</c> — không bao giờ khớp, nên chẳng
///         panel nào bị gỡ và lần <c>OnStartup</c> sau của plugin ném "panel/tab đã tồn tại".
///     </para>
/// </summary>
public readonly record struct RibbonPanelKey(string TabId, string PanelId, string PanelTitle);

/// <summary>Ảnh chụp ribbon tại một thời điểm: có những tab nào và những panel nào.</summary>
public sealed class RibbonSnapshot
{
    public required IReadOnlyCollection<string> Tabs { get; init; }
    public required IReadOnlyCollection<RibbonPanelKey> Panels { get; init; }

    public static RibbonSnapshot Empty => new() { Tabs = [], Panels = [] };
}

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
///         Đây là vùng API nội bộ không được Autodesk bảo đảm, nên mọi thao tác đều bọc
///         try/catch: ribbon sót lại thì khó chịu, nhưng không được phép chặn lần reload.
///     </para>
/// </summary>
internal static class RibbonDiff
{
    /// <summary>Chụp toàn bộ tab/panel đang có trên ribbon.</summary>
    public static RibbonSnapshot Snapshot()
    {
        var tabs = new HashSet<string>(StringComparer.Ordinal);
        var panels = new HashSet<RibbonPanelKey>();

        try
        {
            foreach (var tab in Autodesk.Windows.ComponentManager.Ribbon.Tabs)
            {
                tabs.Add(tab.Id);

                foreach (var panel in tab.Panels)
                {
                    panels.Add(new RibbonPanelKey(tab.Id, panel.Source.Id, panel.Source.Title));
                }
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Không chụp được ảnh ribbon — sẽ không tự gỡ ribbon của plugin được");
        }

        return new RibbonSnapshot { Tabs = tabs, Panels = panels };
    }

    /// <summary>
    ///     Gỡ mọi tab/panel xuất hiện trong <paramref name="after"/> mà không có trong
    ///     <paramref name="before"/>, cộng thêm phần khai tường minh trong
    ///     <paramref name="explicitTabs"/>/<paramref name="explicitPanels"/>.
    /// </summary>
    public static void RemoveAdded(
        UIControlledApplication application,
        RibbonSnapshot before,
        RibbonSnapshot after,
        IEnumerable<string> explicitTabs,
        IEnumerable<RibbonPanelRef> explicitPanels)
    {
        var addedPanels = after.Panels.Except(before.Panels).ToList();

        foreach (var panelRef in explicitPanels)
        {
            var match = after.Panels.FirstOrDefault(key =>
                key.TabId == panelRef.TabName && key.PanelTitle == panelRef.PanelName);

            if (match != default && !addedPanels.Contains(match)) addedPanels.Add(match);
        }

        foreach (var tabName in explicitTabs)
        {
            addedPanels.AddRange(after.Panels.Where(key => key.TabId == tabName && !addedPanels.Contains(key)));
        }

        foreach (var key in addedPanels) RemovePanel(application, key);

        // Tab RIÊNG do plugin tự tạo (không có trong ảnh chụp "trước") phải gỡ nốt: panel đã
        // sạch nhưng tab rỗng vẫn nằm lại trên thanh ribbon, và quan trọng hơn là lần
        // OnStartup sau của plugin sẽ ném "The tab with the input name exists already".
        var addedTabs = after.Tabs.Except(before.Tabs).Concat(explicitTabs).Distinct();
        foreach (var tabId in addedTabs) RemoveTab(tabId);
    }

    private static void RemovePanel(UIControlledApplication application, RibbonPanelKey key)
    {
        // Đường chính: RemovePanel() của Nice3point.Revit.Extensions gỡ cả khỏi UI thật LẪN
        // khỏi dictionary nội bộ của Revit. Thiếu vế thứ hai thì tạo lại panel cùng tên sẽ
        // báo lỗi dù nó đã biến mất khỏi màn hình.
        try
        {
            var panel = FindPanel(application, key);

            if (panel is not null)
            {
                panel.RemovePanel();
                Log.Debug("Đã gỡ panel ribbon {Tab}/{Panel}", key.TabId, key.PanelTitle);
                return;
            }
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "RemovePanel() không xử lý được {Tab}/{Panel}, thử gỡ trực tiếp",
                key.TabId, key.PanelTitle);
        }

        // Đường dự phòng: panel không phải do Revit API tạo (hoặc GetRibbonPanels không thấy
        // tab đó) -> gỡ thẳng khỏi collection của Autodesk.Windows.
        try
        {
            var tab = FindTab(key.TabId);
            var target = tab?.Panels.FirstOrDefault(p => p.Source.Id == key.PanelId);

            if (target is null) return;

            tab!.Panels.Remove(target);
            Log.Debug("Đã gỡ panel ribbon {Tab}/{Panel} (đường dự phòng)", key.TabId, key.PanelTitle);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Không gỡ được panel ribbon {Tab}/{Panel}", key.TabId, key.PanelTitle);
        }
    }

    private static void RemoveTab(string tabId)
    {
        try
        {
            var tab = FindTab(tabId);
            if (tab is null) return;

            // Tab còn panel nghĩa là còn thứ không phải do plugin tạo (hoặc gỡ panel đã
            // thất bại) -> KHÔNG đụng vào, thà để ribbon trùng còn hơn xoá nhầm của người khác.
            if (tab.Panels.Count > 0)
            {
                Log.Warning("Tab '{Tab}' vẫn còn {Count} panel nên không gỡ — plugin có thể báo trùng tên khi nạp lại",
                    tabId, tab.Panels.Count);
                return;
            }

            Autodesk.Windows.ComponentManager.Ribbon.Tabs.Remove(tab);
            Log.Debug("Đã gỡ tab ribbon {Tab}", tabId);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Không gỡ được tab ribbon {Tab}", tabId);
        }
    }

    /// <summary>
    ///     Tìm <see cref="RibbonPanel"/> phía Revit API tương ứng với một panel trong ảnh chụp.
    ///     <para>
    ///         Phải thử hai overload: <c>GetRibbonPanels(tabName)</c> chỉ tra được TAB TUỲ
    ///         BIẾN và trả về <see langword="null"/> cho tab "Add-Ins" dựng sẵn — panel nào
    ///         plugin thêm vào Add-Ins thì phải lấy qua overload enum <c>Tab.AddIns</c>. Bỏ
    ///         sót vế này thì panel chỉ biến mất khỏi màn hình mà vẫn còn trong dictionary
    ///         nội bộ của Revit, và lần <c>OnStartup</c> sau của plugin ném
    ///         "The panel with name ... already exists!".
    ///     </para>
    /// </summary>
    private static RibbonPanel? FindPanel(UIControlledApplication application, RibbonPanelKey key)
    {
        var panel = TryGetPanels(() => application.GetRibbonPanels(key.TabId))
            .FirstOrDefault(candidate => candidate.Name == key.PanelTitle);

        if (panel is not null) return panel;

        return TryGetPanels(() => application.GetRibbonPanels(Tab.AddIns))
            .FirstOrDefault(candidate => candidate.Name == key.PanelTitle);
    }

    private static IEnumerable<RibbonPanel> TryGetPanels(Func<IList<RibbonPanel>?> getter)
    {
        try
        {
            return getter() ?? [];
        }
        catch (Exception)
        {
            // Tên tab không tồn tại -> Revit ném thay vì trả null. Với việc dọn dẹp
            // best-effort thì "không tìm thấy" và "hỏi sai chỗ" là như nhau.
            return [];
        }
    }

    private static Autodesk.Windows.RibbonTab? FindTab(string tabId)
        => Autodesk.Windows.ComponentManager.Ribbon.Tabs.FirstOrDefault(tab => tab.Id == tabId);
}
