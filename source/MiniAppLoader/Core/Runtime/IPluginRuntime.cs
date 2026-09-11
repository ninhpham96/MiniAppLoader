using System.Reflection;

namespace MiniAppLoader.Core.Runtime;

/// <summary>
///     Trừu tượng hoá khác biệt DUY NHẤT giữa .NET Framework (Revit ≤2024) và .NET Core
///     (Revit 2025+): cách nạp và gỡ một DLL plugin. Phần còn lại của loader không cần biết
///     đang chạy trên runtime nào.
/// </summary>
internal interface IPluginRuntime
{
    /// <summary>Nạp (hoặc nạp lại) DLL của slot và trả về assembly mới.</summary>
    Assembly Load(PluginSlot slot);

    /// <summary>Gỡ bản đang nạp của slot.</summary>
    UnloadResult Unload(int slotIndex);
}

/// <summary>Kết quả gỡ một plugin — phân biệt "nền tảng không hỗ trợ" với "gỡ không được".</summary>
internal enum UnloadResult
{
    /// <summary>Slot chưa nạp gì, không có việc gì để làm.</summary>
    NothingLoaded,

    /// <summary>AssemblyLoadContext đã chết hẳn, lần nạp sau là bản hoàn toàn mới.</summary>
    Released,

    /// <summary>
    ///     Đã gọi <c>Unload()</c> nhưng context vẫn còn sống — plugin đang bị giữ chặt
    ///     (thường do subscribe <c>Idling</c>/<c>DocumentOpened</c> mà không nhả). Code cũ
    ///     vẫn nằm trong tiến trình; chỉ restart Revit mới sạch.
    /// </summary>
    StillAlive,

    /// <summary>
    ///     .NET Framework không có collectible AssemblyLoadContext nên không bao giờ gỡ
    ///     thật được. Đây là giới hạn nền tảng đã biết, KHÔNG phải lỗi — mỗi lần reload nạp
    ///     một assembly mới và bản cũ nằm lại (rò rỉ nhẹ, chấp nhận được khi dev).
    /// </summary>
    NotSupported
}
