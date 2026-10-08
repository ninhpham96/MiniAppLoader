using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MiniAppLoader.Core.Runtime;

/// <summary>
///     Copy DLL (+ .pdb nếu có) thành một bản "shadow" đặt ngay CẠNH bản gốc
///     (<c>Tên~slot0.shadow</c>), rồi nạp từ bản shadow đó.
///     <para>
///         <b>Vì sao không nạp thẳng từ byte[] / stream:</b> nạp từ bộ nhớ đúng là không khoá
///         file gốc (đúng mục tiêu hot-reload), NHƯNG khiến <c>Assembly.Location</c> trả về
///         CHUỖI RỖNG — đây là hành vi chuẩn của .NET, không phải bug. Rất nhiều plugin thật
///         dùng <c>Assembly.GetExecutingAssembly().Location</c> để tự tìm thư mục chứa mình
///         (đọc config, ảnh, PDF, DLL anh em…) và sẽ ném lỗi ngay trong <c>OnStartup</c>.
///     </para>
///     <para>
///         <b>Vì sao đặt cạnh bản gốc mà không phải thư mục con:</b> bản đầu để shadow trong
///         <c>~shadow\</c>, nên <c>Location</c> trỏ vào thư mục con đó và mọi đường dẫn tương
///         đối của plugin (DLL anh em, <c>Resources\</c>, thư mục cha…) đều lệch. Đặt cạnh bản
///         gốc thì <c>Location</c> nằm đúng thư mục output thật. Cái giá là vài file
///         <c>*~slotN.shadow/.pdb</c> trong thư mục output, được dọn ở lần mở Revit sau.
///     </para>
/// </summary>
internal static class ShadowCopy
{
    /// <summary>Thư mục shadow của các bản cũ — không còn tạo nữa, nhưng vẫn dọn rác nó để lại.</summary>
    private const string ShadowDirName = "~shadow";

    /// <summary>Dấu trong tên file shadow đặt CẠNH DLL gốc: <c>Tên~slot0.shadow</c> (hoặc <c>Tên~slot-GUID.shadow</c>).</summary>
    private const string SiblingMarker = "~slot";

    /// <summary>
    ///     Đuôi của bản shadow cạnh DLL gốc — cố ý KHÔNG phải <c>.dll</c>. Các bước build hay quét
    ///     <c>*.dll</c> trong thư mục output (ILRepack của template Nice3point gom MỌI dll ở đó
    ///     thành một); thấy <c>Tên~slot0.dll</c> là nó nhét luôn bản trùng này vào rồi build hỏng.
    ///     CLR nạp assembly từ đường dẫn bất kể đuôi file gì, và pdb vẫn đi theo tên
    ///     (<c>Tên~slot0.pdb</c>).
    /// </summary>
    private const string SiblingExtension = ".shadow";

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
        var shadowDirectory = Path.GetDirectoryName(dllPath)!;

        // Tên phải luôn chứa SiblingMarker để CleanupSiblings nhận ra và dọn được; bản dự
        // phòng tên GUID cũng vậy.
        string ShadowName(string suffix) =>
            Path.GetFileNameWithoutExtension(dllPath) + "~" + (suffix.StartsWith("slot", StringComparison.Ordinal) ? suffix : "slot-" + suffix) + SiblingExtension;

        var shadowDll = Path.Combine(shadowDirectory, ShadowName(preferredSuffix ?? Guid.NewGuid().ToString("N")));

        try
        {
            File.Copy(dllPath, shadowDll, overwrite: true);
        }
        catch (Exception exception) when (preferredSuffix is not null && IsFileLocked(exception))
        {
            // Bản shadow cũ chưa được giải phóng xong -> dùng tên GUID để không kẹt.
            shadowDll = Path.Combine(shadowDirectory, ShadowName(Guid.NewGuid().ToString("N")));
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

    /// <summary>Thư mục gốc chứa các bản sao nguyên thư mục output của plugin (chỉ dùng cho ALC).</summary>
    public static string MirrorRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniAppLoader", "mirror");

    /// <summary>
    ///     Sao CẢ thư mục output của plugin sang một thư mục tạm mới và trả về đường dẫn DLL chính
    ///     trong đó (giữ nguyên tên file gốc).
    ///     <para>
    ///         Shadow từng DLL (<see cref="Create"/>) chỉ bảo vệ được file do LOADER nạp. Plugin
    ///         tự nạp thêm từ thư mục chứa nó (vd. <c>LoadFromAssemblyPath</c> cho mọi
    ///         <c>*.dll</c> cạnh <c>Assembly.Location</c>) thì khoá thẳng bản gốc và build kế
    ///         tiếp của chính plugin báo "file đang được sử dụng". Cho plugin sống hẳn trong một
    ///         bản sao: <c>Assembly.Location</c> trỏ vào đó, nên mọi đường nạp — của loader lẫn
    ///         của plugin — chỉ chạm bản sao, thư mục output thật luôn ghi đè được.
    ///     </para>
    ///     <para>Mỗi lần nạp một thư mục MỚI: bản của lần nạp trước có thể còn bị khoá tới khi ALC cũ được GC.</para>
    /// </summary>
    public static string Mirror(string dllPath, int slotIndex)
    {
        var source = Path.GetDirectoryName(dllPath)!;
        var target = Path.Combine(MirrorRoot, $"slot{slotIndex}-{Guid.NewGuid():N}");

        CopyDirectory(source, target);
        return Path.Combine(target, Path.GetFileName(dllPath));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Rác shadow kiểu cũ nằm cạnh DLL gốc — không có lý do để mang theo.
            if (Path.GetFileName(file).Contains(SiblingMarker, StringComparison.Ordinal)) continue;

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory) == ShadowDirName) continue;

            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    /// <summary>
    ///     Xoá (best-effort) các thư mục mirror, trừ những thư mục trong <paramref name="keep"/>.
    ///     Thư mục còn bị khoá (ALC chưa nhả, hoặc phiên Revit khác) thì bỏ qua, lần sau dọn tiếp.
    /// </summary>
    public static void CleanupMirrors(IReadOnlyCollection<string>? keep = null, string? namePrefix = null)
    {
        if (!Directory.Exists(MirrorRoot)) return;

        foreach (var directory in Directory.EnumerateDirectories(MirrorRoot))
        {
            if (namePrefix is not null && !Path.GetFileName(directory).StartsWith(namePrefix, StringComparison.Ordinal)) continue;
            if (Path.GetFileName(directory) == "shared") continue; // đang nạp vào context mặc định
            if (keep is not null && keep.Contains(directory, StringComparer.OrdinalIgnoreCase)) continue;

            try { Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (IsFileLocked(exception)) { /* còn bị khoá, để lần sau */ }
        }
    }

    /// <summary>Dọn (best-effort) các bản shadow cũ nằm cạnh <paramref name="dllPath"/>.</summary>
    public static void CleanupOld(string dllPath, string? exceptPath = null)
        => CleanupDirectory(Path.GetDirectoryName(dllPath), exceptPath);

    /// <summary>
    ///     Dọn theo thẳng thư mục chứa DLL gốc. Gọi lúc Revit vừa khởi động để quét rác còn
    ///     sót từ PHIÊN TRƯỚC — lúc đó chắc chắn không file nào đang bị phiên hiện tại khoá,
    ///     nên dọn được cả rác net48 để lại (net48 không unload được nên chỉ dọn được kiểu này).
    /// </summary>
    /// <returns>
    ///     <see langword="true"/> nếu sau khi dọn vẫn còn rác (file đang bị khoá) — phía gọi nên
    ///     nhớ thư mục này để dọn tiếp ở lần sau.
    /// </returns>
    public static bool CleanupDirectory(string? directory, string? exceptPath = null)
    {
        if (string.IsNullOrEmpty(directory)) return false;

        var leftovers = CleanupSiblings(directory!, exceptPath);

        var shadowDirectory = Path.Combine(directory!, ShadowDirName);
        if (!Directory.Exists(shadowDirectory)) return leftovers;

        foreach (var file in Directory.EnumerateFiles(shadowDirectory))
        {
            if (string.Equals(file, exceptPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception exception) when (IsFileLocked(exception)) { /* còn bị khoá, để lần sau */ }
        }

        try
        {
            if (Directory.GetFileSystemEntries(shadowDirectory).Length == 0) Directory.Delete(shadowDirectory);
            else leftovers = true;
        }
        catch (Exception exception) when (IsFileLocked(exception))
        {
            // Còn file bị khoá bên trong, hoặc đua với một lần Create khác — bỏ qua.
            leftovers = true;
        }

        return leftovers;
    }

    /// <summary>
    ///     Dọn các bản shadow đặt cạnh DLL gốc (<c>*~slotN.shadow/.pdb</c>), kể cả <c>*~slotN.dll</c>
    ///     do bản trước tạo ra.
    /// </summary>
    private static bool CleanupSiblings(string directory, string? exceptPath)
    {
        var leftovers = false;
        if (!Directory.Exists(directory)) return leftovers;

        foreach (var file in Directory.EnumerateFiles(directory, "*" + SiblingMarker + "*"))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(SiblingExtension, StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;

            if (string.Equals(file, exceptPath, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (IsFileLocked(exception))
            {
                leftovers = true; // còn bị khoá, để lần sau
            }
        }

        return leftovers;
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
