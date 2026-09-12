using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Autodesk.Revit.UI;
using MiniAppLoader.Commands;
using Nice3point.Revit.Extensions.UI;

namespace MiniAppLoader.Core;

/// <summary>
///     Sở hữu các nút ribbon của plugin và việc cấp phát slot.
///     <para>
///         <b>Toàn bộ nút được tạo sẵn trong <c>OnStartup</c> rồi ẩn đi</b>, không tạo thêm
///         lúc chạy. Đây là khác biệt lớn so với bản v1 (tạo nút live khi Load Plugin), và
///         nó gỡ được ba vấn đề cùng lúc:
///         <list type="bullet">
///             <item><c>AddStackPanel</c> chỉ nhận item lúc tạo, không append được về sau.</item>
///             <item>ID ribbon phụ thuộc tên nút nên phải ổn định để <c>PostCommand</c> chạy.</item>
///             <item>Thứ tự nút không còn phụ thuộc thứ tự thêm plugin qua từng phiên.</item>
///         </list>
///         Thêm plugin = gán slot + đổi <c>ItemText</c> + bật <c>Visible</c>.
///         Gỡ plugin = tắt <c>Visible</c> + trả slot về pool. Nút được TÁI SỬ DỤNG, không
///         tiêu hao — nên "nút ở lại ribbon tới khi restart Revit" của v1 không còn đúng.
///     </para>
/// </summary>
public sealed class SlotPool
{
    /// <summary>
    ///     Số plugin tối đa có nút ribbon cùng lúc. Phải khớp đúng số class
    ///     <c>GenericCommandNN</c> trong <c>Commands/GenericCommands.cs</c>.
    /// </summary>
    public const int MaxSlots = 16;

    private readonly PushButton?[] _buttons = new PushButton?[MaxSlots];
    private readonly string?[] _commandIds = new string?[MaxSlots];
    private readonly ObservableCollection<PluginSlot> _slots = [];

    public SlotPool()
    {
        for (var i = 0; i < MaxSlots; i++) _slots.Add(new PluginSlot { Index = i });
        Slots = new ReadOnlyObservableCollection<PluginSlot>(_slots);
    }

    /// <summary>Toàn bộ slot (kể cả slot trống) — Plugin Hub tự lọc theo <see cref="PluginSlot.IsFree"/>.</summary>
    public ReadOnlyObservableCollection<PluginSlot> Slots { get; }

    /// <summary>
    ///     <see langword="true"/> khi mọi nút đều tra được <see cref="RevitCommandId"/>, tức
    ///     là nút Run trong Plugin Hub dùng được. Nếu <see langword="false"/> thì kỹ thuật ID
    ///     nội bộ đã gãy trên version Revit này — Hub phải disable Run kèm giải thích thay vì
    ///     để người dùng bấm rồi không có gì xảy ra.
    /// </summary>
    public bool CanPostCommands { get; private set; }

    /// <summary>
    ///     Tạo panel "Plugins" cùng toàn bộ nút (ẩn sẵn). Chỉ được gọi một lần, từ
    ///     <c>OnStartup</c>.
    /// </summary>
    public void Build(RibbonPanel panel)
    {
        var verified = 0;

        // Một stack panel duy nhất: wrapper của Nice3point tự xuống cột mới sau mỗi 3 item,
        // nên không cần tự chia nhóm.
        var stack = panel.AddStackPanel();

        for (var index = 0; index < MaxSlots; index++)
        {
            // Nhãn nút đổi được lúc chạy, nhưng TÊN nút thì không — nó nằm trong ID ribbon
            // nội bộ mà PostCommand dựa vào, nên phải cố định và duy nhất.
            var button = GenericCommandButtons.Add(stack, index, $"Slot {index:D2}");
            button.Visible = false;

            _buttons[index] = button;
            _commandIds[index] = RibbonIds.TryGetVerifiedCommandId(button);
            if (_commandIds[index] is not null) verified++;
        }

        CanPostCommands = verified == MaxSlots;
    }

    /// <summary>Slot trống đầu tiên, hoặc <see langword="null"/> nếu đã dùng hết pool.</summary>
    public PluginSlot? TakeFreeSlot() => _slots.FirstOrDefault(slot => slot.IsFree);

    /// <summary>Các plugin đang thực sự hoạt động (slot đã gán) theo thứ tự slot.</summary>
    public IEnumerable<PluginSlot> ActiveSlots() => _slots.Where(slot => !slot.IsFree);

    /// <summary>Hiện nút của slot, đặt lại nhãn theo tên plugin và gán icon.</summary>
    public void ShowButton(PluginSlot slot)
    {
        var button = _buttons[slot.Index];
        if (button is null) return;

        // ItemText đổi được lúc chạy; Name (nằm trong ID ribbon) thì không.
        button.ItemText = string.IsNullOrWhiteSpace(slot.ButtonText) ? slot.Id : slot.ButtonText;
        button.ToolTip = slot.DllPath;
        button.Visible = true;

        if (slot.Entry is { } entry)
        {
            var (small, large) = PluginIcon.Resolve(entry);
            button.Image = small;
            button.LargeImage = large;
        }
    }

    /// <summary>Ẩn nút khi plugin bị gỡ. Nút được giữ lại để cấp cho plugin sau.</summary>
    public void HideButton(PluginSlot slot)
    {
        var button = _buttons[slot.Index];
        if (button is null) return;

        button.Visible = false;
        button.ItemText = $"Slot {slot.Index:D2}";
        button.Image = null;
        button.LargeImage = null;
    }

    /// <summary>
    ///     ID lệnh của slot, dùng cho <c>UIApplication.PostCommand</c> khi bấm Run trong
    ///     Plugin Hub. <see langword="null"/> nghĩa là không post được trên version này.
    /// </summary>
    public string? GetCommandId(int index) => _commandIds[index];
}
