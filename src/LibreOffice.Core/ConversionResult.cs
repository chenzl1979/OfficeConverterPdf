// 一次成功转换的结果。

namespace LibreOffice.Core;

/// <summary>
/// 一次成功转换的结果记录：输出文件路径、所用 soffice 与 filter、耗时、退出码与输出文本。
/// </summary>
/// <param name="OutputPath">转换得到的输出文件的完整路径。传值示例（构造时）：@"C:\Docs\报告.pdf"</param>
/// <param name="SofficePath">本次实际使用的 soffice 可执行文件路径。传值示例（构造时）：@"C:\Program Files\LibreOffice\program\soffice.exe"</param>
/// <param name="Filter">
/// 传给 soffice --convert-to 的 filter 规格，可用于排查转换质量问题时手动复现。
/// 传值示例（构造时）："pdf:writer_pdf_Export"
/// </param>
/// <param name="Elapsed">本次转换（不含排队等待）的耗时。传值示例（构造时）：TimeSpan.FromSeconds(2.4)</param>
/// <param name="ExitCode">soffice 进程退出码（成功转换时通常为 0）。传值示例（构造时）：0</param>
/// <param name="StandardOutput">soffice 标准输出流的全部文本。传值示例（构造时）："convert ... using writer_pdf_Export\n"</param>
/// <param name="StandardError">soffice 标准错误流的全部文本（成功时可能为空串）。传值示例（构造时）：""</param>
/// <remarks>
/// 调用示例（接收并使用转换结果）：
/// <code>
///   using var converter = new DocumentConverter();
///   ConversionResult result = await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
///   Console.WriteLine($"已生成 {result.OutputPath}，耗时 {result.Elapsed.TotalSeconds:0.0} 秒");
/// </code>
/// </remarks>
public sealed record ConversionResult(
    string OutputPath,
    string SofficePath,
    string Filter,
    TimeSpan Elapsed,
    int ExitCode,
    string StandardOutput,
    string StandardError);
