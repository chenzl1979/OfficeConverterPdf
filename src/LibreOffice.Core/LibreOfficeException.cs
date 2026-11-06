// 转换失败时抛出的业务异常：携带 soffice 的退出码与错误输出，便于调用方定位原因。

namespace LibreOffice.Core;

/// <summary>
/// LibreOffice 转换失败时抛出的异常。
/// 典型场景：soffice 正常退出（退出码 0）但没有产出文件、或产出的文件为空。
/// 进程超时抛 <see cref="TimeoutException"/>，被用户取消抛 <see cref="OperationCanceledException"/>，与本异常区分。
/// </summary>
/// <param name="message">人可读的中文错误描述。传值示例（构造时）："LibreOffice 未产出任何文件（退出码 1）。Error: source file could not be loaded"</param>
/// <param name="exitCode">soffice 的进程退出码；未知时传 null。传值示例（构造时）：1</param>
/// <param name="standardError">soffice 标准错误流的文本；没有时传 null。传值示例（构造时）："Error: source file could not be loaded"</param>
/// <remarks>
/// 调用示例（捕获本库抛出的异常）：
/// <code>
///   try
///   {
///       using var converter = new DocumentConverter();
///       await converter.ConvertAsync(@"C:\Docs\损坏.docx", "pdf");
///   }
///   catch (LibreOfficeException ex)
///   {
///       Console.WriteLine($"转换失败：{ex.Message}，退出码：{ex.ExitCode?.ToString() ?? "未知"}");
///   }
/// </code>
/// </remarks>
public sealed class LibreOfficeException(string message, int? exitCode = null, string? standardError = null)
    : Exception(message)
{
    /// <summary>soffice 的进程退出码；拿不到时为 null。传值示例：1</summary>
    public int? ExitCode { get; } = exitCode;

    /// <summary>
    /// soffice 标准错误流的文本（常含具体失败原因）；没有时为 null。
    /// 传值示例："Error: source file could not be loaded"
    /// </summary>
    public string? StandardError { get; } = standardError;
}
