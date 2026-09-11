using System;
using System.IO;
using System.Threading;

namespace MiniAppLoader.Core.Runtime;

/// <summary>
///     Copy DLL (+ .pdb nếu có) ra một bản "shadow" trong subfolder <c>~shadow\</c> nằm cạnh
///     bản gốc, rồi nạp từ bản shadow đó.
///     <para>
///         <b>Vì sao không nạp thẳng từ byte[] / stream:</b> nạp từ bộ nhớ đúng là không khoá
///         file gốc (đúng mục tiêu hot-reload), NHƯNG khiến <c>Assembly.Location</c> trả về
///         CHUỖI RỖNG — đây là hành vi chuẩn của .NET, không phải bug. Rất nhiều plugin thật
///         dùng <c>Assembly.GetExecutingAssembly().Location</c> để tự tìm thư mục chứa mình
///         (đọc config, ảnh, PDF…) và sẽ ném <c>ArgumentException</c> ngay trong
///         <c>OnStartup</c> khi gặp chuỗi rỗng.
///     </para>
///     <para>
///         <b>Đánh đổi đã biết và chấp nhận:</b> vì bản shadow nằm trong <c>~shadow\</c>,
///         <c>Location</c> của plugin trỏ vào thư mục con đó chứ KHÔNG phải thư mục output
///         thật. Plugin nào dùng <c>Location</c> để tìm resource nằm cạnh nó sẽ tìm sai chỗ.
///         Không có cách vẹn cả đôi đường: CLR luôn báo <c>Location</c> = đường dẫn vật lý đã
///         nạp. Các plugin-loader hot-reload khác (Prise, McMaster…) xử lý bằng cách không
///         dựa vào <c>Location</c> trong plugin và tài liệu hoá rõ giới hạn, thay vì cố lừa CLR.
///     </para>
/// </summary>
internal static class ShadowCopy
{
    private const string ShadowDirName = "~shadow";

    /// <summary>
    ///     Tạo bản shadow của <paramref name="dllPath"/> và trả về đường dẫn của nó.
    /// </summary>
    /// <param name="preferredSuffix">
    ///     Dùng tên CỐ ĐỊNH (theo slot) thay vì GUID mới mỗi lần, để không cộng dồn rác qua
    ///     từng lần reload. Chỉ truyền khi chắc bản shadow trước đã được giải phóng
    ///     (net8: sau khi ALC unload xong); nếu bản cũ còn bị khoá thì tự rơi về tên GUID.
    /// </param>
    public static string Create(string dllPath, string? preferredSuffix = null)
    {
        var directory = Path.GetDirectoryName(dllPath)!;
        var shadowDirectory = Path.Combine(directory, ShadowDirName);
        Directory.CreateDirectory(shadowDirectory);

        var shadowDll = Path.Combine(shadowDirectory, (preferredSuffix ?? Guid.NewGuid().ToString("N")) + ".dll");

        try
        {
            File.Copy(dllPath, shadowDll, overwrite: true);
        }
        catch (Exception exception) when (preferredSuffix is not null && IsFileLocked(exception))
        {
            // Bản shadow cũ chưa được giải phóng xong -> dùng tên GUID để không kẹt.
            shadowDll = Path.Combine(shadowDirectory, Guid.NewGuid().ToString("N") + ".dll");
            File.Copy(dllPath, shadowDll, overwrite: true);
        }

        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            try
            {
                File.Copy(pdbPath, Path.ChangeExtension(shadowDll, ".pdb"), overwrite: true);
            }
            catch (Exception exception) when (IsFileLocked(exception))
            {
                // Thiếu pdb chỉ mất số dòng trong stack trace, không đáng để hỏng cả lần nạp.
            }
        }

        return shadowDll;
    }

    /// <summary>Dọn (best-effort) các bản shadow cũ nằm cạnh <paramref name="dllPath"/>.</summary>
    public static void CleanupOld(string dllPath, string? exceptPath = null)
        => CleanupDirectory(Path.GetDirectoryName(dllPath), exceptPath);

    /// <summary>
    ///     Dọn theo thẳng thư mục chứa DLL gốc. Gọi lúc Revit vừa khởi động để quét rác còn
    ///     sót từ PHIÊN TRƯỚC — lúc đó chắc chắn không file nào đang bị phiên hiện tại khoá,
    ///     nên dọn được cả rác net48 để lại (net48 không unload được nên chỉ dọn được kiểu này).
    /// </summary>
    public static void CleanupDirectory(string? directory, string? exceptPath = null)
    {
        if (string.IsNullOrEmpty(directory)) return;

        var shadowDirectory = Path.Combine(directory!, ShadowDirName);
        if (!Directory.Exists(shadowDirectory)) return;

        foreach (var file in Directory.EnumerateFiles(shadowDirectory))
        {
            if (string.Equals(file, exceptPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception exception) when (IsFileLocked(exception)) { /* còn bị khoá, để lần sau */ }
        }

        try
        {
            if (Directory.GetFileSystemEntries(shadowDirectory).Length == 0) Directory.Delete(shadowDirectory);
        }
        catch (Exception exception) when (IsFileLocked(exception))
        {
            // Còn file bị khoá bên trong, hoặc đua với một lần Create khác — bỏ qua.
        }
    }

    /// <summary>
    ///     File đang bị khoá biểu hiện thành HAI exception khác nhau trên Windows, tuỳ thao
    ///     tác: <see cref="IOException"/> khi mở, nhưng <see cref="UnauthorizedAccessException"/>
    ///     khi XOÁ một DLL vẫn còn được map làm assembly image. Bắt sót vế thứ hai từng khiến
    ///     việc dọn rác (vốn best-effort) ném lỗi ra ngoài và giết cả lần reload.
    /// </summary>
    private static bool IsFileLocked(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    /// <summary>
    ///     Chờ tới khi <paramref name="path"/> mở đọc được độc quyền.
    ///     <para>
    ///         MSBuild ghi DLL kiểu ghi-tạm-rồi-đổi-tên và có thể ghi nhiều nhịp liên tiếp.
    ///         Nếu nạp ngay khi FileSystemWatcher báo, ta sẽ đọc phải file viết dở và nhận
    ///         <c>BadImageFormatException</c> — đây chính là lý do debounce đơn thuần theo
    ///         thời gian là không đủ.
    ///     </para>
    /// </summary>
    public static bool WaitUntilReadable(string path, int attempts = 20, int delayMilliseconds = 100)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (Exception exception) when (IsFileLocked(exception))
            {
                Thread.Sleep(delayMilliseconds);
            }
        }

        return false;
    }
}
