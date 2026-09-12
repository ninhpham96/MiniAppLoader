using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>Một panel mà plugin thêm vào tab DÙNG CHUNG (vd "Add-Ins") — không xoá cả tab được.</summary>
public sealed class RibbonPanelRef
{
    public string TabName { get; set; } = string.Empty;
    public string PanelName { get; set; } = string.Empty;
}

/// <summary>Một entry trong <c>plugins.json</c>.</summary>
public sealed class PluginEntry
{
    public string Id { get; set; } = string.Empty;
    public string DllPath { get; set; } = string.Empty;
    public string? ButtonText { get; set; }

    /// <summary>
    ///     Icon cho nút trên panel "Plugins". Tuỳ chọn — bỏ trống thì loader tự tìm file
    ///     <c>&lt;tên-dll&gt;.png</c> cạnh DLL, rồi tự sinh icon (vòng tròn màu + chữ cái đầu)
    ///     nếu vẫn không có gì. Đường dẫn tương đối tính theo thư mục chứa DLL.
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>
    ///     Chỉ cần khi một DLL có NHIỀU class implement entry point và bạn muốn chỉ rõ class
    ///     nào. Bỏ trống thì lấy class đầu tiên tìm thấy qua reflection.
    /// </summary>
    public string? CommandClassName { get; set; }

    public bool AutoReload { get; set; } = true;

    /// <summary>"Command" (mặc định) hoặc "Application".</summary>
    public string Kind { get; set; } = nameof(PluginKind.Command);

    /// <summary>
    ///     Chỉ dùng khi <see cref="Kind"/> = Application, và chỉ như một OVERRIDE thủ công:
    ///     mặc định loader tự phát hiện ribbon plugin tạo ra bằng cách so ảnh chụp trước/sau
    ///     <c>OnStartup</c> (xem <see cref="RibbonDiff"/>), nên bình thường để trống.
    /// </summary>
    public List<string> RibbonTabsToRemove { get; set; } = [];

    /// <inheritdoc cref="RibbonTabsToRemove"/>
    public List<RibbonPanelRef> RibbonPanelsToRemove { get; set; } = [];
}

/// <summary>Nội dung file <c>plugins.json</c>.</summary>
public sealed class PluginConfigFile
{
    public List<PluginEntry> Plugins { get; set; } = [];
}

/// <summary>
///     Đọc/ghi <c>plugins.json</c>.
///     <para>
///         <b>File nằm ở <c>%AppData%\MiniAppLoader\&lt;RevitVersion&gt;\plugins.json</c></b>,
///         không phải cạnh DLL đã deploy như bản v1. Hai lý do:
///         <list type="number">
///             <item>
///                 Bộ cài MSI per-machine đặt add-in vào Program Files — user non-admin sẽ
///                 không ghi được config nếu nó nằm cạnh DLL.
///             </item>
///             <item>
///                 Tách theo version Revit vì một plugin net48 không nạp được trong Revit
///                 2026; dùng chung một file sẽ phải thêm cột "hợp với version nào" và cả UI
///                 lọc theo nó.
///             </item>
///         </list>
///     </para>
/// </summary>
public sealed class ConfigStore(string revitVersion, string? legacyConfigPath = null)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // camelCase để khớp đúng schema plugins.json của bản v1 (đọc thì đằng nào cũng
        // case-insensitive, nhưng file ghi ra nên giống bản cũ để copy qua lại được).
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MiniAppLoader", revitVersion, "plugins.json");

    /// <summary>
    ///     Đọc config. Không bao giờ ném: config hỏng chỉ nên làm mất danh sách plugin, chứ
    ///     không được kéo theo việc Revit disable cả add-in.
    /// </summary>
    public List<PluginEntry> Load()
    {
        try
        {
            MigrateLegacyConfigIfNeeded();

            if (!File.Exists(ConfigPath)) return [];

            var config = JsonSerializer.Deserialize<PluginConfigFile>(File.ReadAllText(ConfigPath), ReadOptions);
            var entries = config?.Plugins ?? [];

            // Entry trỏ tới file không còn tồn tại vẫn giữ lại (hiện trạng thái Error trong
            // Hub) — người dùng có thể chỉ đang checkout nhánh khác, xoá hộ là mất công khai báo lại.
            return entries.Where(entry => !string.IsNullOrWhiteSpace(entry.DllPath)).ToList();
        }
        catch (JsonException exception)
        {
            // File này người dùng sửa tay, và lỗi hay gặp nhất là quên nhân đôi backslash
            // trong đường dẫn Windows ("C:\dev\..." thay vì "C:\\dev\\..."). Stack trace của
            // System.Text.Json dài và vô dụng với người đọc — nói thẳng dòng nào và cách sửa,
            // vì đây là thứ hiện ngay trong khung nhật ký của Plugin Hub.
            Log.Error("plugins.json sai cú pháp ở dòng {Line}: {Reason}", exception.LineNumber + 1, exception.Message);
            Log.Error("Mẹo: trong JSON, đường dẫn Windows phải nhân đôi backslash " +
                      @"(""C:\\dev\\MyPlugin.dll""), hoặc dùng dấu / (""C:/dev/MyPlugin.dll""). " +
                      "Danh sách plugin tạm để trống cho tới khi sửa xong: {Path}", ConfigPath);
            return [];
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Không đọc được {Path}", ConfigPath);
            return [];
        }
    }

    /// <summary>Ghi config theo kiểu ghi-tạm-rồi-thay, để không để lại file rỗng khi Revit crash giữa chừng.</summary>
    public void Save(IEnumerable<PluginEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

            var temporaryPath = ConfigPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new PluginConfigFile { Plugins = entries.ToList() }, WriteOptions));

            if (File.Exists(ConfigPath)) File.Replace(temporaryPath, ConfigPath, destinationBackupFileName: null);
            else File.Move(temporaryPath, ConfigPath);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Không ghi được {Path}", ConfigPath);
        }
    }

    /// <summary>Lần đầu chạy v2: mang danh sách plugin của v1 (nằm cạnh DLL) sang chỗ mới.</summary>
    private void MigrateLegacyConfigIfNeeded()
    {
        if (legacyConfigPath is null || File.Exists(ConfigPath) || !File.Exists(legacyConfigPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.Copy(legacyConfigPath, ConfigPath);
        Log.Information("Đã chuyển plugins.json của bản cũ từ {From} sang {To}", legacyConfigPath, ConfigPath);
    }
}
