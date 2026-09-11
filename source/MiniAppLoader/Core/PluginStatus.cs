namespace MiniAppLoader.Core;

/// <summary>
///     Trạng thái vòng đời của một plugin, hiển thị bằng chấm màu trong Plugin Hub.
/// </summary>
public enum PluginStatus
{
    /// <summary>Đã khai báo nhưng chưa nạp lần nào.</summary>
    Idle,

    /// <summary>Đã nạp thành công, sẵn sàng chạy.</summary>
    Loaded,

    /// <summary>DLL vừa đổi, đang chờ Revit rảnh để reload.</summary>
    Pending,

    /// <summary>Lần nạp/chạy gần nhất ném exception — xem <see cref="PluginSlot.LastError"/>.</summary>
    Error,

    /// <summary>
    ///     Đã gọi unload nhưng AssemblyLoadContext vẫn còn sống (plugin giữ chặt reference,
    ///     thường do subscribe Idling/DocumentOpened mà không nhả). Bản cũ còn nằm trong
    ///     tiến trình — phải restart Revit mới reload sạch được.
    /// </summary>
    Leaked,

    /// <summary>Đã gỡ khỏi config; nút ribbon đã ẩn và slot được trả về pool.</summary>
    Removed
}
