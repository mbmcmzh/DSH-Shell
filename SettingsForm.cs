using System;
using System.Drawing;
using System.Windows.Forms;

namespace DSHShell
{
    /// <summary>
    /// 设置对话框。目前只管一件事：后台 dsh web 跑在 Windows 还是 WSL 里。
    /// 这里只负责编出一份新设置，保存与“要重启才生效”的提示交给调用方。
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private const string DefaultDistroItem = "默认发行版";

        private readonly bool _dark;
        private readonly ComboBox _mode = new ComboBox();
        private readonly ComboBox _distro = new ComboBox();
        private readonly Label _distroLabel = new Label();
        private readonly Label _hint = new Label();
        private readonly Label _command = new Label();
        private readonly ToolTip _tips = new ToolTip();
        private readonly bool _hasDistros;
        private bool _loading = true;

        /// <summary>编辑后的设置；对话框返回 OK 时才需要理会。</summary>
        public AppSettings Result { get; }

        public SettingsForm(AppSettings current, bool dark)
        {
            _dark = dark;
            Result = (current ?? new AppSettings()).Clone();

            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Theme.Bg(dark);
            ForeColor = Theme.Label(dark);
            // 高度交给内容自己撑：文案换行数会随启动方式和系统字号变，写死就会互相压住
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // 这些尺寸都按 96 DPI 写，再换算到当前 DPI；AutoScaleMode.Font 只管字体不管硬编码像素
            var contentWidth = LogicalToDeviceUnits(468);
            var hintHeight = LogicalToDeviceUnits(48);       // 预留三行，切换启动方式时对话框不跳动

            var modeLabel = SectionLabel("后端启动方式");
            StyleCombo(_mode, contentWidth);
            _mode.Items.Add("Windows（PowerShell / cmd）");
            _mode.Items.Add("WSL（适用于 Linux 的 Windows 子系统）");
            _mode.SelectedIndex = Result.LaunchMode == LaunchMode.Wsl ? 1 : 0;
            _mode.SelectedIndexChanged += (sender, e) => OnChoiceChanged();

            _hint.AutoSize = true;
            _hint.MaximumSize = new Size(contentWidth, 0);
            _hint.MinimumSize = new Size(contentWidth, hintHeight);
            _hint.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(14));
            _hint.ForeColor = Theme.LabelTertiary(dark);

            _distroLabel.Text = "WSL 发行版";
            _distroLabel.AutoSize = true;
            _distroLabel.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(6));
            _distroLabel.Font = new Font(Font, FontStyle.Bold);

            StyleCombo(_distro, contentWidth);
            _distro.Items.Add(DefaultDistroItem);
            foreach (var name in ServerManager.ListWslDistros()) _distro.Items.Add(name);
            _hasDistros = _distro.Items.Count > 1;
            _distro.SelectedIndex = IndexOfSavedDistro();
            _distro.SelectedIndexChanged += (sender, e) => OnChoiceChanged();

            _command.AutoSize = true;
            _command.MaximumSize = new Size(contentWidth, 0);
            _command.MinimumSize = new Size(contentWidth, LogicalToDeviceUnits(42));
            _command.Margin = new Padding(0, LogicalToDeviceUnits(6), 0, LogicalToDeviceUnits(14));
            _command.ForeColor = Theme.LabelTertiary(dark);
            _command.Font = new Font("Segoe UI", 8.5f);

            var ok = MakeButton("确定", primary: true);
            var cancel = MakeButton("取消", primary: false);
            ok.Click += (sender, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (sender, e) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok;
            CancelButton = cancel;

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            buttons.Controls.Add(cancel);   // 右起：取消、确定（Windows 惯例是确定在左）
            buttons.Controls.Add(ok);

            var root = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(LogicalToDeviceUnits(24), LogicalToDeviceUnits(20),
                                      LogicalToDeviceUnits(24), LogicalToDeviceUnits(16)),
                BackColor = Color.Transparent
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (var i = 0; i < root.RowCount; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(modeLabel, 0, 0);
            root.Controls.Add(_mode, 0, 1);
            root.Controls.Add(_hint, 0, 2);
            root.Controls.Add(_distroLabel, 0, 3);
            root.Controls.Add(_distro, 0, 4);
            root.Controls.Add(_command, 0, 5);
            root.Controls.Add(buttons, 0, 6);
            Controls.Add(root);

            // 焦点落在下拉框上会被系统涂成一整条高亮，交给确定按钮更干净
            ActiveControl = ok;

            _loading = false;
            UpdatePreview();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(Handle, _dark);
        }

        // ---------- 交互 ----------

        private void OnChoiceChanged()
        {
            if (_loading) return;
            Result.LaunchMode = _mode.SelectedIndex == 1 ? LaunchMode.Wsl : LaunchMode.Windows;
            Result.WslDistro = _distro.SelectedIndex <= 0 ? string.Empty : Convert.ToString(_distro.SelectedItem);
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            var wsl = Result.LaunchMode == LaunchMode.Wsl;
            _distroLabel.Enabled = wsl;
            _distro.Enabled = wsl;

            _hint.Text = !wsl
                ? "在 Windows 上直接运行 dsh web，用 PATH 里的 dsh（npm install -g @deepseek-ai/dsh）。"
                : _hasDistros
                    ? "在 WSL 里用登录 shell 启动 dsh web，服务经 WSL 的 localhost 转发回到本机。\n" +
                      "需要在该发行版里装好 Node 与 dsh，工作目录为 Linux 侧的用户主目录。"
                    : "没有检测到可用的 WSL 发行版，请先安装（在管理员 PowerShell 里执行 wsl --install）。";

            var command = ServerManager.DescribeCommand(Result);
            _command.Text = "启动命令：" + command;
            _tips.SetToolTip(_command, command);
        }

        private int IndexOfSavedDistro()
        {
            var saved = (Result.WslDistro ?? string.Empty).Trim();
            if (saved.Length == 0) return 0;

            for (var i = 1; i < _distro.Items.Count; i++)
                if (string.Equals(Convert.ToString(_distro.Items[i]), saved, StringComparison.OrdinalIgnoreCase))
                    return i;

            // 记着的发行版现在列不出来（卸了、或 WSL 没起来）：补一条占位，别把用户选过的名字悄悄丢掉
            return _distro.Items.Add(saved);
        }

        // ---------- 控件外观 ----------

        private Label SectionLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(6)),
            Font = new Font(Font, FontStyle.Bold)
        };

        private void StyleCombo(ComboBox combo, int width)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.MinimumSize = new Size(width, 0);
            combo.Width = width;
            combo.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(10));
            combo.BackColor = Theme.Field(_dark);
            combo.ForeColor = Theme.Label(_dark);
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                Size = new Size(LogicalToDeviceUnits(96), LogicalToDeviceUnits(32)),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(LogicalToDeviceUnits(8), 0, 0, 0),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            if (primary)
            {
                button.BackColor = Theme.Accent;
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
                button.FlatAppearance.MouseDownBackColor = Theme.AccentHover;
            }
            else
            {
                button.BackColor = Theme.Field(_dark);
                button.ForeColor = Theme.Label(_dark);
                button.FlatAppearance.BorderColor = Theme.Border(_dark);
            }
            return button;
        }
    }
}
