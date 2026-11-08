using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RazerMini.Ui;

/// <summary>诊断报告窗口：可复制、可另存，方便把内容发回来看。</summary>
internal sealed class DiagnosticsForm : Form
{
    private readonly string _report;
    private readonly string _logPath;
    private readonly TextBox _textBox;

    public DiagnosticsForm(string report, string logPath)
    {
        _report = report;
        _logPath = logPath;

        Text = "RazerMini 诊断信息";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(520, 360);
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = true;

        _textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            Text = Normalize(report),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 8, 8, 8),
        };

        var copy = new Button { Text = "复制到剪贴板", Width = 120, Height = 28 };
        var save = new Button { Text = "另存为…", Width = 90, Height = 28 };
        var openLog = new Button { Text = "打开日志", Width = 90, Height = 28 };
        var close = new Button { Text = "关闭", Width = 80, Height = 28, DialogResult = DialogResult.OK };

        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(Normalize(_report));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"复制失败：{ex.Message}", "RazerMini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        save.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                FileName = $"razer-mini-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "文本文件|*.txt|所有文件|*.*",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(dialog.FileName, _report);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"保存失败：{ex.Message}", "RazerMini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        openLog.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"打开日志失败：{ex.Message}", "RazerMini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        close.Click += (_, _) => Close();

        buttons.Controls.AddRange(new Control[] { copy, save, openLog, close });
        Controls.Add(_textBox);
        Controls.Add(buttons);
        AcceptButton = close;
    }

    /// <summary>统一成 CRLF（报告用 AppendLine 生成，已在 Windows 上跑过一遍就带 \r\n，直接再补会变 \r\r\n）。</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
