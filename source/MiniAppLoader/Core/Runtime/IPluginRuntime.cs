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

    /// <summary>
    ///     Số bản nạp CŨ của slot vẫn còn nằm trong tiến trình.
    ///     <para>
    ///         Đây thuần tuý là chỉ số BỘ NHỚ, không phải chỉ số đúng/sai: mỗi lần reload đều
    ///         nạp một assembly hoàn toàn mới, nên code đang chạy luôn là code mới nhất dù con
    ///         số này bằng bao nhiêu. Nó chỉ có ý nghĩa khi tăng dần không ngừng — dấu hiệu
    ///         plugin đang giữ chặt chính nó (thường do subscribe sự kiện Revit mà không nhả).
    ///     </para>
    /// </summary>
    int CountStaleLoads(int slotIndex);
}

/// <summary>Kết quả gỡ một plugin.</summary>
internal enum UnloadResult
{
    /// <summary>Slot chưa nạp gì, không có việc gì để làm.</summary>
    NothingLoaded,

    /// <summary>
    ///     Đã yêu cầu gỡ. Lần nạp kế tiếp chắc chắn là assembly mới; việc GC thu hồi bản cũ
    ///     xảy ra sau đó, theo nhịp của chính GC.
    /// </summary>
    Requested,

    /// <summary>
    ///     .NET Framework không có collectible AssemblyLoadContext nên không bao giờ gỡ thật
    ///     được. Đây là giới hạn nền tảng đã biết, KHÔNG phải lỗi — mỗi lần reload nạp một
    ///     assembly mới và bản cũ nằm lại (rò rỉ nhẹ, chấp nhận được khi dev).
    /// </summary>
    NotSupported
}
