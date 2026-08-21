using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSHShell
{
    /// <summary>后台 dsh web 的启动方式。</summary>
    public enum LaunchMode
    {
        /// <summary>在 Windows 上直接跑（PowerShell / cmd 那套 PATH）。</summary>
        Windows,

        /// <summary>在 WSL 发行版里跑（Linux 侧的 node 与 dsh）。</summary>
        Wsl
    }

    /// <summary>
    /// 用户设置，存 %LOCALAPPDATA%\DeepSeekHarness\settings.json。
    /// 只有“下次启动才生效”的开关放这里；窗口位置那种运行期状态仍走各自的文件。
    /// </summary>
    public sealed class AppSettings
    {
        private static string FilePath => Path.Combine(ServerManager.DataDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }   // 存成 "Wsl" 而不是 1，便于手改
        };

        /// <summary>后台服务的启动方式。</summary>
        public LaunchMode LaunchMode { get; set; } = LaunchMode.Windows;

        /// <summary>WSL 发行版名；空串表示用 WSL 的默认发行版。</summary>
        public string WslDistro { get; set; } = string.Empty;

        /// <summary>读取设置；文件不存在或损坏时一律回落到默认值（绝不因为设置文件让程序起不来）。</summary>
        public static AppSettings Load()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                       ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public bool Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public AppSettings Clone() => new AppSettings { LaunchMode = LaunchMode, WslDistro = WslDistro };

        /// <summary>两份设置是否等价（决定改完要不要提示重启）。</summary>
        public bool SameAs(AppSettings other) =>
            other != null &&
            other.LaunchMode == LaunchMode &&
            string.Equals(other.WslDistro ?? string.Empty, WslDistro ?? string.Empty, StringComparison.Ordinal);
    }
}
