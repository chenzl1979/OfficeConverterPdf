// 字体安装器：解决离线/国产环境下缺中文字体导致的"豆腐块"问题。
//
// 背景：LibreOffice 把文档渲染成 PDF 时用本机字体；解包版 LibreOffice 自带 100+ 西文字体但没有一个 CJK 字体，
// Windows 服务器通常有宋体/微软雅黑，而 Linux/openEuler 最小安装常常一个中文字体都没有——中文文档转出的
// PDF 会整页变成 □□□（缺字的"豆腐块"，而不是编码乱码）。
//
// 机制（实测验证）：LibreOffice 会加载自身安装目录的 share/fonts/truetype/（三平台通用行为），
// 所以把预置字体复制进 engines\libreoffice\<平台>\share\fonts\truetype 即可——
// 零系统修改、免管理员/root、随 LibreOffice 一起"安装/卸载"，profile 清空也不受影响。
// （曾实测 profile 的 user/fonts 与 Windows HKCU 用户字体注册：soffice 均不加载，故不采用。）
//
// 预置字体目录结构（engines\fonts，字体均为 SIL OFL 1.1 可自由再分发，许可证文件在 share 内）：
//   engines\fonts\
//     share\            ← 通用字体（所有平台都装）：思源黑体/宋体、霞鹜文楷
//     win-x64\          ← 仅 Windows（可选；与 share 同名时覆盖通用版）
//     linux-x64\        ← 仅 Linux x64（可选）
//     linux-arm64\      ← 仅 Linux ARM64（可选，飞腾/鲲鹏）
//   兼容旧布局：直接散放在 engines\fonts\ 根下的字体同样按通用字体收集。
//   字体只分操作系统与架构、不分 deb/rpm（字体二进制与包格式无关）。
// 合并规则：安装 LibreOffice 时，把"share + 本平台专属"合并复制进 libreoffice\<平台>\share\fonts\truetype，
// 通用先复制、平台专属后复制（同名覆盖）。
//
// 通用预置字体（share，均为 SIL OFL 1.1）：
//  * Noto Sans CJK SC Regular/Bold（思源黑体）——替代 黑体/微软雅黑/等线
//  * Noto Serif CJK SC Regular/Bold（思源宋体）——替代 宋体
//  * LXGW WenKai Regular（霞鹜文楷）——替代 楷体/仿宋风格
// 商业字体（方正小标宋、汉仪等）许可不允许再分发、无法预置：文档用到时按上面近似替换（文字不丢，版式略有偏移）。

namespace LibreOffice.Core;

/// <summary>
/// 随库预置中文字体的安装器：把 engines\fonts 里的开源中文字体（思源黑体/宋体、霞鹜文楷）
/// 复制进本库解包安装的 LibreOffice 的 share\fonts\truetype 目录，使转换引擎始终有中文字体可用，
/// 避免离线/国产环境转换出的 PDF 出现"豆腐块"。
/// LibreOfficeInstaller.InstallAsync 安装完成后会自动调用；DocumentConverter.CreateAsync 也会自动补装（幂等）。
/// </summary>
/// <remarks>
/// 调用示例：
/// <code>
///   // 通常无需手工调用（安装 LibreOffice 与创建转换器时自动执行）；手工补装时：
///   int copied = await FontInstaller.InstallAsync();
///   Console.WriteLine(copied == 0 ? "字体已就位" : $"已复制 {copied} 个字体到 LibreOffice");
/// </code>
/// </remarks>
public static class FontInstaller
{
    /// <summary>
    /// 判断预置字体是否已复制进 LibreOffice（当前平台应装的"share + 平台专属"字体，
    /// 在 engines\libreoffice\&lt;当前平台&gt;\share\fonts\truetype 里文件齐全且大小一致）。
    /// </summary>
    /// <param name="enginesRoot">engines 目录；传 null 自动查找。传值示例：@"D:\App\engines"</param>
    /// <returns>已就位返回 true；没有 LibreOffice 安装或字体未复制返回 false。</returns>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   if (!FontInstaller.IsInstalled())
    ///       await FontInstaller.InstallAsync();
    /// </code>
    /// </remarks>
    public static bool IsInstalled(string? enginesRoot = null)
    {
        // 预置字体目录必须存在
        var fontsDirectory = FindFontsDirectory(enginesRoot);
        if (fontsDirectory is null)
            return true;   // 没打包字体：视为"无事可做"，不算未安装
        // 当前平台的 LibreOffice 安装目录
        var libreOfficeDirectory = FindLibreOfficeDirectory(enginesRoot);
        if (libreOfficeDirectory is null)
            return false;
        // 当前平台应装的字体清单（share + 平台专属）
        var platformKey = LibreOfficeInstaller.DetectPlatformKey();
        // 全部应装字体都在且大小一致 → 已安装
        foreach (var font in CollectFontFiles(fontsDirectory, platformKey))
        {
            var destination = Path.Combine(libreOfficeDirectory, "share", "fonts", "truetype", font.FileName);
            if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(font.Source).Length)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 把预置字体复制进 engines 管理的全部 LibreOffice 安装的 share\fonts\truetype（幂等：已存在且大小一致则跳过）。
    /// 每个平台目录按自己的平台键取"share + 平台专属"合并清单（通用先复制、平台专属后复制，同名覆盖）。
    /// 只写本库解包出来的 LibreOffice 目录（engines\libreoffice\*），不碰系统安装的 LibreOffice、不写系统目录。
    /// </summary>
    /// <param name="enginesRoot">engines 目录；传 null 自动查找。传值示例：@"D:\App\engines"</param>
    /// <param name="cancellationToken">取消令牌。传值示例：CancellationToken.None</param>
    /// <returns>本次实际复制的字体文件数；已就位时为 0。</returns>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   int copied = await FontInstaller.InstallAsync();
    ///   Console.WriteLine(copied == 0 ? "字体已就位" : $"已复制 {copied} 个字体");
    /// </code>
    /// </remarks>
    public static Task<int> InstallAsync(string? enginesRoot = null, CancellationToken cancellationToken = default)
    {
        // 纯文件复制放到线程池，避免阻塞调用方
        return Task.Run(() =>
        {
            // 预置字体目录：没有打包字体就无事可做
            var fontsDirectory = FindFontsDirectory(enginesRoot);
            if (fontsDirectory is null)
                return 0;
            // engines 根目录；没有则无事可做（连 LibreOffice 都还没装）
            var root = enginesRoot ?? EnginePaths.FindEnginesRoot();
            if (root is null)
                return 0;
            // libreoffice 目录不存在（尚未安装）→ 返回 0
            var libreOfficeRoot = Path.Combine(root, "libreoffice");
            if (!Directory.Exists(libreOfficeRoot))
                return 0;

            // 复制进每一个已解包的平台目录（win-x64、linux-deb-arm64 等）的 share/fonts/truetype，
            // 各平台按自己的键取合并清单（share + 该平台专属）
            var copied = 0;
            foreach (var platformDirectory in Directory.GetDirectories(libreOfficeRoot))
            {
                // 目录名即平台键，传值示例："win-x64"、"linux-deb-arm64"
                var platformKey = Path.GetFileName(platformDirectory);
                // 跳过暂存目录（.staging-*）与非 LibreOffice 目录（只认含 program 子目录的真实安装）
                if (platformKey.StartsWith(".", StringComparison.Ordinal) ||
                    !Directory.Exists(Path.Combine(platformDirectory, "program")))
                    continue;
                // 该平台应装的合并字体清单
                var fontFiles = CollectFontFiles(fontsDirectory, platformKey);
                // 目标目录：<平台>\share\fonts\truetype（LibreOffice 启动时会加载）
                var target = Path.Combine(platformDirectory, "share", "fonts", "truetype");
                Directory.CreateDirectory(target);
                // 逐个复制：目标缺失或大小不同才复制（幂等，秒级跳过）
                foreach (var (source, fileName) in fontFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(target, fileName);
                    if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(source).Length)
                    {
                        File.Copy(source, destination, overwrite: true);
                        copied++;
                    }
                }
            }
            return copied;
        }, cancellationToken);
    }

    // ───────────────────────── 内部辅助 ─────────────────────────

    /// <summary>
    /// 查找随库预置的字体目录（engines\fonts）。
    /// </summary>
    /// <param name="enginesRoot">engines 目录；传 null 自动查找。传值示例：@"D:\App\engines"</param>
    /// <returns>字体目录完整路径；没有则 null。传值示例：@"D:\App\engines\fonts"</returns>
    private static string? FindFontsDirectory(string? enginesRoot)
    {
        // engines 根目录：调用方指定或自动查找
        var root = enginesRoot ?? EnginePaths.FindEnginesRoot();
        if (root is null)
            return null;
        // fonts 子目录存在才有效
        var fonts = Path.Combine(root, "fonts");
        return Directory.Exists(fonts) ? fonts : null;
    }

    /// <summary>
    /// 查找当前平台对应的 LibreOffice 安装目录（engines\libreoffice\&lt;平台键&gt;，含 program 子目录）。
    /// </summary>
    /// <param name="enginesRoot">engines 目录。传值示例：@"D:\App\engines"</param>
    /// <returns>LibreOffice 平台目录完整路径；没有则 null。传值示例：@"D:\App\engines\libreoffice\win-x64"</returns>
    private static string? FindLibreOfficeDirectory(string? enginesRoot)
    {
        // engines 根目录
        var root = enginesRoot ?? EnginePaths.FindEnginesRoot();
        if (root is null)
            return null;
        // 按当前平台键定位（与 LibreOfficeInstaller 的平台键一致）
        var platformKey = LibreOfficeInstaller.DetectPlatformKey();
        var platformDirectory = Path.Combine(root, "libreoffice", platformKey);
        return Directory.Exists(Path.Combine(platformDirectory, "program")) ? platformDirectory : null;
    }

    /// <summary>
    /// 平台键 → fonts 下平台专属子目录名（字体只分操作系统与架构、不分 deb/rpm）。
    /// </summary>
    /// <param name="platformKey">平台键。传值示例："win-x64"、"linux-deb-arm64"</param>
    /// <returns>子目录名；未知平台返回 null（只用 share）。传值示例："linux-deb-arm64" → "linux-arm64"</returns>
    private static string? FontPlatformSubDirectory(string platformKey) => platformKey switch
    {
        // Windows x64（含 ARM64 设备经模拟层运行 x64 包）
        "win-x64" => "win-x64",
        // Linux x64（deb/rpm 家族共用）
        var key when key.StartsWith("linux-", StringComparison.Ordinal) && key.EndsWith("-x64", StringComparison.Ordinal) => "linux-x64",
        // Linux ARM64（飞腾/鲲鹏，deb/rpm 家族共用）
        var key when key.StartsWith("linux-", StringComparison.Ordinal) && key.EndsWith("-arm64", StringComparison.Ordinal) => "linux-arm64",
        // 未知平台：只用 share 通用字体
        _ => null,
    };

    /// <summary>
    /// 收集某平台应装的合并字体清单：engines\fonts 根（旧布局兼容）+ share（通用）+ 平台专属目录。
    /// 收集顺序为 通用在前、平台专属在后，同名文件以平台专属为准（后写覆盖先写）。
    /// 只取 .ttf/.otf/.ttc，许可证等非字体文件自然被排除。
    /// </summary>
    /// <param name="fontsDirectory">预置字体根目录。传值示例：@"D:\App\engines\fonts"</param>
    /// <param name="platformKey">目标平台键。传值示例："linux-deb-arm64"</param>
    /// <returns>（源文件完整路径, 目标文件名）列表，按文件名排序。传值示例（单个元素）：(@"D:\...\share\NotoSansCJKsc-Regular.otf", "NotoSansCJKsc-Regular.otf")</returns>
    private static List<(string Source, string FileName)> CollectFontFiles(string fontsDirectory, string platformKey)
    {
        // 收集目录列表：顺序即覆盖优先级（越靠后优先级越高）
        // 候选目录列表，传值示例（单个元素）：@"D:\App\engines\fonts\share"
        var directories = new List<string>();
        // ① 旧布局兼容：engines\fonts 根下直接散放的字体
        directories.Add(fontsDirectory);
        // ② 通用字体目录（所有平台都装）
        var shareDirectory = Path.Combine(fontsDirectory, "share");
        if (Directory.Exists(shareDirectory))
            directories.Add(shareDirectory);
        // ③ 平台专属目录（仅该平台装；同名覆盖通用）
        var subDirectory = FontPlatformSubDirectory(platformKey);
        if (subDirectory is not null)
        {
            var platformDirectory = Path.Combine(fontsDirectory, subDirectory);
            if (Directory.Exists(platformDirectory))
                directories.Add(platformDirectory);
        }

        // 按文件名收集（后加入的目录覆盖先加入的同名文件），字体文件扩展名过滤（大小写不限）
        var fontExtensions = new HashSet<string>([".ttf", ".otf", ".ttc"], StringComparer.OrdinalIgnoreCase);
        // 文件名 → 源完整路径（后写覆盖先写），传值示例："NotoSansCJKsc-Regular.otf" → @"D:\...\share\NotoSansCJKsc-Regular.otf"
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                // 扩展名不在字体清单里（如 LICENSE-*.txt）则跳过
                if (!fontExtensions.Contains(Path.GetExtension(file)))
                    continue;
                byFileName[Path.GetFileName(file)] = file;
            }
        }
        // 输出按文件名排序（复制顺序稳定，便于幂等判断与排查）
        return byFileName
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (Source: pair.Value, FileName: pair.Key))
            .ToList();
    }
}
