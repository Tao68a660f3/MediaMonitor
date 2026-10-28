using System;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MediaMonitor.Core
{
    /// <summary>
    /// 全局应用信息（程序名 / 版本号）。
    ///
    /// **版本号的唯一来源是 `MediaMonitor.csproj` 的 `&lt;Version&gt;`**：
    /// 窗口标题、托盘提示、exe「属性 → 详细信息」都从这份程序集元数据来 ——
    /// 不要再把 `V3.x.x` 这类字面量写回 XAML / 代码里（自测 R15 会拦住）。
    ///
    /// 只依赖自身程序集的元数据、不引用任何 WPF 类型，因此
    /// `Tests/NewProtocolSelfTest` 可以直接链接本文件做断言
    /// （那时读到的是**自测程序集自己的**版本，所以 R15 只校验"格式与拼接"，不断言具体号）。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>程序名（窗口标题前缀）：与 csproj 的 &lt;AssemblyTitle&gt; / &lt;Product&gt; 保持一致</summary>
        public const string Name = "媒体同步调试终端";

        /// <summary>只取版本号开头的 1~4 段数字（剥掉 `+构建元数据` / `-预发布后缀`）</summary>
        private static readonly Regex VersionHead = new(@"^\d+(\.\d+){0,3}", RegexOptions.Compiled);

        private static string? _version;
        private static string? _buildInfo;

        /// <summary>
        /// 语义版本，三段：`3.x.x`。
        /// **用惰性求值而不是字段初始化器**（两个原因，都是实测踩出来的）：
        /// ① 静态字段按声明顺序初始化，<see cref="VersionHead"/> 排在后面时 `Version` 初始化会读到 null
        ///    → 类型初始化抛异常 → App 直接弹「初始化零件失败: The type initializer for ... threw an exception」；
        /// ② 元数据读不出来（被裁剪/改写）时只退化成 `0.0.0`，绝不该拖垮整个程序启动。
        /// </summary>
        public static string Version => _version ??= ResolveVersion();

        /// <summary>展示用版本（带 V 前缀）：`V3.x.x`</summary>
        public static string VersionText => "V" + Version;

        /// <summary>
        /// 窗口标题：`媒体同步调试终端 V3.x.x`。
        /// MainWindow 构造时把这个值赋给 `Title`，托盘提示（`$"... | {this.Title}"`）也就跟着走。
        /// </summary>
        public static string WindowTitle => $"{Name} {VersionText}";

        /// <summary>
        /// 完整构建标识：`3.x.x+1a2b3c4`（.NET SDK 在 git 仓库里会把提交号拼进 InformationalVersion）。
        /// 排障时用它确认"跑的到底是哪次构建"；拿不到 git 信息时等于 <see cref="Version"/>。
        /// </summary>
        public static string BuildInfo => _buildInfo ??= ResolveBuildInfo();

        // ---------------- 内部实现 ----------------

        private static string ResolveVersion()
        {
            try
            {
                string core = Strip(Informational() ?? FileVersion());
                return core.Length > 0 ? core : "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        private static string ResolveBuildInfo()
        {
            try
            {
                // 正常情况：InformationalVersion = `<Version>[+<git sha>]`；异常时退化成纯 Version
                string raw = (Informational() ?? string.Empty).Trim();
                return raw.StartsWith(Version, StringComparison.Ordinal) ? raw : Version;
            }
            catch
            {
                return Version;
            }
        }

        private static string Strip(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string head = raw!.Trim().Split('+')[0].Split('-')[0].Trim();
            Match m = VersionHead.Match(head);
            return m.Success ? m.Value : string.Empty;
        }

        private static string? Informational() =>
            typeof(AppInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        private static string? FileVersion() =>
            typeof(AppInfo).Assembly
                .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
    }
}
