// engines 目录的发现逻辑：定位器（找解包好的 soffice）与安装器（找预置的安装包）共用。

namespace LibreOffice.Core;

/// <summary>
/// 随库预置的 engines 目录定位（内部共用辅助类）。
/// engines 目录结构：
/// <code>
///   engines\
///     libreoffice\            ← 解包结果（setup 脚本或 LibreOfficeInstaller 产出）
///       win-x64\program\soffice.exe
///       linux-deb-arm64\program\soffice
///     downloads\              ← 预置的安装包（fetch-engines.ps1 下载，或人工拷入）
///       LibreOffice_26.8.2_Win_x86-64.msi
///       LibreOffice_26.8.2_Linux_aarch64_deb.tar.gz
/// </code>
/// </summary>
internal static class EnginePaths
{
    /// <summary>
    /// 查找 engines 根目录：① 从应用目录向上最多 8 级找含仓库标记 LibreOfficeConverter.slnx 的目录，取其 engines；
    /// ② 应用目录旁的 engines（发布部署布局：engines 目录随程序一起拷贝）。
    /// </summary>
    /// <returns>存在的 engines 目录完整路径；两处都没有时返回 null。传值示例：H:\ZCodeFile\LibreOfficeConverter\engines</returns>
    public static string? FindEnginesRoot()
    {
        // 候选目录列表，传值示例（单个元素）：H:\ZCodeFile\LibreOfficeConverter\engines
        var candidates = new List<string>();
        // ① 从应用目录向上找仓库标记（开发与源码运行场景：bin\Debug\net10.0 → … → 仓库根）
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LibreOfficeConverter.slnx")))
            {
                candidates.Add(Path.Combine(directory.FullName, "engines"));
                break;
            }
        }
        // ② 应用目录旁的 engines（发布部署场景）
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "engines"));
        // 返回第一个真实存在的候选
        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// 在 engines 根目录下查找匹配指定平台键的预置安装包（在 downloads 与 engines 根两级里找）。
    /// </summary>
    /// <param name="enginesRoot">engines 目录完整路径。传值示例：H:\ZCodeFile\LibreOfficeConverter\engines</param>
    /// <param name="platformKey">平台键。传值示例："win-x64"、"linux-deb-arm64"</param>
    /// <returns>包文件完整路径；没有则 null。传值示例：H:\...\engines\downloads\LibreOffice_26.8.2_Win_x86-64.msi</returns>
    public static string? FindPackage(string enginesRoot, string platformKey)
    {
        // 平台键 → 包文件通配模式（与 engines.json 里 file 命名一致的家族匹配）
        // 传值示例：win-x64 → "LibreOffice_*_Win_x86-64.msi"
        var pattern = platformKey switch
        {
            "win-x64" => "LibreOffice_*_Win_x86-64.msi",
            "linux-deb-x64" => "LibreOffice_*_Linux_x86-64_deb.tar.gz",
            "linux-rpm-x64" => "LibreOffice_*_Linux_x86-64_rpm.tar.gz",
            "linux-deb-arm64" => "LibreOffice_*_Linux_aarch64_deb.tar.gz",
            "linux-rpm-arm64" => "LibreOffice_*_Linux_aarch64_rpm.tar.gz",
            _ => null,
        };
        if (pattern is null)
            return null;
        // 搜索目录：downloads 优先，engines 根兜底（便于人工直接丢包）
        // 匹配到的包路径列表，传值示例（单个元素）：H:\...\engines\downloads\LibreOffice_26.8.2_Win_x86-64.msi
        foreach (var directory in new[] { Path.Combine(enginesRoot, "downloads"), enginesRoot })
        {
            if (!Directory.Exists(directory))
                continue;
            // 同名多个版本时取字典序最大的（最新版本）
            var found = Directory.GetFiles(directory, pattern).OrderByDescending(f => f).FirstOrDefault();
            if (found is not null)
                return found;
        }
        return null;
    }
}
