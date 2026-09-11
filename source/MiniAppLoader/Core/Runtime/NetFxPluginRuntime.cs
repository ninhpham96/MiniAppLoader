#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MiniAppLoader.Core.Runtime;

/// <summary>
///     Runtime cho Revit ≤2024 (.NET Framework), nơi không có collectible
///     <c>AssemblyLoadContext</c>.
///     <para>
///         Chiến lược: mỗi lần reload nạp một assembly MỚI từ một đường dẫn shadow mới; bản
///         cũ nằm lại trong tiến trình (rò rỉ nhẹ) nhưng mọi reflection đều trỏ bản mới nhất.
///         Chấp nhận được khi dev, KHÔNG dùng cho production.
///     </para>
///     <para>
///         <b>Giới hạn cứng không vượt qua được từ phía loader:</b> chỉ DLL chính được nạp
///         mới thật sự. Các DLL dependency của nó được CLR phân giải qua tham chiếu tĩnh, và
///         CLR sẽ TÁI SỬ DỤNG bản đã nạp nếu thấy assembly cùng tên + cùng
///         <c>AssemblyVersion</c> — nó thậm chí không buồn gọi lại <c>AssemblyResolve</c>.
///         Nên plugin nhiều project (Foo.App tham chiếu Foo.Model, Foo.View…) mà các project
///         con có <c>AssemblyVersion</c> cố định thì sửa code trong project con sẽ KHÔNG có
///         tác dụng cho tới khi restart Revit. Cách duy nhất giải quyết triệt để là AppDomain
///         riêng, nhưng object của Revit API rất khó marshal qua ranh giới AppDomain.
///     </para>
/// </summary>
internal sealed class NetFxPluginRuntime : IPluginRuntime
{
    private readonly Dictionary<int, Assembly> _loaded = new();

    /// <summary>
    ///     Thư mục của mọi plugin đã từng nạp, dùng để tự dò dependency khi CLR không tìm
    ///     thấy — <c>LoadFile</c> không probe cạnh nó như <c>LoadFrom</c>.
    /// </summary>
    private readonly HashSet<string> _pluginDirectories = new(StringComparer.OrdinalIgnoreCase);

    private bool _resolveHooked;

    public Assembly Load(PluginSlot slot)
    {
        HookResolveOnce();

        var directory = Path.GetDirectoryName(slot.DllPath);
        if (!string.IsNullOrEmpty(directory)) _pluginDirectories.Add(directory);

        ShadowCopy.CleanupOld(slot.DllPath);
        var shadowDll = ShadowCopy.Create(slot.DllPath);

        // LoadFile chứ KHÔNG PHẢI LoadFrom, vì hai lý do:
        //  1. LoadFrom tự probe + khoá mọi DLL cùng thư mục mà assembly này tham chiếu
        //     (bỏ qua hẳn AssemblyResolve bên dưới) -> khoá luôn các DLL project thật dù
        //     bản chính đã đi qua shadow-copy.
        //  2. LoadFrom có thể trả về bản ĐÃ nạp trước đó nếu trùng identity Name+Version ->
        //     "reload xong mà vẫn chạy code cũ". LoadFile luôn tạo instance mới từ đúng path.
        var assembly = Assembly.LoadFile(shadowDll);
        _loaded[slot.Index] = assembly;
        return assembly;
    }

    public UnloadResult Unload(int slotIndex)
    {
        if (!_loaded.Remove(slotIndex)) return UnloadResult.NothingLoaded;

        // .NET Framework không unload được assembly khỏi AppDomain hiện tại. Đây là giới hạn
        // nền tảng đã biết, không phải lỗi -> báo NotSupported để UI không hiện "Leaked".
        return UnloadResult.NotSupported;
    }

    private void HookResolveOnce()
    {
        if (_resolveHooked) return;
        _resolveHooked = true;

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var simpleName = new AssemblyName(args.Name).Name;

            foreach (var directory in _pluginDirectories)
            {
                var candidate = Path.Combine(directory, simpleName + ".dll");
                if (!File.Exists(candidate)) continue;

                // Shadow-copy luôn cả dependency: với plugin nhiều project, các "dependency"
                // thực ra là code CỦA BẠN, được build lại cùng lúc với DLL chính — phải
                // tránh khoá bản gốc y hệt DLL chính, không chỉ NuGet package tĩnh.
                return Assembly.LoadFile(ShadowCopy.Create(candidate));
            }

            return null;
        };
    }
}
#endif
