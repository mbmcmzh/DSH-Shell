using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DSHShell
{
    /// <summary>
    /// 壳里所有原生 UI（主窗口、启动画面、设置对话框）共用的 DSW 设计令牌与窗口外观开关，
    /// 取值与 dsh-web 的 design-platform.css 对齐，深浅两套一一对应。
    /// </summary>
    internal static class Theme
    {
        // --dsw-static-neutral-bluish-00 / -950
        public static readonly Color LightBg = Color.FromArgb(255, 255, 255);
        public static readonly Color DarkBg = Color.FromArgb(21, 21, 23);

        // --dsw-alias-label-primary / --dsw-alias-label-tertiary
        public static readonly Color LightLabel = Color.FromArgb(15, 17, 21);
        public static readonly Color DarkLabel = Color.FromArgb(235, 236, 238);
        public static readonly Color LightLabelTertiary = Color.FromArgb(129, 133, 140);
        public static readonly Color DarkLabelTertiary = Color.FromArgb(137, 141, 148);

        // 输入控件的面与描边
        public static readonly Color LightField = Color.FromArgb(252, 252, 253);
        public static readonly Color DarkField = Color.FromArgb(33, 33, 36);
        public static readonly Color LightBorder = Color.FromArgb(222, 224, 228);
        public static readonly Color DarkBorder = Color.FromArgb(58, 58, 63);

        /// <summary>DeepSeek 品牌蓝，只用在主按钮上。</summary>
        public static readonly Color Accent = Color.FromArgb(77, 107, 254);
        public static readonly Color AccentHover = Color.FromArgb(64, 92, 232);

        public static Color Bg(bool dark) => dark ? DarkBg : LightBg;
        public static Color Label(bool dark) => dark ? DarkLabel : LightLabel;
        public static Color LabelTertiary(bool dark) => dark ? DarkLabelTertiary : LightLabelTertiary;
        public static Color Field(bool dark) => dark ? DarkField : LightField;
        public static Color Border(bool dark) => dark ? DarkBorder : LightBorder;

        // ---- 窗口外观（DWM）----

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        /// <summary>让系统把窗口描边/标题栏按深色画（老系统不认这个属性，忽略即可）。</summary>
        public static void ApplyDarkTitleBar(IntPtr handle, bool dark) =>
            Set(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);

        /// <summary>Windows 11 的平滑圆角。</summary>
        public static void ApplyRoundedCorners(IntPtr handle) =>
            Set(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        private static void Set(IntPtr handle, int attribute, int value)
        {
            if (handle == IntPtr.Zero) return;
            try { DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)); }
            catch { }
        }
    }
}
