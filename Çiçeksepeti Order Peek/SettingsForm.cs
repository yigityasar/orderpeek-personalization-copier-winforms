using System.Windows.Forms;

namespace Çiçeksepeti_Order_Peek
{
    public sealed class SettingsForm : Form
    {
        public SettingsForm(AppSettings s)
        {
            Text = "Ayarlar";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            Width = 460;
            Height = 280;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblKey = new Label { Left = 12, Top = 10, Text = "x-api-key", AutoSize = true };
            var txtKey = new TextBox { Left = 12, Top = 30, Width = 420, Text = s.ApiKey };

            var chkSandbox = new CheckBox
            {
                Left = 12,
                Top = 65,
                Width = 420,
                Text = "Sandbox kullan (test)",
                Checked = s.UseSandbox
            };

            // Prefetch aralığı
            var lblPast = new Label { Left = 12, Top = 100, Width = 180, Text = "Kaç gün geriye?" };
            var numPast = new NumericUpDown
            {
                Left = 200,
                Top = 96,
                Width = 80,
                Minimum = 0,
                Maximum = 30,
                Value = Math.Max(0, Math.Min(30, s.PrefetchPastDays))
            };

            var lblFuture = new Label { Left = 12, Top = 132, Width = 180, Text = "Kaç gün ileriye?" };
            var numFuture = new NumericUpDown
            {
                Left = 200,
                Top = 128,
                Width = 80,
                Minimum = 0,
                Maximum = 30,
                Value = Math.Max(0, Math.Min(30, s.PrefetchFutureDays))
            };

            var hint = new Label
            {
                Left = 12,
                Top = 160,
                Width = 420,
                Text = "Örn: 3 gün önce + 2 gün sonra = [-3, +2] aralığı",
                AutoSize = false
            };

            var btnOk = new Button
            {
                Text = "Kaydet",
                Left = 272,
                Width = 160,
                Top = 195,
                DialogResult = DialogResult.OK
            };

            btnOk.Click += (_, __) =>
            {
                s.ApiKey = txtKey.Text.Trim();
                s.UseSandbox = chkSandbox.Checked;
                s.PrefetchPastDays = (int)numPast.Value;
                s.PrefetchFutureDays = (int)numFuture.Value;
            };

            Controls.Add(lblKey);
            Controls.Add(txtKey);
            Controls.Add(chkSandbox);

            Controls.Add(lblPast);
            Controls.Add(numPast);
            Controls.Add(lblFuture);
            Controls.Add(numFuture);
            Controls.Add(hint);

            Controls.Add(btnOk);

            AcceptButton = btnOk;
        }
    }
}
