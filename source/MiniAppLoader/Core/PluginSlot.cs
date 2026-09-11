using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniAppLoader.Core;

/// <summary>
///     Một plugin đang được loader quản lý.
///     <para>
///         Đây vừa là model vừa là row-viewmodel của Plugin Hub — cố ý KHÔNG tách đôi, vì
///         hai danh sách song song phải đồng bộ tay là chỗ thiết kế kiểu này hay mục nhất.
///     </para>
///     <para>
///         <b>Luật threading:</b> chỉ được mutate trên Revit main thread (tức là bên trong
///         <c>IExternalEventHandler.Execute</c> hoặc trong một command). Callback của
///         FileSystemWatcher chạy trên ThreadPool và TUYỆT ĐỐI không được đụng vào đây.
///     </para>
/// </summary>
public sealed partial class PluginSlot : ObservableObject
{
    /// <summary>
    ///     Vị trí trong <see cref="SlotPool"/>, cũng là hậu tố của class
    ///     <c>GenericCommandNN</c> gắn với nút ribbon của plugin này. Không đổi trong suốt
    ///     thời gian slot được cấp phát.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    ///     Entry gốc trong <c>plugins.json</c>. Giữ những field không hiện trên UI
    ///     (<c>commandClassName</c>, các override gỡ ribbon) để ghi lại đúng nguyên trạng.
    ///     <see langword="null"/> khi slot đang trống.
    /// </summary>
    public PluginEntry? Entry { get; set; }

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _dllPath = string.Empty;
    [ObservableProperty] private string _buttonText = string.Empty;
    [ObservableProperty] private PluginKind _kind = PluginKind.Command;
    [ObservableProperty] private PluginStatus _status = PluginStatus.Idle;
    [ObservableProperty] private bool _autoReload = true;

    /// <summary>Stack trace của lần hỏng gần nhất; hiện trong tooltip của chấm đỏ.</summary>
    [ObservableProperty] private string? _lastError;

    /// <summary>Thời điểm nạp thành công gần nhất — để thấy reload đã thực sự xảy ra.</summary>
    [ObservableProperty] private DateTime? _lastLoadedAt;

    /// <summary>Thời gian nạp của lần gần nhất, hiển thị trong log pane.</summary>
    [ObservableProperty] private TimeSpan _lastLoadDuration;

    /// <summary>Slot đang trống (chưa gán plugin nào) — nút ribbon tương ứng đang ẩn.</summary>
    public bool IsFree => string.IsNullOrEmpty(DllPath);
}

/// <summary>Kiểu entry point mà loader tìm trong DLL plugin.</summary>
public enum PluginKind
{
    /// <summary>Plugin có class <c>IExternalCommand</c>, chạy bằng nút trên panel Plugins.</summary>
    Command,

    /// <summary>
    ///     Plugin có class <c>IExternalApplication</c> và tự dựng ribbon riêng trong
    ///     <c>OnStartup</c> — loader gọi thẳng lúc Revit khởi động, không tạo nút cho nó.
    /// </summary>
    Application
}
