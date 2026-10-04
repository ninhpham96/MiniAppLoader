using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using MiniAppLoader.Core.Runtime;
using Serilog;

namespace MiniAppLoader.Core;

/// <summary>
///     Hợp đồng tuỳ chọn cho plugin muốn TỰ chạy lại sau mỗi lần build (auto-reload).
///     Chỉ cần <see cref="UIApplication"/> — thứ duy nhất có sẵn trong
///     <c>IExternalEventHandler</c>.
/// </summary>
public interface IHotCommand
{
    void Execute(UIApplication application);
}

/// <summary>
///     Nạp / gỡ / khởi tạo entry point của plugin. Đây là nơi duy nhất đụng tới
///     <see cref="IPluginRuntime"/>; phần còn lại của loader không cần biết đang chạy trên
///     .NET Framework hay .NET Core.
/// </summary>
internal sealed class PluginLoader
{
#if NET8_0_OR_GREATER
    private readonly IPluginRuntime _runtime = new AlcPluginRuntime();
#else
    private readonly IPluginRuntime _runtime = new NetFxPluginRuntime();
#endif

    private readonly Dictionary<int, Assembly> _assemblies = [];

    /// <summary>Dấu vết (giờ ghi + kích thước) của DLL gốc tại lần nạp gần nhất của từng slot.</summary>
    private readonly Dictionary<int, (DateTime WrittenUtc, long Length)> _loadedStamps = [];

    /// <summary>
    ///     <see langword="true"/> khi slot chưa nạp lần nào hoặc DLL gốc đã đổi kể từ lần nạp
    ///     gần nhất. Dùng để KHÔNG nạp lại vô ích mỗi lần bấm nút: mỗi lần nạp là một bản shadow
    ///     mới, một ALC mới và một bản cũ chờ GC.
    /// </summary>
    public bool NeedsReload(PluginSlot slot)
    {
        if (!_assemblies.ContainsKey(slot.Index) || !_loadedStamps.TryGetValue(slot.Index, out var stamp)) return true;

        try
        {
            var info = new FileInfo(slot.DllPath);
            return !info.Exists || info.LastWriteTimeUtc != stamp.WrittenUtc || info.Length != stamp.Length;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    ///     Instance <c>IExternalApplication</c> đang chạy của các slot Kind=Application, cùng
    ///     bản <see cref="UIControlledApplication"/> đã trao cho nó.
    ///     <para>
    ///         Phải giữ ĐÚNG một instance <c>UIControlledApplication</c> cho suốt đời một
    ///         plugin: <c>OnStartup</c> và <c>OnShutdown</c> của nó phải nhận cùng một
    ///         object, nếu không lệnh huỷ đăng ký sự kiện bên trong plugin sẽ nhắm vào
    ///         instance khác và để lại delegate treo vào assembly đã gỡ.
    ///     </para>
    /// </summary>
    private readonly Dictionary<int, (IExternalApplication Instance, UIControlledApplication Application)> _applications = [];

    /// <summary>Nạp lại DLL của slot. Ném nếu file hỏng/không đọc được — phía gọi ghi vào slot.LastError.</summary>
    public Assembly Reload(PluginSlot slot)
    {
        var stopwatch = Stopwatch.StartNew();

        // Phân biệt "không có file" với "file đang bị khoá" TRƯỚC khi chờ: FileNotFoundException
        // là con của IOException, nên nếu không tách ra thì vòng chờ sẽ thử 20 lần một file
        // không tồn tại rồi báo "đang bị ghi/khoá" — sai hoàn toàn, và đây là ca rất hay gặp
        // (đổi nhánh, clean solution, sửa dllPath lệch đường dẫn).
        if (!File.Exists(slot.DllPath))
        {
            throw new FileNotFoundException(
                $"Không tìm thấy '{slot.DllPath}'. Plugin đã được build chưa, hay đường dẫn trong " +
                "plugins.json đã cũ?", slot.DllPath);
        }

        if (!ShadowCopy.WaitUntilReadable(slot.DllPath))
        {
            throw new IOException($"'{slot.DllPath}' vẫn đang bị ghi/khoá sau nhiều lần thử — bỏ qua lần reload này.");
        }

        // PHẢI nhả tham chiếu tới assembly cũ TRƯỚC khi unload: một object Assembly giữ
        // sống chính AssemblyLoadContext sinh ra nó, nên chỉ cần dictionary này còn trỏ tới
        // bản cũ là ctx.Unload() + GC.Collect không bao giờ thu được — slot sẽ báo "Leaked"
        // dù plugin hoàn toàn sạch. Đã gặp thật khi test trên Revit 2026.
        _assemblies.Remove(slot.Index);
        _loadedStamps.Remove(slot.Index);

        _runtime.Unload(slot.Index);

        Assembly assembly;
        try
        {
            assembly = _runtime.Load(slot);
        }
        catch (BadImageFormatException exception)
        {
            // Thông báo gốc chỉ vào bản SHADOW ("…\~shadow\slot3.dll") — một file người dùng
            // chưa từng nghe tới, đi tìm cũng không hiểu gì. Nói tên DLL mà HỌ khai báo.
            throw new BadImageFormatException(
                $"'{slot.DllPath}' không phải assembly .NET hợp lệ (file hỏng, hoặc sai kiến trúc " +
                "x86/x64, hoặc thật ra là file khác bị đặt đuôi .dll).", slot.DllPath, exception);
        }

        _assemblies[slot.Index] = assembly;

        var loadedInfo = new FileInfo(slot.DllPath);
        _loadedStamps[slot.Index] = (loadedInfo.LastWriteTimeUtc, loadedInfo.Length);

        stopwatch.Stop();
        slot.LastLoadedAt = DateTime.Now;
        slot.LastLoadDuration = stopwatch.Elapsed;
        slot.Status = PluginStatus.Loaded;
        slot.LastError = null;

        // Kiểm tra LƯỜI, sau khi đã nạp xong: đếm xem bản cũ nào còn chưa được GC thu hồi.
        // Không ép GC ở đây — xem AlcPluginRuntime để biết vì sao.
        slot.StaleLoads = _runtime.CountStaleLoads(slot.Index);

        Log.Information("Đã nạp '{Id}' trong {Ms} ms{Stale}", slot.Id, stopwatch.ElapsedMilliseconds,
            slot.StaleLoads > 0 ? $" ({slot.StaleLoads} bản cũ chưa thu hồi)" : string.Empty);
        return assembly;
    }

    /// <summary>Đường dẫn file của assembly đang nạp cho slot (bản shadow), hoặc null nếu chưa nạp.</summary>
    public string? LoadedLocation(PluginSlot slot)
        => _assemblies.TryGetValue(slot.Index, out var assembly) ? assembly.Location : null;

    /// <summary>Tạo instance <c>IExternalCommand</c> của plugin từ assembly đang nạp.</summary>
    public IExternalCommand CreateCommand(PluginSlot slot, string? preferredClassName)
    {
        var assembly = _assemblies[slot.Index];

        // Slot gắn với một command cụ thể (DLL nhiều command) mà class đó đã biến mất sau
        // build thì phải báo lỗi — lẳng lặng chạy class đầu tiên sẽ khiến nút "B" chạy lệnh "A".
        if (!string.IsNullOrEmpty(preferredClassName) &&
            !FindAllEntryPoints(assembly, typeof(IExternalCommand)).Any(candidate => candidate.FullName == preferredClassName))
        {
            throw new InvalidOperationException(
                $"Không còn class '{preferredClassName}' trong {assembly.GetName().Name} — đã đổi tên hoặc xoá? " +
                "Gỡ plugin rồi thêm lại để quét lại danh sách command.");
        }

        var type = FindEntryPoint(assembly, typeof(IExternalCommand), preferredClassName)
                   ?? throw new InvalidOperationException(
                       $"Không tìm thấy class nào implement IExternalCommand trong {assembly.GetName().Name}.");

        return (IExternalCommand)Activator.CreateInstance(type)!;
    }

    /// <summary>
    ///     Liệt kê entry point trong assembly đang nạp của slot: có <c>IExternalApplication</c>
    ///     hay không, và FullName của MỌI class <c>IExternalCommand</c>. Dùng khi thêm DLL để
    ///     quyết định tạo tab riêng (Application) hay một nút cho mỗi command.
    /// </summary>
    public (bool HasApplication, IReadOnlyList<(string FullName, string Name)> Commands) Discover(PluginSlot slot)
    {
        var assembly = _assemblies[slot.Index];
        var hasApplication = FindEntryPoint(assembly, typeof(IExternalApplication), preferredClassName: null) is not null;
        var commands = FindAllEntryPoints(assembly, typeof(IExternalCommand))
            .Select(type => (type.FullName ?? type.Name, type.Name))
            .ToList();

        return (hasApplication, commands);
    }

    /// <summary>Instance <see cref="IHotCommand"/> nếu plugin có, để tự chạy lại sau build.</summary>
    public IHotCommand? CreateHotCommand(PluginSlot slot)
    {
        var type = FindEntryPoint(_assemblies[slot.Index], typeof(IHotCommand), preferredClassName: null);
        return type is null ? null : (IHotCommand)Activator.CreateInstance(type)!;
    }

    /// <summary>
    ///     Nạp và gọi <c>OnStartup</c> cho plugin <c>Kind=Application</c>, đồng thời chụp ảnh
    ///     ribbon trước/sau để biết plugin đã thêm những gì.
    /// </summary>
    public void StartApplication(PluginSlot slot, PluginEntry entry, UIControlledApplication controlled)
    {
        Reload(slot);

        var assembly = _assemblies[slot.Index];
        var type = FindEntryPoint(assembly, typeof(IExternalApplication), entry.CommandClassName)
                   ?? throw new InvalidOperationException(
                       $"Không tìm thấy class nào implement IExternalApplication trong {assembly.GetName().Name}.");

        // Giữ controlled app TRƯỚC khi gọi OnStartup: nếu OnStartup ném, lần reload sau vẫn
        // phải gỡ được phần ribbon plugin kịp dựng, mà gỡ thì cần đúng object này.
        _controlled[slot.Index] = controlled;

        var before = RibbonDiff.Snapshot();

        PreloadIntoDefaultContext(assembly, slot);

        // Plugin hay hỏng OnStartup vì thiếu file icon (thư mục output không kèm Resources).
        // Gặp vậy thì tạo icon mặc định đúng chỗ plugin tìm rồi chạy lại OnStartup — mỗi lượt
        // vá được một file, nên lặp cho tới khi hết file thiếu (có trần để khỏi lặp vô hạn).
        for (var attempt = 0; ; attempt++)
        {
            var instance = (IExternalApplication)Activator.CreateInstance(type)!;
            var retrying = false;

            try
            {
                instance.OnStartup(controlled);
                _applications[slot.Index] = (instance, controlled);
                return;
            }
            catch (Exception exception)
            {
                // Chỉ vá file nằm trong thư mục của plugin: đó là chỗ Resources lẽ ra phải ở,
                // và không để loader tạo file ở nơi tuỳ ý trên đĩa chỉ vì plugin dò một đường dẫn.
                var missing = attempt < MaxIconRepairs ? FindMissingImagePath(exception) : null;
                if (missing is null || !IsUnder(Path.GetDirectoryName(slot.DllPath), missing) || !PluginIcon.TryWritePlaceholder(missing, Path.GetFileNameWithoutExtension(missing))) throw;

                Log.Warning("'{Id}' thiếu file ảnh '{Path}' — đã tạo icon mặc định, chạy lại OnStartup",
                    slot.Id, missing);

                // Gỡ phần ribbon plugin kịp dựng dở để OnStartup lượt sau không báo trùng tên.
                RibbonDiff.RemoveAdded(controlled, before, RibbonDiff.Snapshot(), [], []);
                retrying = true;
            }
            finally
            {
                // Chụp cả khi OnStartup ném. Plugin thường đã dựng xong tab + vài panel rồi mới
                // hỏng ở panel tiếp theo; không ghi lại thì phần đã dựng thành rác vĩnh viễn và
                // mọi lần nạp sau đều ném "The tab with the input name exists already".
                // Riêng lượt sắp chạy lại thì chưa chụp: ribbon vừa được dọn sạch.
                if (!retrying) _ribbonSnapshots[slot.Index] = (before, RibbonDiff.Snapshot());
            }
        }
    }

    private const int MaxIconRepairs = 50;

    /// <summary>
    ///     Nạp sẵn assembly của plugin Application vào context MẶC ĐỊNH của Revit, để nút ribbon
    ///     của nó bấm được.
    ///     <para>
    ///         Revit chạy command của một nút bằng cách nạp assembly theo TÊN vào context mặc
    ///         định. Khi chưa có gì trong đó, lời gọi rơi xuống các handler <c>AssemblyResolve</c>
    ///         có sẵn của Autodesk (<c>RevitPnIDIteropRibbonPanel</c>,
    ///         <c>FabPartBrowserApplication</c>…) — chúng quét mọi assembly đang nạp trong process,
    ///         kể cả bản trong ALC collectible của loader, rồi trả về chính bản đó. Đưa một
    ///         assembly collectible vào context mặc định thì .NET ném
    ///         <c>"Operation is not supported (0x80131515)"</c>, và nút ribbon bấm không phản ứng
    ///         gì. Đã tái hiện bằng <c>Assembly.Load("Test1")</c> trong Revit 2026 — và sau khi nạp
    ///         sẵn thì cùng nút đó chạy.
    ///     </para>
    ///     <para>
    ///         Hệ quả phải chấp nhận: command của plugin Application chạy từ bản đầu tiên Revit nạp
    ///         và KHÔNG đổi khi reload — chỉ phần OnStartup/ribbon trong ALC được làm mới. Đây không
    ///         phải do cách nạp sẵn này: đã thử trả bản mới nhất qua
    ///         <c>AssemblyLoadContext.Default.Resolving</c> (ALC không collectible) thì Revit vẫn nhớ
    ///         assembly theo tên từ lần nạp đầu và chạy bản cũ. Add-in Revit thường cũng vậy; muốn
    ///         code command mới phải mở lại Revit, hoặc dùng plugin Command (nạp lại được).
    ///     </para>
    /// </summary>
    private static void PreloadIntoDefaultContext(Assembly assembly, PluginSlot slot)
    {
#if NET8_0_OR_GREATER
        try
        {
            // Một identity chỉ nạp được MỘT lần vào context mặc định (LoadFrom ném "Assembly with
            // same name is already loaded" nếu nạp thêm bản thứ hai). Lần reload thứ hai trở đi
            // đã có bản đầu tiên ở đó, và command của nút sẽ tiếp tục chạy bản đó.
            var name = assembly.GetName().Name;
            if (System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Any(loaded => loaded.GetName().Name == name))
            {
                Log.Warning("'{Id}': ribbon đã dựng lại, nhưng code của các nút vẫn là bản nạp lần đầu — " +
                            "mở lại Revit để chạy bản build mới", slot.Id);
                return;
            }

            Assembly.LoadFrom(assembly.Location);
        }
        catch (Exception exception)
        {
            Log.Warning(exception,
                "'{Id}': không nạp sẵn được assembly vào context mặc định — nút ribbon của plugin có thể bấm không chạy",
                slot.Id);
        }
#endif
    }

    private static bool IsUnder(string? directory, string path)
    {
        if (string.IsNullOrEmpty(directory)) return false;

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico"];

    /// <summary>
    ///     Đường dẫn file ảnh mà exception (hoặc inner của nó) báo là không tồn tại, hoặc
    ///     <see langword="null"/> nếu lỗi này không phải do thiếu ảnh.
    /// </summary>
    private static string? FindMissingImagePath(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string? path = null;

            if (current is FileNotFoundException { FileName: { } fileName }) path = fileName;
            else if (current is DirectoryNotFoundException or FileNotFoundException)
            {
                // Thông báo dạng "Could not find a part of the path 'X'." / "Could not find file 'X'."
                var message = current.Message;
                var start = message.IndexOf('\'');
                var end = message.LastIndexOf('\'');
                if (start >= 0 && end > start) path = message.Substring(start + 1, end - start - 1);
            }

            if (path is not null && Path.IsPathRooted(path) &&
                ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    ///     Gọi <c>OnShutdown</c> và (tuỳ chọn) gỡ ribbon plugin đã tạo. Không ném: đang trên
    ///     đường reload hoặc đang đóng Revit, nuốt lỗi tốt hơn là kẹt giữa chừng.
    /// </summary>
    /// <param name="removeRibbon">
    ///     Chỉ đúng khi RELOAD hoặc GỠ plugin — lúc đó ribbon cũ phải biến mất để bản mới dựng
    ///     lại được. Lúc ĐÓNG Revit thì bỏ qua: Revit đang tự tháo dỡ UI của nó, và
    ///     <c>ComponentManager.Ribbon</c> có thể đã null, nên gỡ vừa vô nghĩa vừa ném
    ///     NullReferenceException vào log mỗi lần thoát.
    /// </param>
    public void StopApplication(PluginSlot slot, PluginEntry entry, bool removeRibbon = true)
    {
        // OnShutdown chỉ gọi khi lần OnStartup trước ĐÃ chạy xong: gọi nó trên một plugin
        // hỏng giữa chừng thì nó phải dọn thứ nó chưa kịp tạo.
        if (_applications.TryGetValue(slot.Index, out var running))
        {
            try
            {
                running.Instance.OnShutdown(running.Application);
            }
            catch (Exception exception)
            {
                slot.LastError = "OnShutdown của plugin ném lỗi: " + exception.Message;
                Log.Warning(exception, "OnShutdown của '{Id}' ném lỗi", slot.Id);
            }

            _applications.Remove(slot.Index);
        }

        if (!removeRibbon) return;

        // Dọn ribbon KHÔNG phụ thuộc vào việc plugin có chạy được hay không. Bản trước
        // thoát ngay ở dòng đầu khi _applications rỗng, nên một lần OnStartup hỏng là kẹt
        // vĩnh viễn: ribbon rác ở lại, mọi lần reload sau đều hỏng đúng chỗ cũ, và hai field
        // override trong plugins.json không bao giờ được đọc tới. Phải restart Revit mới
        // thoát ra được — đã dựng lại được lỗi này trên Revit 2026.
        if (!_controlled.TryGetValue(slot.Index, out var controlled)) return;

        var hasOverride = entry.RibbonTabsToRemove.Count > 0 || entry.RibbonPanelsToRemove.Count > 0;
        if (!_ribbonSnapshots.TryGetValue(slot.Index, out var snapshot))
        {
            if (!hasOverride) return;
            snapshot = (RibbonSnapshot.Empty, RibbonSnapshot.Empty);
        }

        RibbonDiff.RemoveAdded(controlled, snapshot.Before, snapshot.After,
            entry.RibbonTabsToRemove, entry.RibbonPanelsToRemove);
        _ribbonSnapshots.Remove(slot.Index);
    }

    /// <summary>Gỡ hẳn slot (khi người dùng bấm Gỡ, hoặc lúc Revit đóng).</summary>
    public UnloadResult Unload(PluginSlot slot)
    {
        _assemblies.Remove(slot.Index);
        _loadedStamps.Remove(slot.Index);
        _controlled.Remove(slot.Index);
        return _runtime.Unload(slot.Index);
    }

    /// <summary>Ảnh chụp ribbon trước/sau <c>OnStartup</c> của từng slot Kind=Application.</summary>
    private readonly Dictionary<int, (RibbonSnapshot Before, RibbonSnapshot After)> _ribbonSnapshots = [];

    /// <summary>
    ///     <see cref="UIControlledApplication"/> của từng slot Kind=Application. Tách riêng
    ///     khỏi <c>_applications</c> vì phải dùng được cả khi <c>OnStartup</c> đã ném — lúc
    ///     đó không có instance nào nhưng ribbon vẫn cần dọn.
    /// </summary>
    private readonly Dictionary<int, UIControlledApplication> _controlled = [];

    /// <summary>
    ///     Tìm class cụ thể implement <paramref name="contract"/>. Ưu tiên
    ///     <paramref name="preferredClassName"/> khi DLL có nhiều ứng viên.
    /// </summary>
    private static Type? FindEntryPoint(Assembly assembly, Type contract, string? preferredClassName)
    {
        var candidates = FindAllEntryPoints(assembly, contract);

        if (!string.IsNullOrEmpty(preferredClassName))
        {
            return candidates.FirstOrDefault(type => type.FullName == preferredClassName) ?? candidates.FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private static List<Type> FindAllEntryPoints(Assembly assembly, Type contract)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Vài type không nạp được (thường do thiếu dependency) -> vẫn dùng những type
            // nạp thành công thay vì hỏng cả lần nạp.
            types = exception.Types.Where(type => type is not null).ToArray()!;
        }

        return types
            .Where(type => contract.IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .ToList();
    }
}
