// 目标格式 → LibreOffice "--convert-to" filter 规格的映射表。
// 忠实移植自 Filee 项目的 src/Filee.Engines/Office/LibreOfficeConverter.cs 的 FilterFor 方法。

namespace LibreOffice.Core;

/// <summary>
/// 把 "源格式 + 目标格式" 映射成 LibreOffice <c>--convert-to</c> 参数所需的 filter 规格。
/// filter 规格的完整形式为 "输出扩展名:过滤器名:过滤器选项"（后两段可省略）。
/// </summary>
/// <remarks>
/// 调用示例：
/// <code>
///   string filter = ConversionFilter.FilterFor("docx", "pdf", pdfA: false);
///   Console.WriteLine(filter);   // 输出：pdf:writer_pdf_Export
/// </code>
/// </remarks>
public static class ConversionFilter
{
    /// <summary>
    /// 返回目标格式对应的 LibreOffice filter 规格。
    /// </summary>
    /// <param name="from">源格式扩展名（小写、不含点），决定用哪个应用家族的导出过滤器。传值示例："docx"、"et"</param>
    /// <param name="to">目标格式扩展名（小写、不含点）。传值示例："pdf"；注意 "ofd" 不受支持，调用方应先用 LibreOfficeFormats.CanConvert 校验</param>
    /// <param name="pdfA">是否导出 PDF/A-2b 归档格式；仅当 to 为 "pdf" 时生效。传值示例：true（需要 LibreOffice 7.4 及以上）</param>
    /// <param name="extension">
    /// 输入文件的实际扩展名，仅用于 "sda" 的歧义消解（.sda 按绘图、.sdd 按演示）；通常传 null。
    /// 传值示例："sda"
    /// </param>
    /// <returns>filter 规格字符串。传值示例：from="docx"、to="pdf" → 返回 "pdf:writer_pdf_Export"</returns>
    internal static string FilterFor(string from, string to, bool pdfA, string? extension = null)
    {
        // 先确定源格式所属的应用家族（writer/calc/impress/draw），它决定 PDF 与图片导出过滤器的名字
        var family = LibreOfficeFormats.FamilyOf(from, extension);
        return to switch
        {
            // PDF/A-2b：用 JSON 过滤器选项语法（需要 LibreOffice 7.4 及以上）
            "pdf" when pdfA => $"pdf:{family}_pdf_Export:{{\"SelectPdfVersion\":{{\"type\":\"long\",\"value\":\"2\"}}}}",
            // 普通 PDF：家族名 + 通用导出过滤器，如 writer_pdf_Export
            "pdf" => $"pdf:{family}_pdf_Export",
            // ───────── 文字处理目标 ─────────
            "docx" => "docx:MS Word 2007 XML",
            "doc" => "doc:MS Word 97",
            "odt" => "odt",
            "fodt" => "fodt:OpenDocument Text Flat XML",     // 扁平 XML 的 ODT（单文件、非 ZIP 容器）
            "ott" => "ott:writer8_template",                 // ODT 文字模板
            "rtf" => "rtf:Rich Text Format",
            "txt" => "txt:Text (encoded):UTF8",              // 纯文本，显式指定 UTF-8 编码
            // 网页导出：表格与文字用各自的 HTML 过滤器
            "html" when family == "calc" => "html:HTML (StarCalc)",
            "html" => "html:HTML (StarWriter)",
            // ───────── 表格目标 ─────────
            "xlsx" => "xlsx:Calc MS Excel 2007 XML",
            "xls" => "xls:MS Excel 97",
            "xlt" => "xlt:MS Excel 97 Vorlage/Template",     // Excel 97 模板（过滤器名是德语原文）
            "ods" => "ods",
            "ots" => "ots:calc8_template",                   // ODS 表格模板
            // CSV：44=逗号分隔符、34=双引号文本限定符、76=UTF-8 编码
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76",
            // ───────── 演示目标 ─────────
            "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            "ppt" => "ppt:MS PowerPoint 97",
            "odp" => "odp",
            "otp" => "otp:impress8_template",                // ODP 演示模板
            // ───────── 绘图/图片目标 ─────────
            // 来自演示文件的 ODG 用演示的绘图过滤器，其余用绘图的
            "odg" when family == "impress" => "odg:impress8_draw",
            "odg" => "odg:draw8",
            // 图片与矢量导出：家族名 + 通用导出过滤器，如 draw_png_Export
            "svg" => $"svg:{family}_svg_Export",
            "png" => $"png:{family}_png_Export",
            "emf" => $"emf:{family}_emf_Export",
            "wmf" => $"wmf:{family}_wmf_Export",
            // 其它目标格式：直接把扩展名当过滤器名（LibreOffice 接受裸扩展名）
            _ => to,
        };
    }
}
