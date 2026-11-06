// LibreOffice 能读/能写的格式清单与"格式家族"判定。
// 忠实移植自 Filee 项目的 src/Filee.Engines/Office/LibreOfficeConverter.cs（格式数组与 FamilyOf/BuildEdges）。
// 说明：OFD（GB/T 33190 版式文档）不在清单里——LibreOffice 不支持导出 OFD，Filee 源项目中 OFD 也是只读格式。

namespace LibreOffice.Core;

/// <summary>
/// LibreOffice 支持的输入/输出格式清单、格式家族（writer/calc/impress/draw）判定与格式对（from → to）能力表。
/// 全部成员为静态，可直接通过类名使用。
/// </summary>
/// <remarks>
/// 调用示例：
/// <code>
///   bool ok = LibreOfficeFormats.CanConvert("doc", "pdf");         // true：doc 可以转 pdf
///   bool no = LibreOfficeFormats.CanConvert("docx", "ofd");        // false：LibreOffice 不能导出 OFD
///   foreach (var format in LibreOfficeFormats.SupportedTargetFormats)
///       Console.WriteLine(format);                                 // 逐行打印全部可用的目标格式
/// </code>
/// </remarks>
public static class LibreOfficeFormats
{
    // ───────────────────────── 可读写的格式（按 LibreOffice 应用家族） ─────────────────────────

    /// <summary>文字处理（Writer）家族可读可写的格式。传值示例（单个元素）："docx"、"doc"、"odt"、"rtf"、"fodt"、"ott"</summary>
    internal static readonly string[] Writer = ["docx", "doc", "odt", "rtf", "fodt", "ott"];

    /// <summary>表格（Calc）家族可读可写的格式。传值示例（单个元素）："xlsx"、"xls"、"ods"、"csv"、"xlt"、"ots"</summary>
    internal static readonly string[] Calc = ["xlsx", "xls", "ods", "csv", "xlt", "ots"];

    /// <summary>演示（Impress）家族可读可写的格式。传值示例（单个元素）："pptx"、"ppt"、"odp"、"otp"</summary>
    internal static readonly string[] Impress = ["pptx", "ppt", "odp", "otp"];

    // ───────────────────────── 只有 LibreOffice 能读的老旧/小众格式（只读） ─────────────────────────
    // 对照 LibreOffice 26.2 的过滤器注册表整理（同 Filee 源项目注释）。

    /// <summary>只能读入的文字处理格式。传值示例（单个元素）："wps"、"wpd"、"pages"、"abw"、"dot"、"lwp"、"sdw"、"cwk"、"mcw"、"602"、"psw"</summary>
    internal static readonly string[] WriterImport = ["dot", "wps", "wpd", "lwp", "abw", "pages", "sdw", "cwk", "mcw", "602", "psw"];

    /// <summary>只能读入的表格格式。传值示例（单个元素）："et"（WPS 表格）、"numbers"（Apple）、"sdc"（StarOffice）、"fods"</summary>
    internal static readonly string[] CalcImport = ["et", "numbers", "sdc", "fods"];

    /// <summary>只能读入的演示格式；"sda" 同时覆盖 .sdd（见 FamilyOf 的特判）。传值示例（单个元素）："key"（Apple Keynote）、"dps"（WPS 演示）、"sda"、"fodp"</summary>
    internal static readonly string[] ImpressImport = ["key", "dps", "sda", "fodp"];

    /// <summary>虽在 Impress 中打开、但按绘图（Draw）方式导出的格式。传值示例（单个元素）："cgm"</summary>
    internal static readonly string[] ImpressGraphics = ["cgm"];

    /// <summary>绘图（Draw）家族的格式。传值示例（单个元素）："pub"（Publisher）、"cdr"（CorelDRAW）、"vsd"（Visio）、"odg"、"fodg"、"emf"、"wmf"</summary>
    internal static readonly string[] Draw = ["pub", "cdr", "vsd", "odg", "fodg", "emf", "wmf"];

    /// <summary>绘图可以导出成的目标格式；svg/png/emf/wmf 只含第一页。传值示例（单个元素）："pdf"、"odg"、"svg"、"png"、"emf"、"wmf"</summary>
    internal static readonly string[] DrawTargets = ["pdf", "odg", "svg", "png", "emf", "wmf"];

    /// <summary>
    /// 虽在 Draw 中打开、但限定只转到部分格式的格式（Filee 中有内置引擎处理其它方向，这里保留同款限制）。
    /// 传值示例（单个元素）："svg"、"pdf"
    /// </summary>
    internal static readonly string[] DrawOnlyFor = ["svg", "pdf"];

    // ───────────────────────── 格式对能力表 ─────────────────────────

    /// <summary>
    /// 全部支持的 "源格式&gt;目标格式" 组合（照搬 Filee 源项目 BuildEdges 生成的边，去掉 HWP 相关项）。
    /// 键传值示例："doc&gt;pdf"、"vsd&gt;png"、"pdf&gt;svg"
    /// </summary>
    private static readonly HashSet<string> Pairs = BuildPairs();

    /// <summary>
    /// 全部可读的输入格式（扩展名，不含点、全小写）。传值示例（单个元素）："doc"、"et"、"key"、"vsd"
    /// </summary>
    public static IReadOnlyList<string> SupportedInputFormats { get; } =
        Writer.Concat(Calc).Concat(Impress).Concat(WriterImport).Concat(CalcImport)
              .Concat(ImpressImport).Concat(ImpressGraphics).Concat(Draw)
              .Concat(new[] { "svg", "pdf", "txt", "html" })      // 这四个也作为输入出现（见 BuildPairs）
              .Distinct().OrderBy(f => f).ToList();

    /// <summary>
    /// 全部可写的目标格式（扩展名，不含点、全小写）。注意：不含 "ofd"——LibreOffice 无法导出 OFD。
    /// 传值示例（单个元素）："pdf"、"docx"、"xlsx"、"png"
    /// </summary>
    public static IReadOnlyList<string> SupportedTargetFormats { get; } =
        new[] { "pdf", "txt", "html" }.Concat(Writer).Concat(Calc).Concat(Impress).Concat(DrawTargets)
                              .Distinct().OrderBy(f => f).ToList();

    /// <summary>
    /// 判断一种输入格式能否转换为一种目标格式。
    /// </summary>
    /// <param name="fromFormat">源格式扩展名（不含点、大小写不限）。传值示例："docx"；也可以传 "DOCX" 或 ".docx"（会自动规整）</param>
    /// <param name="toFormat">目标格式扩展名（不含点、大小写不限）。传值示例："pdf"；传 "ofd" 一律返回 false（LibreOffice 不支持）</param>
    /// <returns>支持该转换返回 true，否则 false。</returns>
    /// <remarks>
    /// 调用示例：
    /// <code>
    ///   bool ok = LibreOfficeFormats.CanConvert("ppt", "pdf");   // true
    ///   bool no = LibreOfficeFormats.CanConvert("docx", "ofd");  // false
    /// </code>
    /// </remarks>
    public static bool CanConvert(string fromFormat, string toFormat) =>
        Pairs.Contains(Normalize(fromFormat) + ">" + Normalize(toFormat));

    /// <summary>
    /// 规整格式字符串：去首尾空白、去前导点、转小写。
    /// </summary>
    /// <param name="format">原始格式字符串。传值示例：".PDF" → 返回 "pdf"；"docx" → 原样返回</param>
    /// <returns>规整后的小写扩展名（不含点）。传值示例：入参 " XLSX " → 返回 "xlsx"</returns>
    private static string Normalize(string format) => format.Trim().TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// 构建格式对能力表（对应 Filee 源项目 BuildEdges 的边清单，去掉 HWP/HWPX 与图片描摹相关的标志）。
    /// </summary>
    /// <returns>形如 "源&gt;目标" 的键集合。传值示例（单个元素）："doc&gt;pdf"</returns>
    private static HashSet<string> BuildPairs()
    {
        // 结果集合：键传值示例 "docx>pdf"
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        // 局部函数：把一批"源 × 目标"组合加入集合（源等于目标的组合跳过，与 Filee 一致）
        void Add(IEnumerable<string> sources, IEnumerable<string> targets)
        {
            // 遍历到的当前源格式，传值示例："docx"
            foreach (var source in sources)
                // 遍历到的当前目标格式，传值示例："pdf"
                foreach (var target in targets)
                    if (source != target)
                        pairs.Add(source + ">" + target);
        }

        // 文字处理互转 + 纯文本/网页/PDF
        Add(Writer, [.. Writer, "txt", "html", "pdf"]);
        // 表格互转 + 网页/PDF
        Add(Calc, [.. Calc, "html", "pdf"]);
        // 演示互转 + PDF
        Add(Impress, [.. Impress, "pdf"]);
        // 纯文本与网页 → 常见可编辑格式与 PDF
        Add(["txt", "html"], ["docx", "odt", "pdf"]);
        // 只读老格式 → 对应家族的可编辑格式与 PDF/网页/纯文本
        Add(WriterImport, [.. Writer, "pdf", "txt", "html"]);
        Add(CalcImport, [.. Calc, "pdf", "html"]);
        Add(ImpressImport, [.. Impress, "pdf"]);
        // CGM 与绘图类 → 绘图目标（pdf/odg/svg/png/emf/wmf）
        Add([.. ImpressGraphics, .. Draw], DrawTargets);
        // SVG 只 → ODG/EMF/WMF（SVG → PDF 走更合适的矢量引擎，LibreOffice 路线不收）
        Add(["svg"], ["odg", "emf", "wmf"]);
        // PDF 只 → SVG/ODG（抽取矢量）
        Add(["pdf"], ["svg", "odg"]);
        return pairs;
    }

    /// <summary>
    /// 判定一种输入格式属于 LibreOffice 的哪个应用家族（filter 名的前缀取决于它）：
    /// writer（文字）/ calc（表格）/ impress（演示）/ draw（绘图）。
    /// 移植自 Filee 源项目的 FamilyOf。
    /// </summary>
    /// <param name="from">源格式扩展名（小写、不含点）。传值示例："docx"、"et"、"sda"</param>
    /// <param name="extension">
    /// 输入文件的实际扩展名（用于 "sda" 的歧义消解：.sda 是 StarDraw 绘图、.sdd 是 StarImpress 演示）。
    /// 传值示例：传 "sda" 表示按绘图处理；传 "sdd" 或 null 表示按演示处理
    /// </param>
    /// <returns>家族名小写字符串。传值示例：入参 from="docx" → 返回 "writer"</returns>
    internal static string FamilyOf(string from, string? extension = null)
    {
        // "sda" 的歧义消解：文件确实是 .sda → 绘图；否则（.sdd 等）→ 演示
        if (from == "sda")
            return string.Equals(extension, "sda", StringComparison.OrdinalIgnoreCase) ? "draw" : "impress";
        // 表格家族：可写表格格式或只读表格格式
        if (Calc.Contains(from) || CalcImport.Contains(from))
            return "calc";
        // 演示家族：可写演示格式、只读演示格式或 CGM
        if (Impress.Contains(from) || ImpressImport.Contains(from) || ImpressGraphics.Contains(from))
            return "impress";
        // 绘图家族：绘图格式，以及限定用途的 svg/pdf；其余全部归入文字处理家族（txt/html 也走 writer）
        return Draw.Contains(from) || DrawOnlyFor.Contains(from) ? "draw" : "writer";
    }
}
