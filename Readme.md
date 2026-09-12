# MiniAppLoader

Nạp và **hot-reload plugin Revit trong lúc Revit đang chạy** — sửa code, build, bấm nút,
thấy kết quả ngay, không restart Revit.

Viết lại từ [ninhpham96/AutoLoadAddin](https://github.com/ninhpham96/AutoLoadAddin) trên
scaffold [Nice3point.Revit.Templates](https://github.com/Nice3point/RevitTemplates) đầy đủ:
giữ nguyên năng lực loader, thay UI bằng **dockable pane**, và bổ sung bộ cài MSI.

Hỗ trợ **Revit 2022 → 2027** (net48 / net8.0-windows / net10.0-windows).

---

## Cài đặt

**Dùng bộ cài:** tải MSI ở phần Releases rồi chạy — chọn đúng các version Revit bạn cần.
Có hai bản: `SingleUser` (cài cho riêng bạn, không cần quyền admin) và `MultiUser`.

**Build từ source:**

```bash
dotnet build source/MiniAppLoader -c Debug.R26
```

`.addin` được Nice3point SDK tự deploy vào `%AppData%\Autodesk\Revit\Addins\<version>\` sau
mỗi lần build — không cần copy tay. Đổi `R26` thành `R22`…`R27` cho version khác. **Không
cần cài Revit để build** (RevitAPI kéo qua NuGet), chỉ cần để chạy.

Lần đầu mở Revit sẽ có hộp thoại *"Security - Unsigned Add-In"* vì add-in chưa ký số —
chọn **Always Load** (hoặc **Load Once** nếu chỉ muốn thử).

---

## Dùng

Ribbon có tab riêng **MiniApps** với 2 panel:

| Panel | Nội dung |
|---|---|
| `Hub` | Nút **Plugin Hub** — bật/tắt bảng quản lý |
| `Plugins` | Nút của từng plugin, tạo sẵn 16 slot và ẩn đi; thêm plugin là hiện một nút |

Bảng **Plugin Hub** (dockable pane, neo được như Properties/Project Browser):

- **+ Thêm** hoặc kéo thả thẳng file `.dll` vào pane
- Mỗi plugin là một card: chấm trạng thái, đường dẫn DLL, và **Chạy / Nạp lại / auto / Gỡ**
- Khung **Nhật ký** ở dưới hiện mọi sự kiện nạp/lỗi (thay cho kiểu bắn `TaskDialog` của bản cũ)
- Palette tự bám theme sáng/tối của Revit 2024+

Gỡ plugin thì nút biến mất **ngay**, slot được tái sử dụng cho plugin sau.

---

## `plugins.json`

Nằm ở `%AppData%\MiniAppLoader\<RevitVersion>\plugins.json` — **không** phải cạnh DLL như
bản cũ, vì bộ cài per-machine đặt add-in vào `Program Files` (user thường không ghi được ở
đó), và plugin net48 không dùng chung được với Revit 2026.

```json
{
  "plugins": [
    {
      "id": "MyPlugin",
      "dllPath": "D:/dev/MyPlugin/bin/Debug/net8.0-windows/MyPlugin.dll",
      "buttonText": "My Plugin",
      "autoReload": true,
      "kind": "Command"
    }
  ]
}
```

| Trường | Ý nghĩa |
|---|---|
| `kind` | `Command` (mặc định) — DLL có class `IExternalCommand`, chạy bằng nút ribbon.<br>`Application` — DLL có class `IExternalApplication` tự dựng ribbon riêng; loader gọi `OnStartup` ngay khi Revit mở, KHÔNG chiếm slot nút nào. |
| `autoReload` | Theo dõi file DLL, tự nạp lại khi build xong |
| `commandClassName` | Chỉ cần khi DLL có nhiều class entry point và bạn muốn chỉ rõ |
| `ribbonTabsToRemove`, `ribbonPanelsToRemove` | Override thủ công cho `kind: Application`. Bình thường **để trống** — loader tự phát hiện (xem bên dưới) |

Hai lưu ý khi sửa tay:

- Đường dẫn Windows trong JSON phải **nhân đôi backslash** (`"D:\\dev\\..."`) hoặc dùng dấu
  `/`. Sai cú pháp thì Hub để trống và khung nhật ký nói rõ sai ở dòng nào.
- **Đóng Revit trước khi sửa tay.** Hub sở hữu file này lúc đang chạy và sẽ ghi đè khi bạn
  thêm/gỡ plugin.

---

## Cơ chế

### Hai runtime

| | Revit 2022–2024 (net48) | Revit 2025+ (net8/net10) |
|---|---|---|
| Nạp | `Assembly.LoadFile` từ bản shadow | `AssemblyLoadContext` collectible, **một context cho mỗi plugin** |
| Gỡ | Không gỡ được (giới hạn nền tảng) | Gỡ thật, GC thu hồi |
| Dependency riêng | `AppDomain.AssemblyResolve` dò thư mục plugin | `AssemblyDependencyResolver` đọc `.deps.json` |

**Shadow copy.** DLL được copy sang `~shadow\` cạnh bản gốc rồi mới nạp, nên bản gốc không bị
khoá và build đè thoải mái. Nạp thẳng từ `byte[]` cũng không khoá file, nhưng khiến
`Assembly.Location` trả về **chuỗi rỗng** — rất nhiều plugin dùng `Location` để tự tìm thư mục
của mình và sẽ ném ngay trong `OnStartup`. Đánh đổi: `Location` trỏ vào `~shadow\`, không phải
thư mục output thật, nên đừng dùng `Location` để tìm resource nằm cạnh DLL.

**Theo dõi file.** `FileSystemWatcher` chỉ ghi slot vào một tập "bẩn" rồi raise **một**
`ExternalEvent` duy nhất. Cần vậy vì `ExternalEvent.Raise()` có tính gộp: build hai plugin
trong cùng một lần MSBuild mà mỗi slot một handler thì một lần reload sẽ bị nuốt mất.

Debounce là kiểu **trailing-edge**: mỗi nhịp ghi file đẩy lùi hẹn giờ, chỉ khi file im lặng
đủ 500 ms mới thực sự reload. Kiểu leading-edge (nhịp đầu kích hoạt, các nhịp sau bị bỏ) sẽ
nạp phải bản ghi dở rồi bỏ qua luôn bản cuối — mà triệu chứng là "reload xong vẫn thấy code
cũ", thứ khó ngờ nhất.

**Gỡ ribbon của `kind: Application`.** Loader chụp ảnh ribbon ngay trước và ngay sau
`OnStartup` của plugin, rồi khi reload chỉ gỡ đúng phần chênh lệch. Khai báo tay như bản cũ
chắc chắn lệch sau vài lần plugin đổi ribbon, và ghi nhầm tên một panel dùng chung là xoá mất
UI của add-in khác.

**Chạy plugin từ Hub.** Nút *Chạy* dùng `PostCommand` với ID ribbon nội bộ, tức là đi qua
đúng pipeline command của Revit — y hệt bạn bấm chuột. Không thể dựng `ExternalCommandData`
giả, và tuyệt đối không được giữ lại một cái cũ để dùng lại.

---

## Đã kiểm chứng ở đâu

Chạy thật qua [rvt-mcp](https://github.com/bimwright/rvt-mcp), không dừng ở "build sạch".

| | Revit 2024 (net48) | Revit 2026 (net8) |
|---|---|---|
| 4 giai đoạn khởi động | ✅ | ✅ |
| Dockable pane | ✅ | ✅ |
| 16 nút tạo sẵn + ẩn/hiện | ✅ | ✅ |
| `PostCommand` tra được ID ribbon | ✅ | ✅ |
| `kind: Command` — chạy + hot reload | ✅ | ✅ |
| `kind: Application` — `OnShutdown` → gỡ ribbon → `OnStartup` | ✅ | ✅ (4 vòng liên tiếp) |
| Ribbon của add-in khác còn nguyên sau reload | ✅ | ✅ |
| Thêm / gỡ / tái dùng slot | — | ✅ |
| Dependency riêng của plugin | ✅ (xem giới hạn) | ✅ |
| `IHotCommand` — tự chạy lại sau build, không chạm chuột | — | ✅ (2 vòng, ~540 ms sau khi build xong) |
| Kéo-thả DLL từ Explorer vào pane | — | ✅ (kể cả nhánh từ chối DLL không có entry point) |
| `ribbonPanelsToRemove` gỡ được panel diff không thấy | — | ✅ (sau khi sửa lỗi, xem dưới) |

### Nhánh lỗi (Revit 2026)

| Tình huống | Kết quả |
|---|---|
| `plugins.json` sai cú pháp | Báo đúng dòng + cách sửa; ribbon và pane vẫn dựng bình thường |
| DLL không tồn tại | *"Không tìm thấy '…'. Plugin đã được build chưa…"* |
| File không phải assembly .NET | Nêu đúng DLL bạn khai (không phải bản shadow) + các nguyên nhân thường gặp |
| DLL không có `IExternalCommand` | Nêu tên assembly, không phải `NullReferenceException` |
| Command ném exception lúc chạy | Bắt được, ghi log, slot chuyển Error, **Revit sống** |
| `OnStartup` của plugin Application ném | Ghi log, **các plugin còn lại vẫn nạp tiếp** |
| Thêm trùng DLL đã nạp | Từ chối, nêu tên plugin đang giữ nó |
| Cạn 16 slot | Từ chối, bảo gỡ bớt; config khai 20 thì lấy 16 và cảnh báo |
| Thêm thất bại | Slot được **trả lại pool**, không rò rỉ |
| Build 2 plugin trong 1 lần MSBuild | **Cả hai** cùng reload |
| Đóng Revit | `OnShutdown` của plugin chạy, log sạch không warning |

Khi plugin ném exception, loader trả `Result.Failed` kèm message nên **Revit hiện hộp
thoại lỗi chuẩn của nó** — giống hệt add-in cài bình thường. Lỗi vẫn được ghi song song vào
khung nhật ký và file log.

### Thao tác UI (Revit 2026, bấm chuột thật)

| Thao tác | Kết quả |
|---|---|
| Ô tìm kiếm | Lọc theo tên plugin và theo đường dẫn DLL; xoá ô lọc thì danh sách trở lại |
| **Chạy** | Plugin thực sự chạy (ghi mốc ra file) |
| **Nạp lại** | Sinh một lần nạp mới trong log |
| Ô **auto** | Ghi thẳng vào `plugins.json`: `autoReload` True → False → True |
| **Gỡ** | Config về 0 plugin, **nút trên ribbon biến mất ngay**, khối "chưa có plugin nào" hiện ra |
| **+ Thêm** | Mở đúng hộp thoại chọn file của add-in |
| **Xoá** nhật ký | Khung log về rỗng |
| Đổi theme Revit sáng↔tối | Pane đổi màu ngay, không cần khởi động lại |
| Reload khi Revit đang kẹt modal dialog | Hoãn lại (không nạp), đóng dialog xong mới nạp |
| `plugins.json` kiểu bản v1 nằm cạnh DLL | Tự chuyển sang `%AppData%\MiniAppLoader\<version>\` ngay lần chạy đầu |

Build sạch, 0 warning, trên cả 6 configuration `R22`…`R27`.

**Chưa kiểm chứng:** vòng hot-reload `kind: Command` đầy đủ trên net48. Revit 2023/2025/2027 chỉ build qua NuGet —
máy phát triển không cài. Bộ cài MSI mới chỉ soi cấu trúc bên trong, chưa cài thử.

---

## Một lỗi tìm ra khi kiểm chứng override ribbon

Hai field `ribbonTabsToRemove`/`ribbonPanelsToRemove` có code từ đầu nhưng chưa ai chạy thử.
Dựng đúng kịch bản chúng sinh ra để cứu — một panel lạ xuất hiện trên tab của plugin **sau**
khi `OnStartup` đã chạy xong, nên ribbon diff không quy được cho plugin — thì lộ ra hai lỗi
chồng nhau:

1. **Override tra panel trong ảnh chụp `after`.** Ảnh đó lấy ngay sau `OnStartup`, nên không
   bao giờ chứa panel xuất hiện muộn hơn — mà đó là trường hợp duy nhất cần tới override.
   Khai tường minh panel trong `plugins.json` cũng vô ích.
2. **`OnStartup` ném thì kẹt vĩnh viễn.** Snapshot và instance đều được gán *sau* lời gọi
   `OnStartup`, nên khi nó ném thì cả hai đều trống; lần reload sau `StopApplication` thoát
   ngay ở dòng đầu và không bao giờ dọn ribbon nữa. Phải restart Revit mới thoát.

Hậu quả thực tế: panel lạ chặn việc gỡ tab → `OnStartup` sau đó ném "The tab with the input
name exists already" → plugin chết cứng cho tới khi restart.

Đã sửa: override tra trên ribbon **hiện tại** thay vì ảnh chụp; snapshot ghi trong `finally`
nên vẫn có khi `OnStartup` ném; và việc dọn ribbon tách khỏi việc plugin có chạy được hay
không. Kiểm chứng trên Revit 2026: cùng kịch bản, override gỡ đúng panel lạ và reload chạy
lại bình thường; 3 vòng reload thường không hồi quy.

---

## Giới hạn đã đo

Những điều dưới đây là **kết quả đo thật**, không phải phỏng đoán.

**1. Plugin `kind: Application` rò rỉ một assembly mỗi lần reload.**
Trên Revit 2026, ALC của plugin loại này không bao giờ được thu hồi, kể cả sau full GC. Đã
tách nguyên nhân bằng thí nghiệm: cho plugin **không dựng ribbon nào** thì vẫn y hệt, nên
không phải do ribbon nó để lại. `kind: Command` thì thu hồi sạch. Hệ quả thực tế: reload vài
chục lần trong một phiên dev thì không sao; rất nhiều thì restart Revit. Hub hiện số này dưới
dạng "N bản cũ chưa thu hồi" — **chỉ là bộ nhớ**, code chạy luôn là bản mới nhất.

**2. Trên net48, plugin không dùng được phiên bản thư viện của riêng nó.**
Nếu Revit đã nạp một assembly cùng tên (ví dụ `Newtonsoft.Json`, Revit có ship sẵn), CLR sẽ
lấy bản đó và **không bao giờ gọi tới** `AssemblyResolve` của loader. Đây là cách một-AppDomain
hoạt động, loader không can thiệp được. Trên .NET 8 thì không bị: mỗi plugin nạp private đúng
bản nó build cùng.

**3. Trên net48, chỉ DLL chính được hot-reload thật sự.**
Các project con mà plugin tham chiếu (`Foo.Model`, `Foo.View`…) được CLR phân giải qua tham
chiếu tĩnh và sẽ **tái sử dụng bản đã nạp** nếu trùng tên + trùng `AssemblyVersion`. Sửa code
trong project con sẽ không có tác dụng cho tới khi restart Revit. Cách thực tế nhất khi dev là
gộp tạm code đang sửa vào DLL chính, hoặc chuyển sang Revit 2025+.

**4. Vài đăng ký của Revit không huỷ được.**
`FailureDefinition.CreateFailureDefinition` với GUID cố định là ví dụ điển hình: gọi lần hai
trong cùng một tiến trình là Revit ném. Nếu plugin không tự bọc `try/catch` quanh đó thì
reload sẽ báo lỗi. Không có cách chung nào xử lý.

**5. Gỡ ribbon dựa trên API nội bộ của Autodesk.**
`RemovePanel()` (Nice3point.Revit.Extensions) dùng `UnsafeAccessor` trên .NET 8 và reflection
trên net48. Autodesk không cam kết ổn định. Mọi thao tác đều bọc `try/catch`: hỏng thì ribbon
cũ còn sót lại, chứ add-in không chết.

---

## Khác gì bản cũ

| Bản cũ | Bản này |
|---|---|
| Tối đa 8 plugin, nút gỡ rồi vẫn nằm lại tới khi restart Revit | 16 slot, nút ẩn ngay và slot tái sử dụng được |
| Dialog gỡ plugin viết bằng WinForms | Dockable pane WPF + MVVM |
| Mọi thông báo bắn `TaskDialog` | Khung nhật ký trong pane + file log ở `%LocalAppData%\MiniAppLoader\logs\` |
| Phải liệt kê tay ribbon cần gỡ | Tự phát hiện bằng cách so ảnh chụp ribbon |
| `RibbonCleanup.cs` tự reflection vào field private của Revit | `RemovePanel()` của Nice3point.Revit.Extensions |
| `plugins.json` cạnh DLL deploy | `%AppData%\MiniAppLoader\<version>\` |
| Không có bộ cài | MSI per-user + per-machine, chọn được từng version Revit |
| Chỉ 2024 + 2025 | 2022 → 2027 |

---

## Phát triển

```
source/MiniAppLoader/   add-in
samples/SamplePlugin/         plugin mẫu kind=Command (multi-target net48 + net8)
samples/SampleApplication/    plugin mẫu kind=Application, tự dựng tab riêng
samples/SampleHotCommand/     plugin mẫu implement IHotCommand — tự chạy lại sau build
build/                  ModularPipelines: compile, đóng gói, publish
install/                WixSharp — sinh MSI
```

Đóng gói bộ cài:

```bash
dotnet run --project build -- pack
```

Tự cài `wix` tool, publish toàn bộ configuration `Release.Rxx`, rồi sinh MSI vào `output/`.
Đặt `Build.Version` trong `build/appsettings.json`; để trống thì dùng GitVersion.

Các plugin mẫu trong `samples/` dùng để thử loader: chúng ghi mốc ra file thay vì bật dialog,
vì dialog là modal và sẽ chặn Revit khiến mọi kiểm chứng tự động treo.

`SampleHotCommand` là ngoại lệ duy nhất phải build **sau** add-in: nó tham chiếu
`MiniAppLoader.dll` để lấy interface `IHotCommand`, nên cố ý KHÔNG nằm trong
`samples/Samples.sln` — `build -- pack` xoá sạch `bin/` và sẽ làm gãy solution mẫu.
Tham chiếu đó đặt `Private=false`: loader tìm entry point bằng `Type.IsAssignableFrom`,
nên plugin phải thấy đúng instance assembly host đã nạp, không phải bản copy.

---

## Cảnh báo

Đây là **công cụ cho lúc phát triển**, không phải thứ để ship cho người dùng cuối. Nó nạp
assembly tuỳ ý vào tiến trình Revit và đụng vào API nội bộ không được Autodesk hỗ trợ.
