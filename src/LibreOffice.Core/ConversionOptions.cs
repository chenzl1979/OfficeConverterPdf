// 转换选项：超时、PDF/A、并发度、soffice 输出回调与 profile 目录。
// 对应 Filee 项目 LibreOfficeConverter 中的 Timeout 常量、Preset.Document.PdfA 选项与 worker profile 机制。

namespace LibreOffice.Core;

/// <summary>
/// 文档转换的可选配置。所有属性都有默认值，不改任何一项即可直接使用。
/// 本类实例按"构造时固定"设计（init 属性），创建后不可修改，可被多个转换共享。
/// </summary>
/// <remarks>
/// 调用示例（自定义超时与 PDF/A，并传给转换器）：
/// <code>
///   var options = new ConversionOptions
///   {
///       Timeout = TimeSpan.FromMinutes(5),   // 大文件适当放宽超时
///       PdfA = true,                         // 输出 PDF/A-2b 归档格式
///       OnOutput = line => Console.WriteLine(line),
///   };
///   using var converter = new DocumentConverter(options: options);
///   await converter.ConvertAsync(@"C:\Docs\报告.docx", "pdf");
/// </code>
/// </remarks>
public sealed class ConversionOptions
{
    /// <summary>
    /// 单次转换允许的最长时间；超时后 soffice 进程树会被强制结束并抛出 <see cref="TimeoutException"/>。
    /// 默认 3 分钟（与 Filee 源项目一致）。建议：大文档（几百页）可放宽到 5-10 分钟。
    /// 传值示例：TimeSpan.FromMinutes(3)
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 是否导出 PDF/A-2b 归档级 PDF（长期保存、符合档案标准的场景）。
    /// 仅当目标格式为 pdf 时生效；默认 false（普通 PDF）。
    /// 传值示例：true
    /// </summary>
    public bool PdfA { get; init; }

    /// <summary>
    /// 最多允许同时运行的 soffice 进程数（1-16）。每个进程使用独立的用户 profile 目录，
    /// 互不干扰——这是 Filee 源项目发现的要点：多个 soffice 共用一个 profile 会静默失败（退出码 1）。
    /// 默认 2。服务器批量转换可适当调大。
    /// 传值示例：4
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 2;

    /// <summary>
    /// LibreOffice 用户 profile（缓存、配置）的存放根目录；每个并发槽位在其下建 worker-0、worker-1… 子目录。
    /// 为 null 时使用系统临时目录下的 LibreOfficeConverter-profiles 文件夹。
    /// profile 会跨转换复用（首次转换会建缓存，较慢；之后变快），所以目录应保持可写。
    /// 传值示例：@"D:\LoData\profiles"
    /// </summary>
    public string? ProfileRoot { get; init; }

    /// <summary>
    /// 接收 soffice 每一行输出（标准输出与标准错误混在一起）的回调，可用于写日志；
    /// 不需要时保持 null。
    /// 传值示例：line => Console.WriteLine("[soffice] " + line)
    /// </summary>
    public Action<string>? OnOutput { get; init; }

    /// <summary>
    /// 启动 soffice 进程时附加的环境变量（在系统环境之上追加），null 表示不附加。
    /// Linux/国产系统（UOS、麒麟、openEuler 等）服务器场景常用：
    /// 以 systemd 服务账户运行时可能没有 HOME，LibreOffice 会启动失败，此时需显式指定。
    /// 传值示例：new Dictionary&lt;string, string&gt; { ["HOME"] = "/home/svc", ["SAL_USE_VCLPLUGIN"] = "svp" }
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}
