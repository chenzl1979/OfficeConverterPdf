// 在本机查找 LibreOffice 的命令行程序 soffice。
// 简化移植自 Filee 项目的 src/Filee.Engines/Infrastructure/EngineEnvironment.cs：
// 顺序为 显式指定 → PATH → 各操作系统标准安装位置。

namespace LibreOffice.Core;

/// <summary>
/// 在本机定位 LibreOffice 命令行程序（Windows 上是 soffice.exe，其它系统是 soffice）。
/// </summary>
/// <remarks>
/// 调用示例：
/// <code>
///   string? path = SofficeLocator.Locate();                       // 自动查找
///   string? path2 = SofficeLocator.Locate(@"D:\Portable\LibreOffice"); // 在指定安装目录里找
///   Console.WriteLine(path ?? "本机没有安装 LibreOffice");
/// </code>
/// </remarks>
public static class SofficeLocator
{
    /// <summary>
    /// 按优先级查找 soffice 程序；找不到返回 null（不抛异常）。
    /// 查找顺序：① 显式指定的路径或安装目录；
    /// ② 随库预置的 engines\libreoffice 目录（build\setup-windows.ps1 / setup-linux.sh 的解包结果，
    ///    从应用目录向上最多 8 级查找仓库标记 LibreOfficeConverter.slnx，或直接找应用旁边的 engines 目录）；
    /// ③ PATH 环境变量（进程级 → 用户级 → 机器级）；
    /// ④ 当前操作系统的标准安装位置；
    /// ⑤ Linux 用户目录的手工解包位置 ~/.local/share/libreoffice-converter。
    /// </summary>
    /// <param name="explicitPath">
    /// 显式指定的 soffice 程序路径，或 LibreOffice 的安装目录（会自动在其下探测常见布局）；
    /// 传 null 或空串表示跳过这一步、只做自动查找。
    /// 传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"；或目录 @"D:\LibreOffice"
    /// </param>
    /// <returns>找到的 soffice 可执行文件的完整路径；找不到返回 null。传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"</returns>
    public static string? Locate(string? explicitPath = null)
    {
        // 第 1 步：使用调用者显式给定的路径或目录
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // 给的本身就是程序文件 → 直接采用（转绝对路径，方便后续日志）
            if (File.Exists(explicitPath))
                return Path.GetFullPath(explicitPath);
            // 给的是目录 → 在目录下探测常见的安装布局
            if (Directory.Exists(explicitPath))
                return CandidatesInFolder(Path.GetFullPath(explicitPath)).FirstOrDefault(File.Exists);
            // 给了但既不是文件也不是目录 → 视为无效，继续自动查找
        }

        // 第 2 步：找随库预置的 engines\libreoffice（setup 脚本的解包结果，版本固定、优先于系统安装）
        var bundled = FindBundled();
        if (bundled is not null)
            return bundled;

        // 第 3 步：在 PATH 的每个目录里找（进程级 PATH，用户级与机器级 PATH 仅 Windows 下读取）
        // 所有 PATH 目录的候选列表，传值示例（单个元素）："C:\Windows\system32"
        var searchDirectories = new List<string>();
        searchDirectories.AddRange(Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? []);
        if (OperatingSystem.IsWindows())
        {
            // Windows 用户级 PATH，传值示例（单个元素）："C:\Users\我\AppData\Local\Microsoft\WindowsApps"
            searchDirectories.AddRange(
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)?.Split(';') ?? []);
            // Windows 机器级 PATH，传值示例（单个元素）："C:\Program Files\LibreOffice\program"（如果有人手工加过）
            searchDirectories.AddRange(
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)?.Split(';') ?? []);
        }
        // 逐个目录拼上程序名检查；找到立即返回
        var foundOnPath = searchDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.Combine(directory.Trim(), ProgramName))
            .FirstOrDefault(File.Exists);
        if (foundOnPath is not null)
            return foundOnPath;

        // 第 4 步：检查当前操作系统的标准安装位置
        var standard = StandardLocations().FirstOrDefault(File.Exists);
        if (standard is not null)
            return standard;

        // 第 5 步（仅 Linux/国产系统）：用户主目录下的手工解包位置（没有仓库布局的服务器部署）
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            // 用户解包根：~/.local/share/libreoffice-converter，传值示例：/home/svc/.local/share/libreoffice-converter
            var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "libreoffice-converter");
            if (Directory.Exists(userRoot))
            {
                // 每个解包子目录里探测 program/soffice，传值示例（单个元素）："linux-deb-x64"
                foreach (var directory in Directory.GetDirectories(userRoot))
                {
                    var candidate = Path.Combine(directory, "program", "soffice");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 查找随库预置的 LibreOffice（engines\libreoffice 目录，setup 脚本或 LibreOfficeInstaller 的解包结果）。
    /// 查找范围由 <see cref="EnginePaths.FindEnginesRoot"/> 决定（仓库标记向上查找 + 应用旁 engines 目录）。
    /// </summary>
    /// <returns>找到的 soffice 完整路径；没有预置目录或里面没有程序时返回 null。传值示例：H:\ZCodeFile\LibreOfficeConverter\engines\libreoffice\win-x64\program\soffice.exe</returns>
    private static string? FindBundled()
    {
        // engines 根目录（不存在则没有预置），传值示例：H:\ZCodeFile\LibreOfficeConverter\engines
        var enginesRoot = EnginePaths.FindEnginesRoot();
        if (enginesRoot is null)
            return null;
        // libreoffice 解包根目录
        var libreOfficeRoot = Path.Combine(enginesRoot, "libreoffice");
        if (!Directory.Exists(libreOfficeRoot))
            return null;
        // 逐个平台子目录探测（win-x64、linux-deb-arm64 等）
        foreach (var platformDirectory in Directory.GetDirectories(libreOfficeRoot))
            if (ProbePlatformDirectory(platformDirectory) is { } found)
                return found;
        return null;
    }

    /// <summary>
    /// 在一个平台子目录里探测 soffice：先探归一化布局 &lt;平台&gt;\program\soffice(.exe)，
    /// 再探未归一化的兜底布局 &lt;平台&gt;\opt\libreoffice*&lt;B&gt;\program\soffice(.exe)。
    /// </summary>
    /// <param name="platformDirectory">平台子目录的完整路径。传值示例：H:\...\engines\libreoffice\win-x64</param>
    /// <returns>soffice 完整路径；没有则 null。传值示例：H:\...\engines\libreoffice\win-x64\program\soffice.exe</returns>
    private static string? ProbePlatformDirectory(string platformDirectory)
    {
        // 当前操作系统的程序名（Windows 是 soffice.exe，其余 soffice）
        var direct = Path.Combine(platformDirectory, "program", ProgramName);
        if (File.Exists(direct))
            return direct;
        // 兜底：setup 脚本尚未归一化时，包自带 opt/libreoffice<版本>/ 前缀（Linux 布局）
        var optDirectory = Path.Combine(platformDirectory, "opt");
        if (Directory.Exists(optDirectory))
        {
            // 探测到的版本目录，传值示例：…\linux-deb-x64\opt\libreoffice26.8
            foreach (var versionDirectory in Directory.GetDirectories(optDirectory, "libreoffice*"))
            {
                var candidate = Path.Combine(versionDirectory, "program", ProgramName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>当前系统的 soffice 程序文件名：Windows 为 "soffice.exe"，其余系统为 "soffice"。</summary>
    private static string ProgramName => OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";

    /// <summary>
    /// 在一个安装根目录下探测 soffice 的常见布局，返回按优先级排列的候选路径。
    /// </summary>
    /// <param name="folder">安装根目录的绝对路径。传值示例：@"D:\LibreOffice"、@"C:\Program Files\LibreOffice" 或 Linux 的 "/opt/libreoffice24.2"</param>
    /// <returns>候选的 soffice 完整路径列表（存在与否未检查，由调用方过滤）。传值示例（单个元素）：@"D:\LibreOffice\program\soffice.exe"</returns>
    private static IEnumerable<string> CandidatesInFolder(string folder)
    {
        // 已探测到的候选列表，传值示例（单个元素）：@"D:\LibreOffice\program\soffice.exe"
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(folder, "program", "soffice.exe"));   // 标准布局：<根>\program\soffice.exe
            candidates.Add(Path.Combine(folder, "soffice.exe"));              // 直接指向 program 目录的情况
            candidates.Add(Path.Combine(folder, "LibreOffice", "program", "soffice.exe")); // 给的是上一级目录的情况
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine(folder, "Contents", "MacOS", "soffice"));             // 给的是 LibreOffice.app
            candidates.Add(Path.Combine(folder, "LibreOffice.app", "Contents", "MacOS", "soffice")); // 给的是 Applications 目录
        }
        else
        {
            // Linux（含 UOS/深度、银河麒麟、openEuler 等国产 Linux）：soffice 是个设置环境变量后再启动 soffice.bin 的外壳脚本，
            // 用 Process 直接启动即可，无需额外处理
            candidates.Add(Path.Combine(folder, "program", "soffice"));       // 标准布局：<根>/program/soffice
            candidates.Add(Path.Combine(folder, "soffice"));                  // 直接指向 program 目录的情况
            candidates.Add(Path.Combine(folder, "libreoffice", "program", "soffice")); // 给的是上一级目录的情况（/usr/lib）
            candidates.Add(Path.Combine(folder, "LibreOffice", "program", "soffice")); // 同上，大写开头（Linux 文件名大小写敏感，两种都探）
        }
        return candidates;
    }

    /// <summary>
    /// 当前操作系统的标准安装位置（按常见程度排序）。
    /// </summary>
    /// <returns>候选的 soffice 完整路径列表（存在与否未检查，由调用方过滤）。传值示例（单个元素）：@"C:\Program Files\LibreOffice\program\soffice.exe"</returns>
    private static IEnumerable<string> StandardLocations()
    {
        // 候选列表，传值示例（单个元素）：@"C:\Program Files\LibreOffice\program\soffice.exe"
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            // %ProgramFiles%（通常是 C:\Program Files）下的标准安装
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // %ProgramFiles(x86)%（32 位兼容目录；在 64 位进程里才拿得到，否则为空串）
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            // 用户级按用户安装的位置（LibreOffice 安装程序支持"仅为我安装"）
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"));
            if (!string.IsNullOrEmpty(programFilesX86))
                candidates.Add(Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe"));
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(Path.Combine(localAppData, "Programs", "LibreOffice", "program", "soffice.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            // 把 .app 拖进"应用程序"文件夹后的标准位置
            candidates.Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
        }
        else
        {
            // Linux 及国产 Linux 系统（UOS/深度 deepin、银河麒麟 Kylin、中标麒麟、openEuler、中科方德、龙芯 Loongnix 等）。
            // 这些系统的 LibreOffice 都来自发行版仓库或官方包，安装位置遵循下述惯例：
            // ① deb 系（Debian/Ubuntu/UOS/深度/麒麟桌面版）：/usr/lib/libreoffice/program/soffice
            candidates.Add("/usr/lib/libreoffice/program/soffice");
            // ② rpm 系 64 位（openEuler/银河麒麟 V10 服务版/CentOS/Fedora/中科方德）：库目录是 lib64
            candidates.Add("/usr/lib64/libreoffice/program/soffice");
            // ③ 源码或手工安装到 /usr/local
            candidates.Add("/usr/local/lib/libreoffice/program/soffice");
            // ④ PATH 里的符号链接（/usr/bin/soffice → 实际位置）；File.Exists 会自动跟随符号链接
            candidates.Add("/usr/bin/soffice");
            // ⑤ snap 安装（Ubuntu 常见）
            candidates.Add("/snap/libreoffice/current/program/soffice");
            // ⑥ /opt 下的官方 deb/tarball 安装（libreoffice24.2、libreoffice7.6 等版本号目录；
            //    Linux 文件名大小写敏感，小写与大写开头各探测一遍）
            foreach (var pattern in new[] { "libreoffice*", "LibreOffice*" })
            {
                if (Directory.Exists("/opt"))
                {
                    // 探测到的 /opt 下的版本目录，传值示例："/opt/libreoffice24.2"
                    foreach (var directory in Directory.GetDirectories("/opt", pattern))
                        candidates.Add(Path.Combine(directory, "program", "soffice"));
                }
            }
            // 注意：Flatpak 版 LibreOffice 沙箱化、不能作为普通进程直接调用（需要 "flatpak run" 前缀），
            // 本库不做自动定位；请改用发行版仓库安装的 LibreOffice，或把 soffice 路径显式传给 DocumentConverter。
        }
        return candidates;
    }
}
