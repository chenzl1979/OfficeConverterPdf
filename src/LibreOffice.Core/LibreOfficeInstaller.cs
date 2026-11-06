// 本地自动安装器：从随库预置的 LibreOffice 安装包（engines/downloads）中，
// 根据当前电脑配置（操作系统 + CPU 架构 + Linux 包格式家族）自动选包并解包安装。
// 借鉴 Filee 项目对 LibreOffice 引擎的处理方式：msiexec /a 管理解包（免管理员）、dpkg-deb -x / rpm2cpio 解包（免 root），
// 全程不写注册表/系统目录，删除 engines/libreoffice/<平台> 目录即"卸载"。
// tar.gz 的解压用 .NET 自带的 System.Formats.Tar + GZipStream 完成，不依赖 tar 命令。

using System.Formats.Tar;
using System.Runtime.InteropServices;

namespace LibreOffice.Core;

/// <summary>
/// LibreOffice 本地自动安装器（离线部署专用）：
/// <para>① 检测当前电脑配置，自动选择匹配的预置包（win-x64 / linux-deb-x64 / linux-deb-arm64 / linux-rpm-x64 / linux-rpm-arm64）；</para>
/// <para>② 解包安装到 engines\libreoffice\&lt;平台&gt;\，全程免管理员（Windows）/ 免 root（Linux）、不写注册表/系统目录；</para>
/// <para>③ 装完后 <see cref="DocumentConverter"/>（包括定位器）自动找到它，无需任何配置。</para>
/// 典型流程：把项目里的 engines 目录随程序拷到离线电脑 → 调 <see cref="InstallAsync"/> → 直接转换。
/// </summary>
/// <remarks>
/// 调用示例：
/// <code>
///   Console.WriteLine(LibreOfficeInstaller.DetectPlatformKey());     // 例如 "linux-deb-arm64"
///   if (!LibreOfficeInstaller.IsInstalled())
///       await LibreOfficeInstaller.InstallAsync();                   // 自动选包并解包（约 1-3 分钟）
///   using var converter = await DocumentConverter.CreateAsync();     // 自动找到装好的 soffice
/// </code>
/// </remarks>
public static class LibreOfficeInstaller
{
    /// <summary>安装互斥锁：多个线程同时触发安装时只真正执行一次。</summary>
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    /// <summary>
    /// 检测当前电脑应使用的预置包标识（依据操作系统、CPU 架构与 Linux 包格式家族）。
    /// </summary>
    /// <returns>平台键。传值示例（Windows x64 返回）："win-x64"；UOS ARM64（飞腾/鲲鹏）返回 "linux-deb-arm64"</returns>
    /// <exception cref="NotSupportedException">macOS（请手工安装 dmg），或无法判断 Linux 包格式家族，或 CPU 架构没有官方包（如 LoongArch）。</exception>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   string key = LibreOfficeInstaller.DetectPlatformKey();
    ///   Console.WriteLine($"本机应使用预置包：{key}");
    /// </code>
    /// </remarks>
    public static string DetectPlatformKey()
    {
        // Windows：官方只有 x86-64 包；ARM64 设备通过系统自带的 x64 模拟层运行 x64 版 LibreOffice
        if (OperatingSystem.IsWindows())
            return "win-x64";
        // macOS：官方走 dmg 安装，不在预置包范围内
        if (OperatingSystem.IsMacOS())
            throw new NotSupportedException("macOS 请从 libreoffice.org 下载 dmg 手工安装，本安装器只处理 Windows 与 Linux（含国产系统）的预置包。");
        // CPU 架构 → 包的架构段；其它架构（LoongArch 等）没有官方包，明确报错而不是装错包
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",      // 兆芯、海光、Intel/AMD
            Architecture.Arm64 => "arm64",  // 飞腾、鲲鹏
            _ => throw new NotSupportedException($"CPU 架构 {RuntimeInformation.ProcessArchitecture} 没有官方 LibreOffice 预置包。"),
        };
        // Linux 包格式家族（deb / rpm），决定解包工具与包文件
        return $"linux-{DetectLinuxPackageFamily()}-{arch}";
    }

    /// <summary>
    /// 判断 LibreOffice 是否已经解包安装好（engines\libreoffice\&lt;当前平台&gt;\program\soffice 存在且非空）。
    /// </summary>
    /// <param name="enginesRoot">
    /// engines 目录完整路径；传 null 时自动查找（仓库标记向上查找 + 应用旁 engines 目录）。
    /// 传值示例：@"D:\App\engines"；传 null 表示自动查找
    /// </param>
    /// <returns>已安装返回 true。</returns>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   if (!LibreOfficeInstaller.IsInstalled())
    ///       Console.WriteLine("首次使用，即将从预置包安装 LibreOffice…");
    /// </code>
    /// </remarks>
    public static bool IsInstalled(string? enginesRoot = null)
    {
        // 定位 engines 目录（调用方未指定时自动查找）
        var root = enginesRoot ?? EnginePaths.FindEnginesRoot();
        if (root is null)
            return false;
        // 当前平台的 soffice 路径；存在且非空即视为已安装
        var soffice = Path.Combine(root, "libreoffice", DetectPlatformKey(), "program",
            OperatingSystem.IsWindows() ? "soffice.exe" : "soffice");
        return File.Exists(soffice) && new FileInfo(soffice).Length > 0;
    }

    /// <summary>
    /// 从预置包安装 LibreOffice：自动选包 → 解包 → 归一化布局。已安装则直接返回现有 soffice 路径（幂等）。
    /// Windows 用 msiexec /a 解包（免管理员），deb 系用 dpkg-deb -x、rpm 系用 rpm2archive 或 rpm2cpio+cpio（免 root）。
    /// </summary>
    /// <param name="enginesRoot">
    /// engines 目录完整路径（其下应有 downloads\ 与 libreoffice\）；传 null 时自动查找；目录不存在会自动创建。
    /// 传值示例：@"D:\App\engines"；传 null 表示自动查找
    /// </param>
    /// <param name="cancellationToken">取消令牌；取消后清理暂存目录。传值示例：CancellationToken.None</param>
    /// <returns>装好的 soffice 可执行文件完整路径。传值示例：@"D:\App\engines\libreoffice\win-x64\program\soffice.exe"</returns>
    /// <exception cref="FileNotFoundException">engines 目录不存在，或没有匹配当前电脑配置的预置包（缺哪个包会写清楚）。</exception>
    /// <exception cref="InvalidOperationException">解包工具不可用（如 rpm 系缺 cpio）或解包结果异常。</exception>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   var soffice = await LibreOfficeInstaller.InstallAsync();
    ///   Console.WriteLine($"已安装：{soffice}");
    ///   // 之后 DocumentConverter 会自动使用它：
    ///   using var converter = await DocumentConverter.CreateAsync();
    /// </code>
    /// </remarks>
    public static async Task<string> InstallAsync(string? enginesRoot = null, CancellationToken cancellationToken = default)
    {
        // 当前电脑的平台键，传值示例："linux-deb-arm64"
        var platformKey = DetectPlatformKey();
        // engines 目录：优先调用方指定，否则自动查找；都没有时报错并说明如何放置预置包
        var root = enginesRoot ?? EnginePaths.FindEnginesRoot()
            ?? throw new FileNotFoundException(
                "没有找到 engines 目录。请把项目里的 engines 目录随程序一起拷贝（或用 enginesRoot 参数指定位置），" +
                "其 downloads 子目录里应有匹配本机的预置包。");
        // 最终安装目标：<engines>\libreoffice\<平台>\，传值示例：D:\App\engines\libreoffice\win-x64
        var target = Path.Combine(root, "libreoffice", platformKey);
        // soffice 期望路径
        var sofficePath = Path.Combine(target, "program", OperatingSystem.IsWindows() ? "soffice.exe" : "soffice");
        // 幂等：已安装直接返回
        if (File.Exists(sofficePath))
            return sofficePath;

        // 串行化：并发触发时只装一次，其余等待后复查
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            // 复查（可能在等待期间被别的线程装好）
            if (File.Exists(sofficePath))
                return sofficePath;
            // 找预置包：engines\downloads（或 engines 根）里匹配平台键的那个
            var package = EnginePaths.FindPackage(root, platformKey)
                ?? throw new FileNotFoundException(
                    $"engines 目录里没有匹配本机（{platformKey}）的 LibreOffice 预置包。" +
                    $"请在 engines\\downloads 放入对应包（文件名见 build\\engines.json），或运行 fetch-engines.ps1 下载。");
            // 暂存目录：解包过程的中间产物都在这里，结束即删
            var staging = Path.Combine(root, "libreoffice", ".staging-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(staging);
            try
            {
                // 分平台解包到暂存目录
                if (platformKey == "win-x64")
                    await InstallWindowsAsync(package, staging, cancellationToken);
                else if (platformKey.Contains("-deb-", StringComparison.Ordinal))
                    await InstallDebAsync(package, staging, cancellationToken);
                else
                    await InstallRpmAsync(package, staging, cancellationToken);

                // 在暂存目录里定位 LibreOffice 根（含 program/soffice 的目录），整体移动到目标位置
                var libreOfficeRoot = FindLibreOfficeRoot(staging)
                    ?? throw new InvalidOperationException("解包完成但没有找到 program/soffice，包内容与预期不符。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.Move(libreOfficeRoot, target);

                // Linux：防御性补执行权限（个别解包路径会丢权限位）
                MakeExecutableOnUnix(Path.Combine(target, "program", "soffice"));
                MakeExecutableOnUnix(Path.Combine(target, "program", "soffice.bin"));
                // 同步预置中文字体进 share\fonts\truetype（LibreOffice 会加载自身目录的这个路径），
                // 避免离线/国产环境缺中文字体导致 PDF"豆腐块"；失败不阻断安装（回退系统字体）
                try { await FontInstaller.InstallAsync(enginesRoot: root, cancellationToken: cancellationToken); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                return sofficePath;
            }
            finally
            {
                // 无论成败都清理暂存目录
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    // ───────────────────────── 平台检测 ─────────────────────────

    /// <summary>
    /// 检测 Linux 包格式家族：优先按解包工具是否存在（dpkg-deb → deb；rpm2cpio → rpm），
    /// 工具都没有时退回解析 /etc/os-release 的 ID / ID_LIKE。
    /// </summary>
    /// <returns>"deb" 或 "rpm"。</returns>
    /// <exception cref="NotSupportedException">无法判断（既没有工具也没有 os-release 线索）。</exception>
    private static string DetectLinuxPackageFamily()
    {
        // 工具优先：deb 系发行版（UOS/深度/麒麟桌面版/Ubuntu）必然有 dpkg-deb（包管理器本体）
        if (FindOnPath("dpkg-deb") is not null)
            return "deb";
        // rpm 系发行版（openEuler/麒麟服务版/CentOS）必然有 rpm2cpio（rpm 包管理器自带）
        if (FindOnPath("rpm2cpio") is not null)
            return "rpm";
        // 退路：解析 /etc/os-release
        var osReleasePath = "/etc/os-release";
        if (File.Exists(osReleasePath))
        {
            // 文件全部键值行，传值示例（单个元素）："ID=uos"
            var lines = File.ReadAllLines(osReleasePath);
            // 从一行 "KEY=value" 里取出去引号的小写 value
            static string? ValueOf(string[] lines, string key)
            {
                foreach (var line in lines)
                    if (line.StartsWith(key + "=", StringComparison.Ordinal))
                        return line[(key.Length + 1)..].Trim().Trim('"').ToLowerInvariant();
                return null;
            }
            // 发行版 ID，传值示例："uos"、"openeuler"
            var id = ValueOf(lines, "ID");
            // 相似发行版列表，传值示例："ubuntu debian"（麒麟桌面）、"rhel fedora centos"（麒麟服务版）
            var idLike = ValueOf(lines, "ID_LIKE");
            // deb 家族关键词
            var debFamily = new[] { "debian", "ubuntu", "uos", "deepin", "kylin", "linuxmint" };
            // rpm 家族关键词
            var rpmFamily = new[] { "rhel", "fedora", "centos", "openeuler", "opensuse", "suse", "anolis", "alinux" };
            // 先看 ID_LIKE（更宽），再看 ID
            foreach (var family in new[] { idLike, id })
            {
                if (family is null)
                    continue;
                if (debFamily.Any(f => family.Contains(f, StringComparison.Ordinal)))
                    return "deb";
                if (rpmFamily.Any(f => family.Contains(f, StringComparison.Ordinal)))
                    return "rpm";
            }
        }
        throw new NotSupportedException(
            "无法判断本机的 Linux 包格式家族（未找到 dpkg-deb / rpm2cpio，/etc/os-release 也无线索）。" +
            "请手工安装 LibreOffice（apt/dnf），或告知包格式后手工解包。");
    }

    /// <summary>
    /// 在 PATH 目录里查找一个可执行程序。
    /// </summary>
    /// <param name="program">程序名。传值示例："dpkg-deb"</param>
    /// <returns>找到的完整路径；没有则 null。传值示例："/usr/bin/dpkg-deb"</returns>
    private static string? FindOnPath(string program)
    {
        // PATH 的全部目录，传值示例（单个元素）："/usr/bin"
        var directories = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.Combine(directory.Trim(), program))
            .FirstOrDefault(File.Exists);
    }

    // ───────────────────────── 分平台安装 ─────────────────────────

    /// <summary>
    /// Windows：msiexec /a 管理解包 MSI 到暂存目录（只解文件，不写注册表，不需要管理员权限）。
    /// </summary>
    /// <param name="msiPath">MSI 包完整路径。传值示例：@"D:\App\engines\downloads\LibreOffice_26.8.2_Win_x86-64.msi"</param>
    /// <param name="staging">暂存目录（TARGETDIR）。传值示例：@"D:\App\engines\libreoffice\.staging-1a2b3c"</param>
    /// <param name="cancellationToken">取消令牌。传值示例：CancellationToken.None</param>
    private static async Task InstallWindowsAsync(string msiPath, string staging, CancellationToken cancellationToken)
    {
        // TARGETDIR 末尾必须带反斜杠（MSI 的老规矩，少了会把最后一段目录名吃掉）
        var targetDirArgument = "TARGETDIR=" + staging.TrimEnd('\\') + "\\";
        // 管理解包：/a 管理安装、/qn 全程无界面；约 1-3 分钟
        var result = await ProcessRunner.RunAsync("msiexec.exe",
            ["/a", msiPath, targetDirArgument, "/qn"],
            TimeSpan.FromMinutes(10), cancellationToken);
        // 0 = 成功；3010 = 成功（要求重启，管理解包不会出现，兼容处理）
        if (result.ExitCode is not (0 or 3010))
            throw new InvalidOperationException($"msiexec /a 解包失败，退出码 {result.ExitCode}。{result.StandardError.Trim()}");
    }

    /// <summary>
    /// deb 系（UOS/深度/麒麟桌面版/Ubuntu）：解压 tar.gz 后逐个 dpkg-deb -x 解出（免 root）。
    /// </summary>
    /// <param name="tarGzPath">deb 系 tar.gz 包完整路径。传值示例："/data/engines/downloads/LibreOffice_26.8.2_Linux_aarch64_deb.tar.gz"</param>
    /// <param name="staging">暂存目录。传值示例："/data/engines/libreoffice/.staging-1a2b3c"</param>
    /// <param name="cancellationToken">取消令牌。传值示例：CancellationToken.None</param>
    private static async Task InstallDebAsync(string tarGzPath, string staging, CancellationToken cancellationToken)
    {
        // 解包工具必须在（deb 系发行版必然自带；没有说明环境异常）
        var dpkgDeb = FindOnPath("dpkg-deb")
            ?? throw new InvalidOperationException("没有找到 dpkg-deb。请改用 rpm 包，或用发行版包管理器安装 LibreOffice。");
        // 第 1 步：解开 tar.gz（C# 自带 TarReader，不依赖 tar 命令），得到 <暂存>/LibreOffice_*_deb/DEBS/*.deb
        ExtractTarGz(tarGzPath, staging);
        // 第 2 步：逐个把 .deb 解出文件到 work 子目录（约 30 个包）
        var work = Path.Combine(staging, "work");
        Directory.CreateDirectory(work);
        foreach (var deb in Directory.GetFiles(staging, "*.deb", SearchOption.AllDirectories))
            await ProcessRunner.RunAsync(dpkgDeb, ["-x", deb, work], TimeSpan.FromMinutes(2), cancellationToken);
    }

    /// <summary>
    /// rpm 系（openEuler/麒麟服务版/CentOS）：解压 tar.gz 后逐个 rpm 解出（免 root）。
    /// 优先用 rpm2archive（生成 tgz 再用 C# 解压，零外部依赖）；没有时退回 rpm2cpio + cpio。
    /// </summary>
    /// <param name="tarGzPath">rpm 系 tar.gz 包完整路径。传值示例："/data/engines/downloads/LibreOffice_26.8.2_Linux_x86-64_rpm.tar.gz"</param>
    /// <param name="staging">暂存目录。传值示例："/data/engines/libreoffice/.staging-1a2b3c"</param>
    /// <param name="cancellationToken">取消令牌。传值示例：CancellationToken.None</param>
    private static async Task InstallRpmAsync(string tarGzPath, string staging, CancellationToken cancellationToken)
    {
        // 第 1 步：解开 tar.gz，得到 <暂存>/LibreOffice_*_rpm/RPMS/*.rpm
        ExtractTarGz(tarGzPath, staging);
        // 第 2 步：逐个解出 rpm 到 work 子目录
        var work = Path.Combine(staging, "work");
        Directory.CreateDirectory(work);
        // 解包工具链：优先 rpm2archive（rpm ≥ 4.16，直接产出 tgz）；退回 rpm2cpio + cpio
        var rpm2archive = FindOnPath("rpm2archive");
        var rpm2cpio = FindOnPath("rpm2cpio");
        var cpio = FindOnPath("cpio");
        foreach (var rpm in Directory.GetFiles(staging, "*.rpm", SearchOption.AllDirectories))
        {
            if (rpm2archive is not null)
            {
                // rpm2archive 在 rpm 同目录生成 <名字>.rpm.tgz，用 C# 解压即可
                await ProcessRunner.RunAsync(rpm2archive, [rpm], TimeSpan.FromMinutes(2), cancellationToken);
                ExtractTarGz(rpm + ".tgz", work);
            }
            else if (rpm2cpio is not null && cpio is not null)
            {
                // 老版本 rpm：rpm2cpio 输出 cpio 流到文件，再由 cpio 解到 work。
                // 两个命令借助 sh 做重定向（路径都是本库生成的临时路径，且用 "$1"/"$2" 引用，无注入风险）
                var cpioFile = Path.Combine(staging, "payload.cpio");
                await ProcessRunner.RunAsync("/bin/sh",
                    ["-c", "rpm2cpio \"$1\" > \"$2\"", "sh", rpm, cpioFile], TimeSpan.FromMinutes(2), cancellationToken);
                await ProcessRunner.RunAsync("/bin/sh",
                    ["-c", "cpio -idmD \"$2\" --quiet < \"$1\"", "sh", cpioFile, work], TimeSpan.FromMinutes(2), cancellationToken);
            }
            else
            {
                throw new InvalidOperationException(
                    "没有找到 rpm2archive（或 rpm2cpio + cpio），无法免 root 解出 rpm。请安装 cpio（dnf install cpio），或改用 deb 包，或用包管理器安装 LibreOffice。");
            }
        }
    }

    // ───────────────────────── 通用辅助 ─────────────────────────

    /// <summary>
    /// 解压 tar.gz 到目标目录（.NET 自带 GZipStream + TarReader，不依赖 tar 命令）。
    /// 只解普通文件与目录（LibreOffice 包里没有符号链接依赖），并防御路径穿越。
    /// </summary>
    /// <param name="tarGzPath">tar.gz 文件完整路径。传值示例："/data/engines/downloads/LibreOffice_26.8.2_Linux_x86-64_deb.tar.gz"</param>
    /// <param name="destination">解压目标目录（须已存在）。传值示例："/data/engines/libreoffice/.staging-1a2b3c"</param>
    private static void ExtractTarGz(string tarGzPath, string destination)
    {
        // 目标目录的绝对路径前缀（用于防穿越比较）
        var destinationFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // 打开 gzip 流再交给 TarReader 逐条目解出
        using var fileStream = File.OpenRead(tarGzPath);
        using var gzipStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new TarReader(gzipStream);
        while (reader.GetNextEntry() is { } entry)
        {
            // 条目的目标路径；拼接后必须仍在目标目录内（防御 ../ 穿越）
            var entryPath = Path.GetFullPath(Path.Combine(destination, entry.Name));
            if (!entryPath.StartsWith(destinationFull, StringComparison.Ordinal))
                throw new InvalidDataException($"压缩包里的条目越出了目标目录：{entry.Name}");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(entryPath);
                    break;
                case TarEntryType.RegularFile:
                    // 先建父目录再落文件（覆盖同名旧文件）
                    Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                    entry.ExtractToFile(entryPath, overwrite: true);
                    break;
                // 其余类型（链接、设备等）LibreOffice 包中不存在，跳过
            }
        }
    }

    /// <summary>
    /// 在解包结果里定位 LibreOffice 根目录（含 program/soffice(.exe) 的那一层）。
    /// </summary>
    /// <param name="directory">解包结果目录。传值示例："/data/engines/libreoffice/.staging-1a2b3c/work"</param>
    /// <returns>LibreOffice 根目录完整路径；没有则 null。传值示例："/data/.../work/opt/libreoffice26.8"</returns>
    private static string? FindLibreOfficeRoot(string directory)
    {
        // 当前系统的程序名
        var programName = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";
        // 递归找 program/soffice(.exe)，取其 program 的上一级；
        // 要求候选目录下确实存在 program 子目录，排除误匹配（如 Maven 缓存式的同名路径）
        return Directory.EnumerateFiles(directory, programName, SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)                          // program 目录
            .Select(programDirectory => Path.GetDirectoryName(programDirectory)!)  // LibreOffice 根
            .FirstOrDefault(candidate => Directory.Exists(Path.Combine(candidate, "program")));
    }

    /// <summary>
    /// 给文件补可执行权限（仅 Linux/国产系统；Windows 为空操作）。
    /// </summary>
    /// <param name="path">文件完整路径。传值示例："/data/engines/libreoffice/linux-deb-x64/program/soffice"</param>
    private static void MakeExecutableOnUnix(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            // 在现有权限位上叠加三组执行位
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (IOException)
        {
            // 补权限失败不影响主流程（dpkg-deb -x 通常已带权限）
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
