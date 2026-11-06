# LibreOfficeConverter（LibreOffice 文档格式转换类库）

从 [Filee](https://github.com/KnifeLemon/Filee) 开源项目提取的 LibreOffice 格式转换能力，封装为独立的 .NET 10 类库。
**一个方法**完成转换：Office 文档（doc/docx/xls/xlsx/ppt/pptx/rtf/csv/odf…）→ PDF、DOCX、XLSX 等，
不需要安装 Microsoft Office 或 WPS。全部代码带中文注释（参数含传值示例、方法含调用示例）。

## 支持的操作系统

| 系统 | 架构 | LibreOffice 定位方式 | 说明 |
|---|---|---|---|
| Windows 10/11 | x64 / ARM64 | PATH → `Program Files` → `Program Files (x86)` → `%LOCALAPPDATA%\Programs` | 也可用 `sofficePath` 参数显式指定 |
| UOS / 深度 deepin | AMD64 / ARM64（飞腾、鲲鹏、兆芯） | `/usr/lib/libreoffice`、`/usr/bin/soffice`、snap | 应用商店或 `apt` 安装 |
| 银河麒麟 Kylin V10（桌面/服务）、中标麒麟 | AMD64 / ARM64 | deb 系 `/usr/lib/libreoffice`、rpm 系 `/usr/lib64/libreoffice` | 软件商店或 `apt`/`dnf`/`yum` 安装 |
| openEuler、中科方德、龙芯 Loongnix 等 | x86_64 / aarch64 / LoongArch* | `/usr/lib64/libreoffice`、`/opt/libreoffice*` | `dnf`/`yum` 安装 |
| macOS 12+ | x64 / ARM64 | `/Applications/LibreOffice.app` | 同样可用 |

\* 龙芯（LoongArch）需要社区版的 .NET 运行时与 LibreOffice 移植；本库为纯托管代码（无 P/Invoke、无本机依赖），
只要平台上有 .NET 10 运行时即可运行。

> Flatpak 版 LibreOffice 沙箱化、无法作为普通进程调用，本库不做自动定位；请用发行版仓库版本，
> 或把 soffice 路径显式传给 `new DocumentConverter(sofficePath)`。

## 安装 LibreOffice

### 方式一：预置包 + 程序自动安装（推荐，完全离线）

5 个平台的 LibreOffice 26.8.2 安装包**已全部预置在项目 `engines\downloads\` 里**（合计约 1.24 GB，
SHA-256 已钉在 `build/engines.json`）。部署到离线电脑时把 `engines` 目录随程序一起拷贝，
**程序运行时按电脑配置自动选包、自动安装，零人工**：

```csharp
// 离线电脑首次运行：自动检测平台（win-x64 / linux-deb-arm64 / linux-rpm-x64 …），
// 找不到 soffice 时自动从预置包解包安装（免管理员/root，约 1-3 分钟，只发生一次）
using var converter = await DocumentConverter.CreateAsync();
var result = await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
```

自动安装的规则（`LibreOfficeInstaller`）：

| 电脑配置 | 自动选择的包 | 解包方式 |
|---|---|---|
| Windows x64 / ARM64（模拟） | win-x64 | `msiexec /a` 管理解包（免管理员、不写注册表） |
| UOS/深度/麒麟桌面版 x64 | linux-deb-x64 | `dpkg-deb -x`（免 root） |
| UOS/深度/麒麟桌面版 ARM64（飞腾/鲲鹏） | linux-deb-arm64 | `dpkg-deb -x`（免 root） |
| openEuler/麒麟服务版/CentOS x64 | linux-rpm-x64 | `rpm2archive` 或 `rpm2cpio+cpio`（免 root） |
| openEuler/麒麟服务版 ARM64 | linux-rpm-arm64 | 同上 |

装好的位置与探测：`engines\libreoffice\<平台>\program\soffice(.exe)`，
`SofficeLocator` **优先于系统安装**使用它（版本固定，每台机器转换结果一致）。
删除 `engines\libreoffice\<平台>` 目录即卸载；重复调用 `CreateAsync`/`InstallAsync` 幂等跳过。

也可以用命令行先装再看结果（演示程序）：

```bash
dotnet run -- --install        # 自动选包并安装，打印 soffice 路径
dotnet run -- --selftest       # 自测（不需要 LibreOffice）
```

> 提示：项目用 git 管理且希望把包入库时，删除 `engines/.gitignore` 即可（注意约 1.24 GB 体积）。
> Linux 上若启动报缺系统库（libcairo、libXinerama 等，精简的服务器镜像可能没有），
> 用发行版包管理器补装对应库即可。

### 方式二：手动脚本（想人工控制时）

```text
# 下载（任一台有网的机器；清华 TUNA 镜像优先，SHA-256 校验）
powershell -File build\fetch-engines.ps1 -Only win-x64
# Windows 解包（msiexec /a 管理解包，无弹窗免管理员）
powershell -File build\setup-windows.ps1
# Linux 解包（免 root；-k deb|rpm -a x64|arm64 可指定，默认自动识别）
./build/setup-linux.sh
```

### 方式三：系统包管理器（部署机有 root 且能联网时）

- **Windows**：[libreoffice.org](https://zh-cn.libreoffice.org/) 安装包，装完自动被找到。
- **Debian/Ubuntu/UOS/深度/麒麟桌面版**：
  ```bash
  sudo apt install libreoffice-core libreoffice-writer libreoffice-calc libreoffice-impress libreoffice-draw
  ```
- **openEuler/麒麟服务版/CentOS/Fedora**：
  ```bash
  sudo dnf install libreoffice libreoffice-headless    # 老版本用 yum
  ```

服务端无界面环境可直接运行（`--headless`，不需要 X/桌面会话）。
以 systemd 服务账户运行时若没有 `HOME`，请配置 `ConversionOptions.EnvironmentVariables`：
`new Dictionary<string,string> { ["HOME"] = "/home/svc" }`。

## 快速开始

```csharp
using LibreOffice.Core;

// Windows / Linux 通用：自动定位 soffice
using var converter = new DocumentConverter();

// Office 文档 → PDF（输出到源文件旁边）
ConversionResult result = await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
// Linux 上同理：
// ConversionResult result = await converter.ConvertAsync("/home/user/文档/报告.docx", "pdf");

Console.WriteLine(result.OutputPath);   // 生成的 PDF 路径
```

服务端批量转换（并发由库内部控制，每个 soffice 进程使用独立 profile，互不干扰）：

```csharp
var options = new ConversionOptions
{
    Timeout = TimeSpan.FromMinutes(5),      // 大文档放宽超时
    MaxDegreeOfParallelism = 4,             // 最多同时 4 个 soffice
    PdfA = true,                            // 输出 PDF/A-2b 归档格式
};
using var converter = new DocumentConverter(options: options);
await converter.ConvertAsync("/data/in/表格.xlsx", "pdf", "/data/out/表格.pdf");
```

命令行演示：

```bash
cd src/LibreOfficeDemo
dotnet run -- "C:\Docs\报告.docx" pdf                  # Windows
dotnet run -- /home/user/文档/报告.docx pdf            # Linux / 国产系统
dotnet run -- --selftest                               # 自测（不需要 LibreOffice）
```

## 字体与"豆腐块"问题（已内置解决方案）

**现象**：转换出的 PDF 中文变成 □□□（这不是编码乱码，是**缺字体**——LibreOffice 渲染 PDF 用的是本机字体，
解包版 LibreOffice 自带 100+ 西文字体但一个 CJK 字体都没有；openEuler/服务器最小安装常常整个系统都没有中文字体）。

**内置方案**：项目 `engines\fonts\` 已预置开源中文字体（均为 SIL OFL 1.1，可自由再分发，许可文件同目录），
按"通用 + 平台专属"两级目录组织，安装时自动合并：

```text
engines\fonts\
├── share\            ← 通用字体（所有平台都装）
│   ├── NotoSansCJKsc-Regular.otf / -Bold.otf      思源黑体 → 替代 黑体/微软雅黑/等线
│   ├── NotoSerifCJKsc-Regular.otf / -Bold.otf     思源宋体 → 替代 宋体
│   ├── LxgwWenKai-Regular.ttf                     霞鹜文楷 → 替代 楷体/仿宋风格
│   ├── NotoSansMonoCJKsc-Regular.otf / -Bold.otf  等宽中文 → 替代 Consolas/Courier 中文混排
│   └── LXGWWenKaiGB-Regular/-Medium.ttf           霞鹜文楷 GB（GB2312 全覆盖）→ 替代 楷体_GB2312/仿宋_GB2312（政府公文）
├── win-x64\          ← 仅 Windows（可选；与 share 同名时覆盖通用版；默认空，Windows 系统字体已够）
├── linux-x64\        ← 仅 Linux x64 / 兆芯、海光（可选；缺字风险最高的平台）
└── linux-arm64\      ← 仅 Linux ARM64 / 飞腾、鲲鹏（可选）
```

字体只分操作系统与架构、不分 deb/rpm（字体二进制与包格式无关）；商业字体（方正小标宋等）许可不允许
再分发、无法预置。

**自动生效**：LibreOffice 会加载自身安装目录的 `share\fonts\truetype\`（三平台通用行为，已实测验证），
`FontInstaller` 把"share 通用 + 本平台专属"合并复制进 `engines\libreoffice\<平台>\share\fonts\truetype\`——
**零系统修改、免管理员/root，Windows/Linux/国产系统行为一致**，随 LibreOffice 一起安装/卸载。
触发时机：安装 LibreOffice 时（`LibreOfficeInstaller`）、`DocumentConverter.CreateAsync`、以及每次转换前自愈式检查（幂等，已就位时只是一次文件存在检查）。

**边界说明**：
- 文档内嵌了字体的（Word"嵌入字体"选项）不受影响，LibreOffice 直接用内嵌字体；
- 商业字体（方正小标宋、汉仪等）许可不允许再分发、无法预置：会自动用上表近似替换，文字不丢、版式略有偏移；
- 各平台基线：Windows 自带宋体/雅黑基本无忧；UOS/麒麟桌面自带思源字体可读；**风险最高的是 Linux 服务器最小安装**——预置字体后三平台行为一致；
- 字体方案已实测：指定 "Noto Sans CJK SC" 的文档转换后，PDF 正确嵌入 `NotoSansCJKsc-Regular`（替代方案 `user\fonts` 与 Windows 用户级字体注册经实测不被 soffice 加载，已弃用）；
- 平台字体目录（`fonts\win-x64` 等）默认留空并附 README 说明：字体文件与 CPU 架构无关，"国内常用字体"中可合法再分发的（思源、文楷、文泉驿等）已放 `share\` 全平台生效；微软雅黑/宋体/方正等商业字体禁止再分发，切勿提取放入。

## 已知限制

- **不支持导出 OFD**：LibreOffice 没有 OFD 导出过滤器（Filee 源项目中 OFD 也是只读格式）；
  传入 `"ofd"` 会抛出 `NotSupportedException`。
- 多页 PDF → SVG 只导出第一页（Filee 源项目行为一致）。
- 转换质量取决于 LibreOffice 对源格式的支持程度；罕见老格式建议先人工抽检。
