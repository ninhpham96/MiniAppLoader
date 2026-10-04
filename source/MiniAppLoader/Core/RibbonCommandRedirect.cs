#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.UI;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>
///     Chuyển hướng việc chạy command của các nút ribbon mà một plugin <c>Kind=Application</c>
///     tự tạo, để mỗi lần bấm chạy code MỚI NHẤT của plugin thay vì bản Revit đã nhớ từ lần nạp
///     đầu.
///     <para>
///         <b>Vì sao cần:</b> trên Revit 2025+ (.NET 8), Revit nạp assembly của command theo tên
///         vào context mặc định và giữ bản đầu tiên cho cả phiên. Loader dựng lại được ribbon sau
///         mỗi lần build, nhưng nút bấm vẫn chạy bản cũ. Đã đo trực tiếp trên Revit 2026: nạp
///         sẵn bản mới vào context mặc định hay trả nó qua <c>AssemblyLoadContext.Resolving</c>
///         đều bị Revit bỏ qua.
///     </para>
///     <para>
///         <b>Cách làm:</b> mỗi nút ribbon thật (<c>Autodesk.Windows.RibbonItem</c>) có
///         <c>CommandHandler</c> là thứ được gọi khi bấm. Thay nó bằng <see cref="RedirectCommand"/>
///         để đẩy yêu cầu sang command của loader (nút Plugin Hub), nơi tạo instance từ assembly mới
///         nhất. Chỉ đụng tới nút có <c>AssemblyName</c> trùng assembly của plugin, nên không thể
///         chạm vào nút của add-in khác.
///     </para>
///     <para>
///         Đây là API nội bộ không được Autodesk bảo đảm, nên mọi bước đều phòng thủ: không gắn
///         được thì nút giữ nguyên hành vi mặc định của Revit (chạy bản cũ), không bao giờ hỏng.
///     </para>
/// </summary>
internal static class RibbonCommandRedirect
{
    /// <summary>Gắn bộ chuyển hướng cho mọi nút thuộc <paramref name="assemblyPath"/>. Trả về số nút đã gắn.</summary>
    /// <param name="dispatch">
    ///     Nhận tên class command; trả về <see langword="false"/> nếu không chuyển được, khi đó
    ///     nút chạy theo đường mặc định của Revit.
    /// </param>
    public static int Attach(UIControlledApplication application, string assemblyPath, Func<string, bool> dispatch)
    {
        var target = Normalize(assemblyPath);
        var attached = 0;

        foreach (var panel in EnumeratePanels(application))
        {
            foreach (var item in panel.GetItems()) attached += Visit(item, target, dispatch);
        }

        return attached;
    }

    private static int Visit(RibbonItem item, string target, Func<string, bool> dispatch)
    {
        switch (item)
        {
            case PushButton button when Normalize(button.AssemblyName) == target:
                return Hook(button, dispatch) ? 1 : 0;

            // SplitButton kế thừa PulldownButton nên cũng rơi vào đây. Nút xếp chồng thì
            // GetItems() của panel đã trả về phẳng từng nút.
            case PulldownButton pulldown:
                return pulldown.GetItems().Sum(child => Visit(child, target, dispatch));

            default:
                return 0;
        }
    }

    private static bool Hook(PushButton button, Func<string, bool> dispatch)
    {
        try
        {
            var internalItem = RibbonIds.TryGetInternalItem(button);
            var property = internalItem?.GetType().GetProperty("CommandHandler");
            if (internalItem is null || property is not { CanRead: true, CanWrite: true }) return false;

            var original = property.GetValue(internalItem) as ICommand;
            if (original is RedirectCommand) return false; // đã gắn rồi

            property.SetValue(internalItem, new RedirectCommand(button.ClassName, original, dispatch));
            return true;
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Không gắn được bộ chuyển hướng cho nút {Class}", button.ClassName);
            return false;
        }
    }

    /// <summary>Mọi panel Revit API tra được: panel của tab tuỳ biến lẫn panel trong tab Add-Ins dựng sẵn.</summary>
    private static IEnumerable<RibbonPanel> EnumeratePanels(UIControlledApplication application)
    {
        var panels = new List<RibbonPanel>();

        try
        {
            foreach (var tab in Autodesk.Windows.ComponentManager.Ribbon.Tabs)
            {
                try
                {
                    panels.AddRange(application.GetRibbonPanels(tab.Id) ?? []);
                }
                catch (Exception)
                {
                    // Tab dựng sẵn của Revit không tra được theo tên — bỏ qua.
                }
            }

            panels.AddRange(application.GetRibbonPanels(Tab.AddIns) ?? []);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Không liệt kê được panel ribbon để gắn bộ chuyển hướng");
        }

        return panels;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        try
        {
            return Path.GetFullPath(path!).ToLowerInvariant();
        }
        catch (Exception)
        {
            return path!.ToLowerInvariant();
        }
    }

    /// <summary>
    ///     Thay cho <c>CommandHandler</c> gốc của Revit: chuyển yêu cầu sang loader; nếu không
    ///     chuyển được thì gọi lại handler gốc để nút vẫn chạy (bản cũ) chứ không chết.
    /// </summary>
    private sealed class RedirectCommand(string className, ICommand? original, Func<string, bool> dispatch) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { if (original is not null) original.CanExecuteChanged += value; }
            remove { if (original is not null) original.CanExecuteChanged -= value; }
        }

        public bool CanExecute(object? parameter) => original?.CanExecute(parameter) ?? true;

        public void Execute(object? parameter)
        {
            try
            {
                if (dispatch(className)) return;
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Chuyển hướng nút '{Class}' thất bại — chạy theo đường mặc định của Revit", className);
            }

            original?.Execute(parameter);
        }
    }
}
#endif
