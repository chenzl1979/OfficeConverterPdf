// 主封装类：把 Filee 项目中 LibreOffice 文档转换的全部要点封闭到一个 ConvertAsync 方法里。
// 提取自 Filee 项目的 src/Filee.Engines/Office/LibreOfficeConverter.cs，要点包括：
//  * 每个并发 soffice 进程必须使用独立的用户 profile（-env:UserInstallation），否则并发转换会静默失败（退出码 1）；
//  * soffice 把结果写到 <输出目录>/<输入名>.<目标扩展名>：先转入私有临时目录，再取其中第一个非空文件移动到位；
//  * 必须设超时：遇到带模态对话框的文档否则会永远挂起；
//  * 产物为空或缺失时按失败处理，抛出带 soffice 错误输出的异常。
//
// 跨平台支持：Windows 10/11、Linux x64/ARM64 以及国产 Linux 系统（UOS/深度、银河麒麟、中标麒麟、
// openEuler、中科方德、龙芯 Loongnix 等）。全部实现为托管代码（net10.0，无 P/Invoke、无本机依赖），
// 只要有对应平台的 .NET 10 运行时即可运行；Linux 上 soffice 是外壳脚本，用 Process 启动即可。
// 注意：Flatpak 版 LibreOffice 沙箱化无法直接调用，请用发行版仓库版本（详见 SofficeLocator 注释）。

namespace LibreOffice.Core;

/// <summary>
/// 文档格式转换器（基于 LibreOffice 无头模式）：把 DOC/DOCX/XLS/XLSX/PPT/PPTX/ODF/RTF/CSV 等文档
/// 转换为 PDF、DOCX、XLSX 等目标格式，不需要安装 Microsoft Office。
/// 支持平台：Windows 10/11、Linux x64/ARM64、国产 Linux 系统（UOS/深度、银河麒麟、openEuler、龙芯 Loongnix 等）。
/// 典型用途：Office 文档 → PDF（服务端批量转换、归档）。
/// 限制：不支持导出 OFD——LibreOffice 本身没有 OFD 导出过滤器（Filee 源项目中 OFD 也是只读格式）。
/// 用法：先创建实例（构造时定位 soffice），然后反复调用 <see cref="ConvertAsync"/>；线程安全，实例可全局复用。
/// </summary>
/// <remarks>
/// 调用示例（最简：docx 转 pdf，输出到源文件旁边）：
/// <code>
///   using var converter = new DocumentConverter();
///   var result = await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
///   Console.WriteLine($"已生成：{result.OutputPath}");
/// </code>
/// </remarks>
public sealed class DocumentConverter : IDisposable
{
    /// <summary>本次使用的 soffice 可执行文件完整路径（构造时确定）。传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"</summary>
    private readonly string _soffice;

    /// <summary>转换选项（构造时传入或使用默认值；选项对象本身不可变，可安全共享）。传值示例：new ConversionOptions()</summary>
    private readonly ConversionOptions _options;

    /// <summary>
    /// 并发闸门：限制同时运行的 soffice 进程数不超过 MaxDegreeOfParallelism。
    /// 传值示例（内部构造）：new SemaphoreSlim(2, 2)
    /// </summary>
    private readonly SemaphoreSlim _capacity;

    /// <summary>
    /// 空闲 profile 槽位号栈（0..并发数-1）：每个并发 soffice 进程占用一个独立 profile，互不干扰。
    /// 传值示例（内部构造，2 个并发时内容为）：栈 [1, 0]
    /// </summary>
    private readonly Stack<int> _freeProfiles;

    /// <summary>是否已释放（Dispose 后禁止再发起转换）。传值示例：false</summary>
    private bool _disposed;

    /// <summary>
    /// 创建转换器。构造时定位 soffice：找不到立即抛 <see cref="FileNotFoundException"/>，
    /// 以便尽早暴露"本机没装 LibreOffice"的问题。
    /// </summary>
    /// <param name="sofficePath">
    /// soffice 程序的完整路径；传 null 则自动查找（PATH 与标准安装位置）。
    /// 传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"；传 null 表示自动查找
    /// </param>
    /// <param name="options">转换选项；传 null 使用全部默认值（3 分钟超时、普通 PDF、最多 2 个并发）。传值示例：new ConversionOptions { PdfA = true }</param>
    /// <exception cref="FileNotFoundException">本机找不到 LibreOffice（soffice）。</exception>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   // 自动查找 soffice
    ///   using var converter = new DocumentConverter();
    ///   // 或显式指定 soffice 路径与选项
    ///   using var converter2 = new DocumentConverter(
    ///       @"D:\LibreOffice\program\soffice.exe",
    ///       new ConversionOptions { Timeout = TimeSpan.FromMinutes(5) });
    /// </code>
    /// </remarks>
    public DocumentConverter(string? sofficePath = null, ConversionOptions? options = null)
    {
        // 保存选项（null 时用默认值）
        _options = options ?? new ConversionOptions();
        // 有效并发数：夹在 1..16 之间，防止无效配置
        var workers = Math.Clamp(_options.MaxDegreeOfParallelism, 1, 16);
        // 并发闸门与槽位栈按有效并发数初始化（栈倒序压入，取号从 0 开始）
        _capacity = new SemaphoreSlim(workers, workers);
        _freeProfiles = new Stack<int>(Enumerable.Range(0, workers).Reverse());
        // 定位 soffice：显式路径优先，其次自动查找；找不到抛出带说明的异常
        _soffice = SofficeLocator.Locate(sofficePath)
            ?? throw new FileNotFoundException(
                "未找到 LibreOffice（soffice）。请安装 LibreOffice，或通过 sofficePath 参数指定 soffice 程序的完整路径。" +
                $"已尝试的查找范围：PATH 环境变量与 {Environment.OSVersion.Platform} 的标准安装位置。");
    }

    // ───────────────────────── 工厂（离线自动安装） ─────────────────────────

    /// <summary>
    /// 创建转换器；本机找不到 soffice 时，自动从随库预置的安装包（engines\downloads）安装——
    /// 按电脑配置（操作系统 + CPU 架构 + deb/rpm 包系）自动选包，免管理员/root 本地解包，然后继续。
    /// 离线部署推荐入口：把 engines 目录随程序拷到目标电脑，第一次调用本方法即完成"安装 + 就绪"。
    /// </summary>
    /// <param name="sofficePath">
    /// soffice 程序的完整路径；传 null 则自动查找（预置解包目录 → PATH → 系统标准位置），
    /// 全都找不到时尝试从预置包自动安装。传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"；传 null 表示自动
    /// </param>
    /// <param name="options">转换选项；传 null 使用全部默认值。传值示例：new ConversionOptions { PdfA = true }</param>
    /// <param name="cancellationToken">自动安装过程的取消令牌（安装约需 1-3 分钟）。传值示例：CancellationToken.None</param>
    /// <returns>就绪的转换器实例。</returns>
    /// <exception cref="FileNotFoundException">找不到 soffice 且自动安装失败（没有匹配的预置包等）。</exception>
    /// <remarks>
    /// 调用示例（离线电脑上的首次初始化，之后即可直接转换）：
    /// <code>
    ///   // engines 目录里已放置预置包；本机没有 LibreOffice 时自动解包安装
    ///   using var converter = await DocumentConverter.CreateAsync();
    ///   var result = await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
    /// </code>
    /// </remarks>
    public static async Task<DocumentConverter> CreateAsync(
        string? sofficePath = null,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 第 1 步：常规定位（预置解包目录 → PATH → 系统标准位置）
        var located = SofficeLocator.Locate(sofficePath);
        if (located is null)
        {
            // 第 2 步：没有就尝试从预置包自动安装（按电脑配置自动选包；幂等，已装过会直接返回）
            await LibreOfficeInstaller.InstallAsync(cancellationToken: cancellationToken);
            // 第 3 步：装完再定位一次
            located = SofficeLocator.Locate(sofficePath)
                ?? throw new FileNotFoundException(
                    "自动安装后仍未找到 LibreOffice（soffice）。请检查 engines 目录与预置包是否匹配本机配置：" +
                    $"本机平台键为 {LibreOfficeInstaller.DetectPlatformKey()}。");
        }
        // 第 4 步：补装预置中文字体到 engines 管理的 LibreOffice（幂等，已就位秒级跳过），
        // 避免离线/国产环境缺中文字体导致 PDF"豆腐块"；失败不阻断（回退系统字体）
        try { await FontInstaller.InstallAsync(cancellationToken: cancellationToken); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        // 用定位到的路径构造转换器（构造函数不再重复定位）
        return new DocumentConverter(located, options);
    }

    // ───────────────────────── 静态辅助 ─────────────────────────

    /// <summary>
    /// 在本机查找 soffice 程序（不创建转换器即可预检）。
    /// </summary>
    /// <returns>找到的 soffice 完整路径；找不到返回 null。传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"</returns>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   if (DocumentConverter.FindSoffice() is null)
    ///       Console.WriteLine("请先安装 LibreOffice");
    /// </code>
    /// </remarks>
    public static string? FindSoffice() => SofficeLocator.Locate();

    /// <summary>
    /// 查询一种格式能否转换为另一种格式（能力表与 Filee 源项目一致，不含 OFD 目标）。
    /// </summary>
    /// <param name="fromFormat">源格式扩展名，大小写不限、可带前导点。传值示例："doc" 或 ".doc"</param>
    /// <param name="toFormat">目标格式扩展名，大小写不限、可带前导点。传值示例："pdf"；"ofd" 恒为 false</param>
    /// <returns>支持返回 true，否则 false。</returns>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   bool ok = DocumentConverter.CanConvert("ppt", "pdf");    // true
    ///   bool no = DocumentConverter.CanConvert("docx", "ofd");   // false
    /// </code>
    /// </remarks>
    public static bool CanConvert(string fromFormat, string toFormat) =>
        LibreOfficeFormats.CanConvert(fromFormat, toFormat);

    /// <summary>
    /// 全部可读的输入格式（小写扩展名，不含点）。
    /// 传值示例（单个元素）："doc"、"wps"、"key"、"vsd"
    /// </summary>
    public static IReadOnlyList<string> SupportedInputFormats => LibreOfficeFormats.SupportedInputFormats;

    /// <summary>
    /// 全部可写的目标格式（小写扩展名，不含点；不含 "ofd"）。
    /// 传值示例（单个元素）："pdf"、"docx"、"xlsx"、"png"
    /// </summary>
    public static IReadOnlyList<string> SupportedTargetFormats => LibreOfficeFormats.SupportedTargetFormats;

    // ───────────────────────── 核心方法 ─────────────────────────

    /// <summary>
    /// 把一个文档文件转换为指定格式（本库被封闭成的一个方法）。
    /// 流程：定位 soffice → 领取独立 profile 槽位 → 在私有临时目录里运行 soffice 无头转换 →
    /// 取第一个非空产物移动到目标路径 → 归还槽位并清理临时目录。
    /// </summary>
    /// <param name="inputPath">输入文件的完整路径，文件必须已存在。传值示例：@"C:\Docs\报告.docx"</param>
    /// <param name="targetFormat">
    /// 目标格式的小写扩展名。传值示例："pdf"（最常用）；也支持 "docx"、"xlsx"、"pptx"、"odt"、"ods"、"odp"、"html"、"txt"、"csv"、"png" 等，
    /// 完整清单见 <see cref="SupportedTargetFormats"/>；传 "ofd" 抛 <see cref="NotSupportedException"/>（LibreOffice 不支持导出 OFD）。
    /// </param>
    /// <param name="outputPath">
    /// 输出文件的完整路径；传 null 时输出到输入文件旁边、同名换扩展名。目录不存在会自动创建，同名文件会被覆盖。
    /// 传值示例：@"C:\Out\报告.pdf"；传 null 表示"源文件旁同名"
    /// </param>
    /// <param name="cancellationToken">取消令牌；取消后会杀死 soffice 进程树并抛 <see cref="OperationCanceledException"/>。传值示例：CancellationToken.None</param>
    /// <returns>转换结果（输出路径、耗时、退出码等）。</returns>
    /// <exception cref="FileNotFoundException">输入文件不存在，或构造时未找到 soffice。</exception>
    /// <exception cref="NotSupportedException">目标格式不受支持（含 OFD），或该源格式到该目标格式没有转换路线。</exception>
    /// <exception cref="LibreOfficeException">soffice 运行结束但没有产出任何文件（文件损坏、格式不识别等）。</exception>
    /// <exception cref="TimeoutException">转换超过 <see cref="ConversionOptions.Timeout"/>（进程树已被杀死）。</exception>
    /// <exception cref="OperationCanceledException">调用方取消了转换。</exception>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   using var converter = new DocumentConverter();
    ///   // ① 最常用：docx → pdf，输出到源文件旁
    ///   var r1 = await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
    ///   // ② 指定输出路径：xlsx → pdf
    ///   var r2 = await converter.ConvertAsync(@"C:\Docs\表格.xlsx", "pdf", @"C:\Out\表格.pdf");
    ///   // ③ 老 Office 格式：doc/ppt/xls 同样一行
    ///   var r3 = await converter.ConvertAsync(@"C:\Docs\旧文档.doc", "pdf");
    /// </code>
    /// </remarks>
    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        string targetFormat,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        // 防止 Dispose 后再使用
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 参数非空校验（给出明确的参数名）
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFormat);

        // 输入转绝对路径并检查存在性
        var fullInput = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInput))
            throw new FileNotFoundException($"输入文件不存在：{fullInput}", fullInput);

        // 规整目标格式：去空白、去前导点、转小写
        var to = targetFormat.Trim().TrimStart('.').ToLowerInvariant();
        // OFD 特判：给出中文说明的明确异常，而不是让 soffice 报"未知过滤器"
        if (to == "ofd")
            throw new NotSupportedException(
                "LibreOffice 不支持导出 OFD 格式（OFD 是中国 GB/T 33190 版式文档标准，" +
                "LibreOffice 与 Filee 源项目都只能读取 OFD、不能生成 OFD）。目标格式请改为 pdf。");
        // 规整源格式（取输入文件扩展名）
        var from = Path.GetExtension(fullInput).TrimStart('.').ToLowerInvariant();
        // 能力校验：该组合必须在能力表里
        if (!LibreOfficeFormats.CanConvert(from, to))
            throw new NotSupportedException(
                $"不支持的转换：{from} → {to}。可输入格式见 DocumentConverter.SupportedInputFormats，" +
                "可输出格式见 DocumentConverter.SupportedTargetFormats。");

        // 最终输出路径：未指定时放在输入文件旁边、同名换扩展名
        var fullOutput = outputPath is null
            ? Path.Combine(Path.GetDirectoryName(fullInput)!, Path.GetFileNameWithoutExtension(fullInput) + "." + to)
            : Path.GetFullPath(outputPath);
        // 确保输出目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);

        // 本次转换使用的 filter 规格，传值示例："pdf:writer_pdf_Export"
        var filter = ConversionFilter.FilterFor(from, to, _options.PdfA);

        // 领取并发槽位（同时最多 MaxDegreeOfParallelism 个 soffice 在跑）
        var slot = await TakeProfileAsync(cancellationToken);
        try
        {
            // 字体保障：预置中文字体未就位时补装进 engines 管理的 LibreOffice（幂等；
            // 已就位时只是一次文件存在检查，首次约 1-2 秒），防离线/国产环境 PDF"豆腐块"；
            // 失败不阻断转换（回退系统字体）
            try
            {
                if (!FontInstaller.IsInstalled())
                    await FontInstaller.InstallAsync(cancellationToken: cancellationToken);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            // 本次转换的私有工作目录（临时输出放里面，结束即删）
            var workDirectory = Path.Combine(Path.GetTempPath(), "LibreOfficeConverter", Guid.NewGuid().ToString("N")[..12]);
            // soffice 的输出子目录，传值示例：C:\Users\我\AppData\Local\Temp\LibreOfficeConverter\1a2b3c\lo
            var outDirectory = Path.Combine(workDirectory, "lo");
            // 本槽位专属的 LibreOffice 用户 profile 目录（首次会建缓存，之后复用变快）
            var profileDirectory = ProfileDirectory(slot);
            Directory.CreateDirectory(outDirectory);
            Directory.CreateDirectory(profileDirectory);

            // 组装命令行参数：无头、无恢复、无锁、独立 profile、指定 filter 与输出目录
            var arguments = new List<string>
            {
                "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
                // profile 路径必须是 file:/// URI 形式（LibreOffice 的要求）
                "-env:UserInstallation=" + new Uri(profileDirectory).AbsoluteUri,
                "--convert-to", filter,
                "--outdir", outDirectory,
                fullInput,
            };

            try
            {
            // 计时开始
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // 运行 soffice（超时/取消会杀整棵进程树；附加选项里指定的环境变量）
            var processResult = await ProcessRunner.RunAsync(_soffice, arguments, _options.Timeout, cancellationToken,
                _options.OnOutput, _options.EnvironmentVariables);
                // 计时结束
                stopwatch.Stop();

                // 在输出目录里找第一个非空文件（soffice 按 "<输入名>.<目标扩展名>" 命名，但不同 filter 可能略有差异，扫描最稳）
                var produced = Directory.EnumerateFiles(outDirectory).FirstOrDefault(f => new FileInfo(f).Length > 0)
                    ?? throw new LibreOfficeException(
                        $"LibreOffice 未产出任何文件（退出码 {processResult.ExitCode}）。输入：{fullInput}",
                        processResult.ExitCode,
                        processResult.StandardError.Trim());

                // 把产物移动到最终输出路径（允许覆盖同名旧文件）
                File.Move(produced, fullOutput, overwrite: true);

                // 组装并返回结果
                return new ConversionResult(fullOutput, _soffice, filter, stopwatch.Elapsed,
                    processResult.ExitCode, processResult.StandardOutput, processResult.StandardError);
            }
            finally
            {
                // 无论成功失败都清理本次的临时工作目录（产物已移走；profile 目录在别处，缓存保留复用）
                try { Directory.Delete(workDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            // 无论成功失败都要归还槽位，否则并发闸门会慢慢漏光
            ReturnProfile(slot);
        }
    }

    // ───────────────────────── profile 槽位管理 ─────────────────────────

    /// <summary>
    /// 计算某个槽位的 LibreOffice 用户 profile 目录：&lt;ProfileRoot&gt;\worker-&lt;槽位号&gt;。
    /// </summary>
    /// <param name="slot">槽位号（0 起始，小于并发数）。传值示例：0</param>
    /// <returns>该槽位的 profile 目录完整路径。传值示例：C:\Users\我\AppData\Local\Temp\LibreOfficeConverter-profiles\worker-0</returns>
    private string ProfileDirectory(int slot) =>
        Path.Combine(_options.ProfileRoot ?? DefaultProfileRoot, $"worker-{slot}");

    /// <summary>profile 根目录的默认值：系统临时目录下的 LibreOfficeConverter-profiles 文件夹。</summary>
    private static string DefaultProfileRoot =>
        Path.Combine(Path.GetTempPath(), "LibreOfficeConverter-profiles");

    /// <summary>
    /// 领取一个空闲 profile 槽位：先过并发闸门（满了就排队），再从空闲栈里弹一个槽位号。
    /// </summary>
    /// <param name="cancellationToken">排队等待期间也可取消。传值示例：CancellationToken.None</param>
    /// <returns>槽位号。传值示例：0</returns>
    private async Task<int> TakeProfileAsync(CancellationToken cancellationToken)
    {
        // 并发闸门：已有 MaxDegreeOfParallelism 个转换在跑时在这里等待
        await _capacity.WaitAsync(cancellationToken);
        // 加锁弹出槽位号（栈本身不是线程安全的）
        lock (_freeProfiles)
            return _freeProfiles.Pop();
    }

    /// <summary>
    /// 归还 profile 槽位：槽位号压回空闲栈，并释放并发闸门的一个名额。
    /// </summary>
    /// <param name="slot">要归还的槽位号。传值示例：0</param>
    private void ReturnProfile(int slot)
    {
        // 加锁压回槽位号
        lock (_freeProfiles)
            _freeProfiles.Push(slot);
        // 释放一个并发名额
        _capacity.Release();
    }

    // ───────────────────────── 释放 ─────────────────────────

    /// <summary>
    /// 释放并发闸门等资源。转换器用完应当 Dispose（配合 using）。
    /// </summary>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   using (var converter = new DocumentConverter())
    ///   {
    ///       await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
    ///   }   // 离开 using 时自动调用 Dispose
    /// </code>
    /// </remarks>
    public void Dispose()
    {
        // 标记已释放（转换入口会检查）
        _disposed = true;
        // 释放并发闸门
        _capacity.Dispose();
    }
}
