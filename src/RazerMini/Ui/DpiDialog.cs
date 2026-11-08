using System.Drawing;
using System.Windows.Forms;

namespace RazerMini.Ui;

/// <summary>自定义 DPI 输入框。上限按设备型号给（Viper Ultimate 是 20000）。</summary>
internal sealed class DpiDialog : Form
{
    private readonly NumericUpDown _input;

    public DpiDialog(int maxDpi, int initial)
    {
        Text = "自定义 DPI";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(300, 140);
        Font = new Font("Microsoft YaHei UI", 9F);

        var label = new Label
        {
            Text = $"DPI（100 - {maxDpi}，X/Y 同步设置）：",
            Location = new Point(16, 20),
            AutoSize = true,
        };

        _input = new NumericUpDown
        {
            Location = new Point(16, 48),
            Width = 180,
            Minimum = 100,
            Maximum = maxDpi,
            Increment = 100,
            Value = Math.Clamp(initial, 100, maxDpi),
        };

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(110, 96), Width = 76 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(196, 96), Width = 76 };

        Controls.AddRange(new Control[] { label, _input, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public int SelectedDpi => (int)_input.Value;
}
