// 外部命令行程序的受控运行器：超时、取消、捕获输出、杀整棵进程树、低于正常优先级。
// 精简移植自 Filee 项目的 src/Filee.Engines/Infrastructure/ProcessRunner.cs（去掉了与 Filee 框架耦合的部分）。

using System.Diagnostics;
using System.Text;

namespace LibreOffice.Core;

/// <summary>
/// 启动外部命令行工具的安全辅助类：参数不经过 shell（无注入风险）、捕获两路标准流、
/// 超时或取消时杀死整棵进程树、并把进程降到"低于正常"优先级以免占满 CPU。
/// </summary>
/// <remarks>
/// 调用示例：
/// <code>
///   ProcessResult result = await ProcessRunner.RunAsync(
///       @"C:\Program Files\LibreOffice\program\soffice.exe",
///       new[] { "--version" },
///       TimeSpan.FromMinutes(1),
///       CancellationToken.None);
///   Console.WriteLine(result.StandardOutput);   // 例如 "LibreOffice 24.2 ..."
/// </code>
/// </remarks>
public static class ProcessRunner
{
    /// <summary>
    /// 启动一个外部程序并等待它退出。
    /// </summary>
    /// <param name="executable">可执行文件的完整路径。传值示例：@"C:\Program Files\LibreOffice\program\soffice.exe"</param>
    /// <param name="arguments">命令行参数序列（逐个传入，由运行时负责转义，不经 shell）。传值示例：new[] { "--headless", "--convert-to", "pdf", "C:\Docs\报告.docx" }</param>
    /// <param name="timeout">最长等待时间；超时后进程树被杀死并抛出 <see cref="TimeoutException"/>。传值示例：TimeSpan.FromMinutes(3)</param>
    /// <param name="cancellationToken">取消令牌；取消后进程树被杀死并抛出 <see cref="OperationCanceledException"/>。传值示例：CancellationToken.None</param>
    /// <param name="onOutput">
    /// 每收到一行标准输出或标准错误就回调一次（收到的是该行文本）；用于写日志或解析进度；不需要时传 null。
    /// 传值示例：line => Console.WriteLine(line)
    /// </param>
    /// <param name="environment">
    /// 附加到进程的系统环境变量（在继承的系统环境之上追加）；不需要时传 null。
    /// Linux 服务器（systemd 服务账户）常用它补 HOME 等变量。
    /// 传值示例：new Dictionary&lt;string, string&gt; { ["HOME"] = "/home/svc" }
    /// </param>
    /// <returns>进程结果（退出码与两路输出文本）。</returns>
    /// <exception cref="InvalidOperationException">程序无法启动（路径不存在、权限不足等）。</exception>
    /// <exception cref="TimeoutException">程序未在 <paramref name="timeout"/> 内退出（已被杀死）。</exception>
    /// <exception cref="OperationCanceledException">调用方通过令牌取消了等待（进程树已被杀死）。</exception>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onOutput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        // 进程启动信息：不经过 shell、不弹窗口、重定向并按 UTF-8 解码两路标准流
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // 工作目录默认设为程序所在目录（LibreOffice 对此不敏感，但这样更稳）
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        // 逐个添加参数：ArgumentList 会自动转义含空格/引号的路径，杜绝命令注入
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // 附加调用方指定的环境变量（覆盖同名系统变量；Linux 服务器场景常用来补 HOME）
        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        // 进程对象；using 确保方法结束时释放句柄
        using var process = new Process { StartInfo = startInfo };
        // 标准输出收集缓冲（加锁访问，因为回调在池线程上触发）
        var stdout = new StringBuilder();
        // 标准错误收集缓冲
        var stderr = new StringBuilder();
        // 收到输出行时：追加到对应缓冲（加锁），并转发给调用方回调
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };

        // 启动失败（可执行文件不存在等）直接抛出明确异常
        if (!process.Start())
            throw new InvalidOperationException($"无法启动程序：{executable}");
        // 降为"低于正常"优先级：批量转换时不抢占桌面/服务的 CPU（空闲机器上速度不受影响；
        // Linux 上 .NET 会把它映射为 nice 值，效果相同）
        LowerPriority(process);
        // 开始异步逐行读取两路输出（不读会导致缓冲区满后进程卡死）
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 关联令牌源：把调用方的取消与超时合并成同一个"该结束了"信号
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            // 等进程退出；超时或取消触发时 WaitForExitAsync 抛 OperationCanceledException
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            // 先杀进程树（LibreOffice 会派生子进程，必须整树杀）
            TryKill(process);
            // 是调用方主动取消 → 按取消向上抛；是超时 → 抛 TimeoutException
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"程序 {Path.GetFileName(executable)} 未在 {timeout.TotalSeconds:0} 秒内完成，已强制结束。");
        }

        // 再等一次确保异步输出读取器全部排空（否则尾部几行可能丢失）
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// 把进程优先级降为"低于正常"；进程已退出或系统不允许时静默忽略（不影响转换本身）。
    /// </summary>
    /// <param name="process">刚启动的进程对象。传值示例：new Process { ... }</param>
    private static void LowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已退出 / 无权限 / 平台不支持：保持默认优先级即可
        }
    }

    /// <summary>
    /// 杀死进程及其全部子进程（整棵树）；进程已经退出或无权限时静默忽略。
    /// </summary>
    /// <param name="process">要终止的进程对象。传值示例：new Process { ... }</param>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程恰好已经退出：无需处理
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 没有杀死进程的权限：能做的只有放弃
        }
    }
}
