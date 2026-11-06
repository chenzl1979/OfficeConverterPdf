// 演示程序：展示 LibreOffice.Core 类库的三种用法。
//
// 命令行用法（在 LibreOfficeDemo 目录下执行）：
//   dotnet run -- "C:\Docs\报告.docx" pdf                    ① 转换：文件 → 格式
//   dotnet run -- "C:\Docs\表格.xlsx" pdf "C:\Out\表格.pdf"   ② 转换：文件 → 格式 → 输出路径
//   dotnet run -- --selftest                                 ③ 自测：校验 filter 映射（不需要 LibreOffice）
//   dotnet run                                               ④ 无参数：打印用法与能力清单

using LibreOffice.Core;

namespace LibreOfficeDemo;

/// <summary>
/// 演示程序入口类：按命令行参数分发到"转换 / 自测 / 打印用法"三种模式。
/// </summary>
public static class Program
{
    /// <summary>
    /// 程序入口。
    /// </summary>
    /// <param name="args">
    /// 命令行参数数组。传值示例：
    ///   new[] { @"C:\Docs\报告.docx", "pdf" }                          → 执行转换；
    ///   new[] { @"C:\Docs\表格.xlsx", "pdf", @"C:\Out\表格.pdf" }      → 执行转换并指定输出路径；
    ///   new[] { "--selftest" }                                        → 运行自测；
    ///   new string[0]                                                 → 打印用法。
    /// </param>
    /// <returns>退出码：0 成功，1 失败。</returns>
    public static async Task<int> Main(string[] args)
    {
        // 模式③：--selftest → 运行内置的 filter 映射自测
        if (args.Length == 1 && args[0] == "--selftest")
            return SelfTest();
        // 模式③'：--install → 从预置包自动安装 LibreOffice（按电脑配置自动选包，离线可用）
        if (args.Length == 1 && args[0] == "--install")
            return await InstallAsync();
        // 模式④：无参数 → 打印用法与能力清单
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }
        // 模式①②：参数至少要有输入文件与目标格式
        if (args.Length < 2)
        {
            Console.WriteLine("参数不足。用法：dotnet run -- <输入文件> <目标格式> [输出路径]");
            return 1;
        }

        // 第 1 个参数：输入文件路径，传值示例：@"C:\Docs\报告.docx"
        var inputPath = args[0];
        // 第 2 个参数：目标格式，传值示例："pdf"
        var targetFormat = args[1];
        // 第 3 个参数（可选）：输出路径，传值示例：@"C:\Out\报告.pdf"；未传时为 null
        var outputPath = args.Length >= 3 ? args[2] : null;

        // 执行转换（真实用法就这两行，更多示例见 BatchConvertAsync 的注释）
        try
        {
            using var converter = new DocumentConverter();               // 创建转换器（自动定位 soffice）
            var result = await converter.ConvertAsync(inputPath, targetFormat, outputPath);  // 转换
            Console.WriteLine($"✔ 转换成功：{result.OutputPath}");
            Console.WriteLine($"  使用 soffice：{result.SofficePath}");
            Console.WriteLine($"  使用 filter ：{result.Filter}");
            Console.WriteLine($"  耗时：{result.Elapsed.TotalSeconds:0.0} 秒");
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or NotSupportedException or LibreOfficeException
                                   or TimeoutException or OperationCanceledException)
        {
            // 把各类失败用中文说清楚（文件不存在 / 格式不支持 / 转换失败 / 超时 / 取消）
            Console.WriteLine($"✘ 转换失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>打印用法说明与当前环境的能力清单（soffice 位置、支持的格式）。</summary>
    private static void PrintUsage()
    {
        Console.WriteLine("LibreOffice 文档格式转换 —— 演示程序");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run -- <输入文件> <目标格式> [输出路径]");
        Console.WriteLine("  dotnet run -- --selftest        （运行自测，不需要 LibreOffice）");
        Console.WriteLine("  dotnet run -- --install         （从预置包自动安装 LibreOffice，离线可用）");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  Windows：dotnet run -- \"C:\\Docs\\报告.docx\" pdf");
        Console.WriteLine("  Windows：dotnet run -- \"C:\\Docs\\表格.xlsx\" pdf \"C:\\Out\\表格.pdf\"");
        Console.WriteLine("  Linux（UOS/麒麟等）：dotnet run -- \"/home/user/文档/报告.docx\" pdf");
        Console.WriteLine();
        // 显示当前系统，提示支持的运行环境
        Console.WriteLine($"当前系统：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        // 探测本机的 soffice，让用户对环境心中有数
        var soffice = DocumentConverter.FindSoffice();
        Console.WriteLine(soffice is null
            ? "本机未找到 LibreOffice（soffice）。请安装 LibreOffice 后重试。"
            : $"已找到 LibreOffice：{soffice}");
        Console.WriteLine();
        Console.WriteLine($"可输入格式（{DocumentConverter.SupportedInputFormats.Count} 种）：{string.Join(" ", DocumentConverter.SupportedInputFormats)}");
        Console.WriteLine($"可输出格式（{DocumentConverter.SupportedTargetFormats.Count} 种）：{string.Join(" ", DocumentConverter.SupportedTargetFormats)}");
        Console.WriteLine("注意：不支持导出 OFD（LibreOffice 没有 OFD 导出过滤器）。");
    }

    /// <summary>
    /// 运行内置自测：校验 filter 映射与能力表的正确性（纯逻辑，不需要安装 LibreOffice）。
    /// </summary>
    /// <returns>全部通过返回 0；任何一条失败返回 1。</returns>
    private static int SelfTest()
    {
        // 通过的用例计数
        var passed = 0;
        // 失败的用例计数
        var failed = 0;

        Console.WriteLine("自测 1/2：filter 映射");
        CheckFilter("docx", "pdf", false, "pdf:writer_pdf_Export");
        CheckFilter("xlsx", "pdf", false, "pdf:calc_pdf_Export");
        CheckFilter("pptx", "pdf", false, "pdf:impress_pdf_Export");
        CheckFilter("vsd", "pdf", false, "pdf:draw_pdf_Export");
        CheckFilter("txt", "pdf", false, "pdf:writer_pdf_Export");
        CheckFilter("docx", "pdf", true, "pdf:writer_pdf_Export:{\"SelectPdfVersion\":{\"type\":\"long\",\"value\":\"2\"}}");
        CheckFilter("doc", "docx", false, "docx:MS Word 2007 XML");
        CheckFilter("xls", "xlsx", false, "xlsx:Calc MS Excel 2007 XML");
        CheckFilter("ppt", "odp", false, "odp");
        CheckFilter("csv", "pdf", false, "pdf:calc_pdf_Export");
        CheckFilter("pdf", "svg", false, "svg:draw_svg_Export");
        CheckFilter("et", "pdf", false, "pdf:calc_pdf_Export");       // WPS 表格 → calc 家族
        CheckFilter("key", "pdf", false, "pdf:impress_pdf_Export");   // Keynote → impress 家族

        Console.WriteLine("自测 2/2：能力表");
        CheckCan("doc", "pdf", true);
        CheckCan("docx", "ofd", false);   // OFD 不受支持
        CheckCan("ppt", "png", false);    // 能力表中演示 → png 不存在（png 只从绘图类格式导出）
        CheckCan("vsd", "png", true);
        CheckCan("svg", "pdf", false);
        CheckCan("pdf", "svg", true);

        Console.WriteLine();
        Console.WriteLine($"自测结果：{passed} 通过，{failed} 失败");
        return failed == 0 ? 0 : 1;

        /// <summary>断言一个 filter 映射结果；失败时打印实际值。</summary>
        void CheckFilter(string from, string to, bool pdfA, string expected)
        {
            // 实际映射结果（通过反射调用类库 internal 的 FilterFor）
            var actual = InvokeFilterFor(from, to, pdfA);
            // 比较并报告
            if (actual == expected)
            {
                passed++;
                Console.WriteLine($"  ✔ {from} → {to}{(pdfA ? " (PDF/A)" : "")} = {actual}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  ✘ {from} → {to}{(pdfA ? " (PDF/A)" : "")}：期望 {expected}，实际 {actual}");
            }
        }

        /// <summary>断言一个能力表查询结果。</summary>
        void CheckCan(string from, string to, bool expected)
        {
            // 实际查询结果
            var actual = LibreOfficeFormats.CanConvert(from, to);
            // 比较并报告
            if (actual == expected)
            {
                passed++;
                Console.WriteLine($"  ✔ CanConvert(\"{from}\", \"{to}\") = {actual.ToString().ToLowerInvariant()}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  ✘ CanConvert(\"{from}\", \"{to}\")：期望 {expected.ToString().ToLowerInvariant()}，实际 {actual.ToString().ToLowerInvariant()}");
            }
        }
    }

    /// <summary>
    /// 从预置包自动安装 LibreOffice（演示 --install 模式）：
    /// 按电脑配置自动选包并本地解包（免管理员/root），装完打印 soffice 路径。
    /// </summary>
    /// <returns>安装成功返回 0；失败返回 1。</returns>
    private static async Task<int> InstallAsync()
    {
        // 显示自动选包结果，让用户知道本机将使用哪个包
        Console.WriteLine($"本机平台键：{LibreOfficeInstaller.DetectPlatformKey()}");
        Console.WriteLine("开始从预置包安装（Windows 约 1-3 分钟 / Linux 约 1-2 分钟，全程免管理员/root）…");
        try
        {
            // 自动选包 + 解包 + 归一化（已安装时幂等跳过）；装好后 DocumentConverter 即可自动使用
            var soffice = await LibreOfficeInstaller.InstallAsync();
            Console.WriteLine($"✔ LibreOffice 就绪：{soffice}");
            // 同步预置中文字体到 LibreOffice 的 share\fonts\truetype（防"豆腐块"；幂等）
            var fontsCopied = await FontInstaller.InstallAsync();
            Console.WriteLine(fontsCopied > 0
                ? $"✔ 中文字体已就位（复制 {fontsCopied} 个文件）"
                : "✔ 中文字体已就位（无变化）");
            Console.WriteLine("现在可以直接转换：dotnet run -- <输入文件> <目标格式>");
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or NotSupportedException or InvalidOperationException)
        {
            // 缺包 / 平台不支持 / 解包失败，给出中文原因
            Console.WriteLine($"✘ 安装失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 通过反射调用类库内部的 ConversionFilter.FilterFor（它是 internal 的，演示程序借反射验证其输出；
    /// 公开使用路径是 DocumentConverter.ConvertAsync，它内部就是调这个方法）。
    /// </summary>
    /// <param name="from">源格式。传值示例："docx"</param>
    /// <param name="to">目标格式。传值示例："pdf"</param>
    /// <param name="pdfA">是否 PDF/A。传值示例：false</param>
    /// <returns>filter 规格字符串。传值示例："pdf:writer_pdf_Export"</returns>
    private static string InvokeFilterFor(string from, string to, bool pdfA)
    {
        // 反射拿到 ConversionFilter 类型的 FilterFor 静态方法（非公开）
        var method = typeof(ConversionFilter).GetMethod("FilterFor",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        // 调用并转成字符串返回（第 4 个参数 extension 传 null）
        return (string)method!.Invoke(null, [from, to, pdfA, null])!;
    }

    /// <summary>
    /// —— 编程调用示例（供复制到你的代码里）——
    /// 本方法不参与命令行流程，仅作为"如何在你的程序里调用本库"的注释化示例保留；
    /// 演示：批量把一个目录里的全部 .doc 转成 .pdf。
    /// </summary>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   await Program.BatchConvertAsync();   // 通常不会这样调它，而是把下面的代码复制走改一改
    /// </code>
    /// </remarks>
    public static async Task BatchConvertAsync()
    {
        // 创建转换器（自动定位 soffice；也可显式传路径，如 new DocumentConverter(@"D:\LibreOffice\program\soffice.exe")）
        using var converter = new DocumentConverter();
        // 遍历目录里全部 .doc 文件，逐个转换
        foreach (var file in Directory.EnumerateFiles(@"C:\Docs", "*.doc"))
        {
            // 执行转换：目标格式 pdf，输出到源文件旁边
            ConversionResult result = await converter.ConvertAsync(file, "pdf");
            // 打印进度信息
            Console.WriteLine($"{file} → {result.OutputPath}（{result.Elapsed.TotalSeconds:0.0} 秒）");
        }
    }
}
