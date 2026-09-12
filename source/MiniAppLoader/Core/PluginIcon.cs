using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
// Autodesk.Revit.DB nạp global bằng ImplicitUsings, và có Point/FormattedText/Color trùng
// tên với System.Windows.* — alias để khỏi viết namespace đầy đủ khắp file.
using WpfPoint = System.Windows.Point;
using WpfFormattedText = System.Windows.Media.FormattedText;

namespace MiniAppLoader.Core;

/// <summary>
///     Icon nút ribbon của từng plugin trên panel "Plugins".
///     <para>
///         Ba lớp ưu tiên, để nút không bao giờ trống trơn:
///         <list type="number">
///             <item><see cref="PluginEntry.IconPath"/> khai tường minh trong <c>plugins.json</c>.</item>
///             <item>
///                 File <c>&lt;tên-dll&gt;.png</c> nằm CẠNH dll (convention) — build ra kèm icon
///                 là dùng được ngay, không cần sửa config.
///             </item>
///             <item>Icon tự sinh: vòng tròn màu + chữ cái đầu, khi không có gì ở trên.</item>
///         </list>
///     </para>
/// </summary>
internal static class PluginIcon
{
    public static (ImageSource Small, ImageSource Large) Resolve(PluginEntry entry)
    {
        var path = ResolvePath(entry);

        if (path is not null)
        {
            try
            {
                return (LoadFrozen(path, 16), LoadFrozen(path, 32));
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Không đọc được icon '{Path}' cho '{Id}' — dùng icon tự sinh",
                    path, entry.Id);
            }
        }

        return (Generate(entry, 16), Generate(entry, 32));
    }

    private static string? ResolvePath(PluginEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.IconPath))
        {
            // Tương đối thì tính theo thư mục chứa DLL, để plugins.json không phải ghi đường
            // dẫn tuyệt đối (dllPath vốn đã tuyệt đối, iconPath ăn theo cho gọn).
            var full = Path.IsPathRooted(entry.IconPath)
                ? entry.IconPath
                : Path.Combine(Path.GetDirectoryName(entry.DllPath) ?? string.Empty, entry.IconPath);

            if (File.Exists(full)) return full;

            Log.Warning("iconPath '{Path}' của '{Id}' không tồn tại — thử theo convention rồi icon tự sinh",
                entry.IconPath, entry.Id);
        }

        var byConvention = Path.ChangeExtension(entry.DllPath, ".png");
        return File.Exists(byConvention) ? byConvention : null;
    }

    private static BitmapImage LoadFrozen(string path, int pixelSize)
    {
        // OnLoad + Freeze: đọc xong đóng file NGAY, không giữ khoá — quan trọng vì icon nằm
        // cạnh DLL plugin, cùng thư mục mà MSBuild và ShadowCopy đang ghi/xoá liên tục.
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = pixelSize;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    ///     Vòng tròn màu + chữ cái đầu. Màu suy ra ỔN ĐỊNH từ <see cref="PluginEntry.Id"/> bằng
    ///     FNV-1a — KHÔNG dùng <see cref="string.GetHashCode()"/>: giá trị đó bị .NET
    ///     randomize mỗi lần khởi động process, icon sẽ đổi màu mỗi lần mở lại Revit.
    /// </summary>
    private static ImageSource Generate(PluginEntry entry, int size)
    {
        var source = string.IsNullOrEmpty(entry.ButtonText) ? entry.Id : entry.ButtonText!;
        var label = source.Length > 0 ? char.ToUpperInvariant(source[0]) : '?';
        var color = Palette[StableHash(entry.Id) % (uint)Palette.Length];

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(color), null,
                new WpfPoint(size / 2.0, size / 2.0), size / 2.0, size / 2.0);

            var text = new WpfFormattedText(
                label.ToString(),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                size * 0.62,
                Brushes.White,
                1.0);

            dc.DrawText(text, new WpfPoint((size - text.Width) / 2.0, (size - text.Height) / 2.0));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return hash;
    }

    private static readonly System.Windows.Media.Color[] Palette =
    [
        System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6), // xanh dương
        System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81), // xanh lá
        System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B), // cam
        System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6), // tím
        System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44), // đỏ
        System.Windows.Media.Color.FromRgb(0x06, 0xB6, 0xD4), // xanh ngọc
        System.Windows.Media.Color.FromRgb(0xEC, 0x48, 0x99), // hồng
        System.Windows.Media.Color.FromRgb(0x84, 0xCC, 0x16) // vàng chanh
    ];
}
