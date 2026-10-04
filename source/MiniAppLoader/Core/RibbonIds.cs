using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace MiniAppLoader.Core;

/// <summary>
///     Lấy ID nội bộ thật của một nút ribbon tạo bằng code.
///     <para>
///         <c>RevitCommandId.LookupCommandId("Namespace.MyCommand")</c> KHÔNG tìm được nút
///         tạo programmatic (chỉ tìm được command khai trong <c>.addin</c>). ID thật nằm ở
///         <c>Autodesk.Windows.RibbonItem.Id</c> — cũng chính là ID Revit dùng khi bạn gán
///         phím tắt trong Keyboard Shortcuts editor — và chỉ tới được qua field private
///         <c>m_RibbonItem</c> của <see cref="RibbonItem"/>.
///     </para>
///     <para>
///         Định dạng: <c>CustomCtrl_%CustomCtrl_%&lt;Tab&gt;%&lt;Panel&gt;%&lt;Button&gt;</c>
///         — tức là ID phụ thuộc TÊN tab/panel/nút. Đổi tên tab hoặc panel là gãy
///         <c>PostCommand</c>, nên chụp ID đúng một lần lúc tạo nút và giữ lại.
///     </para>
///     <para>
///         Đây là API không được Autodesk công bố. Đã verify trên Revit 2024.3; các version
///         khác phải tự kiểm chứng — vì vậy mọi lỗi đều nuốt và trả <see langword="null"/>,
///         phía gọi tự disable nút Run thay vì để nổ lúc người dùng bấm.
///     </para>
/// </summary>
internal static class RibbonIds
{
    private static readonly FieldInfo? InternalItemField = typeof(RibbonItem)
        .GetField("m_RibbonItem", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    /// <summary>
    ///     Object <c>Autodesk.Windows.RibbonItem</c> thật bên dưới <paramref name="item"/>, hoặc
    ///     <see langword="null"/> nếu kỹ thuật reflection không còn đúng.
    /// </summary>
    public static object? TryGetInternalItem(RibbonItem item)
    {
        try
        {
            return InternalItemField?.GetValue(item);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Trả về ID nội bộ của <paramref name="item"/>, hoặc <see langword="null"/> nếu
    ///     kỹ thuật reflection không còn đúng trên version Revit đang chạy.
    /// </summary>
    public static string? TryGetInternalId(RibbonItem item)
    {
        try
        {
            var internalItem = InternalItemField?.GetValue(item);
            return internalItem?.GetType().GetProperty("Id")?.GetValue(internalItem) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Lấy ID nội bộ VÀ xác nhận Revit thật sự tra cứu được nó. Gọi ngay lúc tạo nút
    ///     trong <c>OnStartup</c> để biết sớm, thay vì phát hiện lúc người dùng bấm Run.
    /// </summary>
    public static string? TryGetVerifiedCommandId(RibbonItem item)
    {
        var id = TryGetInternalId(item);
        if (string.IsNullOrEmpty(id)) return null;

        try
        {
            return RevitCommandId.LookupCommandId(id) is null ? null : id;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
