#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MiniAppLoader.Core.Runtime;

/// <summary>
///     Một <see cref="AssemblyLoadContext"/> collectible riêng cho MỖI plugin (không dùng
///     chung một context cho tất cả), để reload/unload plugin A không đụng tới plugin B.
/// </summary>
internal sealed class PluginLoadContext(string pluginDllPath) : AssemblyLoadContext(isCollectible: true)
{
    /// <summary>
    ///     Đọc <c>&lt;TênPlugin&gt;.deps.json</c> nằm cạnh DLL để biết plugin kéo theo NuGet
    ///     package nào và tìm ra đường dẫn thật của chúng — thường KHÔNG nằm cạnh DLL plugin
    ///     mà ở NuGet global cache (<c>%userprofile%\.nuget\packages\…</c>).
    /// </summary>
    private readonly AssemblyDependencyResolver _resolver = new(pluginDllPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // ĐIỂM MẤU CHỐT: nếu một assembly cùng tên ĐÃ nằm trong Default context (RevitAPI,
        // RevitAPIUI, MiniAppLoader, hay bất kỳ lib nào Revit/loader đã nạp), phải nhường
        // cho Default để giữ nguyên type identity. Thiếu bước này là gặp ngay lỗi kiểu
        // "unable to cast IExternalCommand to IExternalCommand".
        foreach (var loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // Ngược lại: dependency riêng của plugin -> nạp PRIVATE vào context này để nó bị gỡ
        // cùng lúc với plugin. Nạp thẳng từ path (khoá file dependency đó) thay vì qua
        // stream: dependency NuGet hiếm khi bị rebuild lúc dev, và tránh cho nó cũng dính
        // Assembly.Location rỗng.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    public Assembly LoadPlugin(string dllPath, int slotIndex)
    {
        // Nạp qua bản shadow để Assembly.Location không rỗng — xem ShadowCopy.
        // Tên shadow cố định theo slot (ALC unload xong là nhả khoá) nên không cộng dồn rác.
        var shadowDll = ShadowCopy.Create(dllPath, $"slot{slotIndex}");
        return LoadFromAssemblyPath(shadowDll);
    }
}

/// <summary>
///     Runtime cho Revit 2025+ (.NET 8/10): nạp vào ALC collectible, gỡ được thật.
/// </summary>
internal sealed class AlcPluginRuntime : IPluginRuntime
{
    private readonly Dictionary<int, PluginLoadContext> _contexts = [];
    private readonly Dictionary<int, string> _dllPaths = [];

    public Assembly Load(PluginSlot slot)
    {
        Unload(slot.Index);

        _dllPaths[slot.Index] = slot.DllPath;

        var context = new PluginLoadContext(slot.DllPath);
        _contexts[slot.Index] = context;

        return context.LoadPlugin(slot.DllPath, slot.Index);
    }

    public UnloadResult Unload(int slotIndex)
    {
        var reference = DetachContext(slotIndex);
        if (reference is null) return UnloadResult.NothingLoaded;

        // Vòng GC này PHẢI nằm ngoài method đang giữ biến cục bộ trỏ tới context — xem
        // DetachContext. Thường 2 vòng là đủ; 10 chỉ để chắc chắn.
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // ALC vừa nhả khoá -> dọn luôn bản shadow của lần nạp trước.
        if (_dllPaths.TryGetValue(slotIndex, out var dllPath)) ShadowCopy.CleanupOld(dllPath);

        return reference.IsAlive ? UnloadResult.StillAlive : UnloadResult.Released;
    }

    /// <summary>
    ///     Gỡ context ra khỏi dictionary, gọi <c>Unload()</c>, rồi TRẢ VỀ — cố ý tách thành
    ///     method riêng và cấm inline.
    ///     <para>
    ///         <c>AssemblyLoadContext.Unload()</c> chỉ đánh dấu; context chỉ thật sự chết khi
    ///         GC không còn thấy reference nào tới nó. Một BIẾN CỤC BỘ trỏ tới context vẫn
    ///         được tính là reference sống cho tới hết scope của method — đặc biệt ở build
    ///         Debug, nơi JIT không rút ngắn tuổi thọ biến. Nếu gọi <c>GC.Collect()</c> ngay
    ///         trong method còn giữ biến đó thì không đời nào thu được, và slot sẽ báo
    ///         "Leaked" dù plugin hoàn toàn sạch.
    ///     </para>
    ///     <para>
    ///         Đây là pattern chính thức trong tài liệu .NET về assembly unloadability, và đã
    ///         gặp đúng triệu chứng này khi test thật trên Revit 2026.
    ///     </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? DetachContext(int slotIndex)
    {
        if (!_contexts.TryGetValue(slotIndex, out var context)) return null;

        _contexts.Remove(slotIndex);

        var reference = new WeakReference(context, trackResurrection: true);
        context.Unload();

        return reference;
    }
}
#endif
