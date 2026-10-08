#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    ///     Những assembly BẮT BUỘC dùng chung với tiến trình Revit, vì type của chúng đi qua
    ///     ranh giới giữa loader và plugin: nạp bản thứ hai là gặp ngay lỗi kiểu "unable to
    ///     cast IExternalCommand to IExternalCommand".
    ///     <para>
    ///         Danh sách này cố ý HẸP. Bản đầu nhường cho Default MỌI assembly đã nạp sẵn, và
    ///         đo trên Revit 2026 cho thấy hậu quả: plugin tham chiếu Newtonsoft.Json 13.0.4
    ///         của chính nó vẫn nhận bản Revit ship trong thư mục cài. Như vậy là ném đi đúng
    ///         thứ khiến ALC đáng dùng, và tái tạo trên .NET 8 đúng cái hạn chế mà .NET
    ///         Framework không tránh được. Giờ chỉ Revit API + chính loader mới dùng chung;
    ///         thư viện nào plugin tự ship thì nạp private cho plugin đó.
    ///     </para>
    /// </summary>
    private static bool MustShareWithHost(string? assemblyName)
    {
        if (assemblyName is null) return false;

        return assemblyName.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("Autodesk.", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("AdWindows", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("UIFramework", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals(typeof(PluginLoadContext).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Thư viện WPF ghi đè metadata của DependencyProperty trên kiểu CÓ SẴN của WPF
    ///     (<c>TextBlock</c>…) trong static constructor. Registry đó là của cả tiến trình, nên bản
    ///     thứ hai của thư viện (sau reload, ở ALC mới) ném
    ///     <c>"PropertyMetadata is already registered for type 'TextBlock'"</c> ngay khi mở cửa sổ.
    ///     Nạp MỘT lần vào context mặc định và dùng chung cho mọi lần reload.
    ///     Đánh đổi: đổi phiên bản các thư viện này cần mở lại Revit.
    /// </summary>
    private static bool MustLoadOnce(string? assemblyName)
        => assemblyName is not null && (assemblyName.Equals("Wpf.Ui", StringComparison.OrdinalIgnoreCase)
                                        || assemblyName.StartsWith("Wpf.Ui.", StringComparison.OrdinalIgnoreCase));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (MustShareWithHost(assemblyName.Name)) return null;

        if (MustLoadOnce(assemblyName.Name))
        {
            var shared = LoadSharedOnce(assemblyName);
            if (shared is not null) return shared;
        }

        // Dependency riêng của plugin -> nạp PRIVATE vào context này để nó bị gỡ cùng lúc với
        // plugin, và để plugin dùng đúng PHIÊN BẢN nó build cùng. Resolver đọc .deps.json nên
        // chỉ trả về path cho thứ plugin thật sự ship; assembly của framework không nằm trong
        // đó, trả null và runtime tự lo qua Default như bình thường.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null) return null;

        // pluginDllPath là bản MIRROR của thư mục output (xem ShadowCopy.Mirror), nên dependency
        // nằm cạnh nó cũng là bản sao — nạp thẳng không khoá file thật. Dependency ở nơi khác
        // (NuGet cache) thì không ai build đè lên, cứ nạp thẳng.
        return LoadFromAssemblyPath(path);
    }

    private Assembly? LoadSharedOnce(AssemblyName assemblyName)
    {
        var existing = Default.Assemblies.FirstOrDefault(loaded => string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var path = _resolver.ResolveAssemblyToPath(assemblyName)
                   ?? Path.Combine(Path.GetDirectoryName(pluginDllPath)!, assemblyName.Name + ".dll");
        if (!File.Exists(path)) return null;

        // Bản của riêng nó, ngoài thư mục mirror: file này nằm lại cả phiên nên không được để
        // thư mục mirror của slot bị kẹt khi dọn.
        var sharedDirectory = Path.Combine(ShadowCopy.MirrorRoot, "shared");
        Directory.CreateDirectory(sharedDirectory);
        var sharedPath = Path.Combine(sharedDirectory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
        File.Copy(path, sharedPath);

        return Default.LoadFromAssemblyPath(sharedPath);
    }

    public Assembly LoadPlugin(string mirroredDllPath) => LoadFromAssemblyPath(mirroredDllPath);
}

/// <summary>
///     Runtime cho Revit 2025+ (.NET 8/10): nạp vào ALC collectible, gỡ được thật.
///     <para>
///         <b>Cố ý KHÔNG ép GC sau khi gỡ.</b> Bản đầu có vòng lặp tới 10 lần
///         <c>GC.Collect()</c> + <c>WaitForPendingFinalizers()</c> để kết luận ngay "đã thu
///         hồi chưa". Đo trên Revit 2026 thì nó vừa CHẬM (treo thread UI của Revit ~2,3 giây
///         mỗi lần reload) vừa KHÔNG ĐÁNG TIN — cùng một plugin, cùng một thao tác, có lần
///         thu được sau 480 ms, có lần 10 vòng vẫn chưa. Việc gỡ ALC vốn là bất đồng bộ.
///     </para>
///     <para>
///         Quan trọng hơn: thu hồi được hay chưa KHÔNG ảnh hưởng tính đúng đắn. Mỗi lần
///         reload tạo một ALC mới và nạp DLL mới, nên code chạy luôn là code mới nhất. Bản cũ
///         còn nằm lại chỉ là bộ nhớ. Vì vậy: gỡ xong là xong, còn việc đếm bản cũ chưa thu
///         hồi thì kiểm tra LƯỜI ở lần nạp sau (xem <see cref="CountStaleLoads"/>) — không
///         tốn gì và không bắt Revit đứng hình.
///     </para>
/// </summary>
internal sealed class AlcPluginRuntime : IPluginRuntime
{
    private readonly Dictionary<int, PluginLoadContext> _contexts = [];
    private readonly Dictionary<int, List<WeakReference>> _unloading = [];
    private readonly Dictionary<int, string> _dllPaths = [];
    private static bool _mirrorsPurged;

    public Assembly Load(PluginSlot slot)
    {
        Unload(slot.Index);

        _dllPaths[slot.Index] = slot.DllPath;

        // Dọn mirror của các lần nạp trước (và rác phiên trước, lần nạp đầu tiên) TRƯỚC khi
        // tạo mirror mới. Mirror của ALC vừa gỡ có thể chưa nhả — để lần sau.
        if (!_mirrorsPurged)
        {
            _mirrorsPurged = true;
            ShadowCopy.CleanupMirrors();
            try { Directory.Delete(Path.Combine(ShadowCopy.MirrorRoot, "shared"), recursive: true); } catch (Exception) { /* phiên Revit khác đang dùng */ }
        }
        else ShadowCopy.CleanupMirrors(keep: [], namePrefix: $"slot{slot.Index}-");

        var mirroredDll = ShadowCopy.Mirror(slot.DllPath, slot.Index);

        var context = new PluginLoadContext(mirroredDll);
        _contexts[slot.Index] = context;

        return context.LoadPlugin(mirroredDll);
    }

    public UnloadResult Unload(int slotIndex)
    {
        var reference = DetachContext(slotIndex);
        if (reference is null) return UnloadResult.NothingLoaded;

        if (!_unloading.TryGetValue(slotIndex, out var pending))
        {
            pending = [];
            _unloading[slotIndex] = pending;
        }

        pending.Add(reference);

        // Best-effort: bản shadow của lần nạp trước thường đã hết bị khoá sau khi GC thu hồi
        // ALC. Chưa nhả thì bỏ qua, lần sau dọn tiếp.
        if (_dllPaths.TryGetValue(slotIndex, out var dllPath)) ShadowCopy.CleanupOld(dllPath);

        return UnloadResult.Requested;
    }

    public int CountStaleLoads(int slotIndex)
    {
        if (!_unloading.TryGetValue(slotIndex, out var pending)) return 0;

        pending.RemoveAll(reference => !reference.IsAlive);
        return pending.Count;
    }

    /// <summary>
    ///     Gỡ context ra khỏi dictionary, gọi <c>Unload()</c>, rồi TRẢ VỀ — cố ý tách thành
    ///     method riêng và cấm inline.
    ///     <para>
    ///         Một BIẾN CỤC BỘ trỏ tới context vẫn được tính là reference sống cho tới hết
    ///         scope của method — đặc biệt ở build Debug, nơi JIT không rút ngắn tuổi thọ
    ///         biến. Nếu kiểm tra <see cref="WeakReference"/> ngay trong method còn giữ biến
    ///         đó thì không đời nào thấy nó chết. Đây là pattern chính thức trong tài liệu
    ///         .NET về assembly unloadability.
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
