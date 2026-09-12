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

    /// <summary>Đã gỡ khỏi config; nút ribbon đã ẩn và slot được trả về pool.</summary>
    Removed
}
