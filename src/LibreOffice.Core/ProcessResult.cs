// 外部进程（soffice）的一次运行结果。
// 提取自 Filee 项目的 src/Filee.Engines/Infrastructure/ProcessRunner.cs（ProcessResult 记录）。

namespace LibreOffice.Core;

/// <summary>
/// 外部进程运行结束后的结果记录：退出码与两路标准流输出。
/// </summary>
/// <param name="ExitCode">
/// 进程退出码；0 表示正常结束。
/// 传值示例（构造时）：0；LibreOffice 加载失败时常见的值为 1。
/// </param>
/// <param name="StandardOutput">
/// 标准输出流（stdout）收集到的全部文本，按行拼接。
/// 传值示例（构造时）："convert C:\Docs\报告.docx -> C:\Temp\lo-1a2b\报告.pdf using filter : writer_pdf_Export\n"
/// </param>
/// <param name="StandardError">
/// 标准错误流（stderr）收集到的全部文本，按行拼接；LibreOffice 的报错信息通常在这里。
/// 传值示例（构造时）："Error: source file could not be loaded\n"
/// </param>
/// <remarks>
/// 调用示例（本记录一般由 <see cref="ProcessRunner.RunAsync"/> 返回，不手工构造）：
/// <code>
///   ProcessResult result = await ProcessRunner.RunAsync(
///       @"C:\Program Files\LibreOffice\program\soffice.exe",
///       new[] { "--version" },
///       TimeSpan.FromMinutes(1),
///       CancellationToken.None);
///   Console.WriteLine(result.ExitCode);
/// </code>
/// </remarks>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
