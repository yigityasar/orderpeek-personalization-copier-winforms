using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Çiçeksepeti_Order_Peek
{
    public sealed class SettingsForm : Form
    {
        private readonly AppSettings _settings;

        private readonly ListBox lstStores = new();
        private readonly TextBox txtName = new();
        private readonly TextBox txtKey = new();
        private readonly CheckBox chkSandbox = new();

        private readonly NumericUpDown numPast = new();
        private readonly NumericUpDown numFuture = new();

        private int _currentIndex = -1;
        private bool _updating = false;

        public SettingsForm(AppSettings s)
        {
            _settings = s;

            Text = "Ayarlar";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            // ClientSize kullan, başlık yüksekliği karışmasın
            ClientSize = new System.Drawing.Size(520, 320);
            MaximizeBox = false;
            MinimizeBox = false;

            if (_settings.Stores == null)
                _settings.Stores = new List<StoreConfig>();

            // Eski tekli ayarı otomatik mağazaya çevir
            if (_settings.Stores.Count == 0 && !string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                _settings.Stores.Add(new StoreConfig
                {
                    Name = "Varsayılan Mağaza",
                    ApiKey = _settings.ApiKey,
                    UseSandbox = _settings.UseSandbox
                });
            }

            if (_settings.Stores.Count == 0)
                _settings.Stores.Add(new StoreConfig { Name = "Mağaza 1" });

            // === Sol panel: mağaza listesi ===
            var left = new Panel
            {
                Left = 10,
                Top = 10,
                Width = 180,
                Height = 230
            };

            var lblStores = new Label
            {
                Left = 0,
                Top = 0,
                Width = 160,
                Text = "Mağazalar",
                AutoSize = false
            };

            lstStores.Left = 0;
            lstStores.Top = 20;
            lstStores.Width = 180;
            lstStores.Height = 170;

            var btnAdd = new Button
            {
                Left = 0,
                Top = 195,
                Width = 85,
                Text = "Ekle"
            };

            var btnRemove = new Button
            {
                Left = 95,
                Top = 195,
                Width = 85,
                Text = "Sil"
            };

            left.Controls.Add(lblStores);
            left.Controls.Add(lstStores);
            left.Controls.Add(btnAdd);
            left.Controls.Add(btnRemove);

            // === Sağ panel: seçili mağaza detayları ===
            var right = new Panel
            {
                Left = 200,
                Top = 10,
                Width = 300,
                Height = 230
            };

            var lblName = new Label { Left = 0, Top = 0, Text = "Mağaza Adı", AutoSize = true };
            txtName.Left = 0;
            txtName.Top = 18;
            txtName.Width = 260;

            var lblKey = new Label { Left = 0, Top = 50, Text = "x-api-key", AutoSize = true };
            txtKey.Left = 0;
            txtKey.Top = 68;
            txtKey.Width = 260;

            chkSandbox.Left = 0;
            chkSandbox.Top = 100;
            chkSandbox.Width = 260;
            chkSandbox.Text = "Sandbox kullan (test)";

            right.Controls.Add(lblName);
            right.Controls.Add(txtName);
            right.Controls.Add(lblKey);
            right.Controls.Add(txtKey);
            right.Controls.Add(chkSandbox);

            // === Alt panel: prefetch + Kaydet ===
            var bottom = new Panel
            {
                Left = 10,
                Top = 245,
                Width = 500,
                Height = 70,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            var lblPast = new Label
            {
                Left = 0,
                Top = 5,
                Width = 140,
                Text = "Kaç gün geriye?"
            };

            numPast.Left = 140;
            numPast.Top = 2;
            numPast.Width = 60;
            numPast.Minimum = 0;
            numPast.Maximum = 30;
            numPast.Value = Math.Max(0, Math.Min(30, _settings.PrefetchPastDays));

            var lblFuture = new Label
            {
                Left = 220,
                Top = 5,
                Width = 140,
                Text = "Kaç gün ileriye?"
            };

            numFuture.Left = 360;
            numFuture.Top = 2;
            numFuture.Width = 60;
            numFuture.Minimum = 0;
            numFuture.Maximum = 30;
            numFuture.Value = Math.Max(0, Math.Min(30, _settings.PrefetchFutureDays));

            var hint = new Label
            {
                Left = 0,
                Top = 30,
                Width = 260,
                AutoSize = false,
                Text = "Örn: 3 gün önce + 2 gün sonra = [-3, +2]"
            };

            var btnOk = new Button
            {
                Text = "Kaydet",
                Left = 360,
                Width = 120,
                Top = 30,
                DialogResult = DialogResult.OK
            };

            btnOk.Click += (_, __) =>
            {
                SaveCurrentStoreFromFields();
                _settings.PrefetchPastDays = (int)numPast.Value;
                _settings.PrefetchFutureDays = (int)numFuture.Value;
            };

            bottom.Controls.Add(lblPast);
            bottom.Controls.Add(numPast);
            bottom.Controls.Add(lblFuture);
            bottom.Controls.Add(numFuture);
            bottom.Controls.Add(hint);
            bottom.Controls.Add(btnOk);

            // === Form Controls ===
            Controls.Add(left);
            Controls.Add(right);
            Controls.Add(bottom);

            AcceptButton = btnOk;

            // === Eventler ===
            lstStores.SelectedIndexChanged += (_, __) => LoadSelectedStoreToFields();

            btnAdd.Click += (_, __) =>
            {
                SaveCurrentStoreFromFields();
                var st = new StoreConfig
                {
                    Name = $"Mağaza {_settings.Stores.Count + 1}",
                    UseSandbox = true
                };
                _settings.Stores.Add(st);
                RefreshStoreList();
                lstStores.SelectedIndex = _settings.Stores.Count - 1;
            };

            btnRemove.Click += (_, __) =>
            {
                if (_currentIndex < 0 || _currentIndex >= _settings.Stores.Count)
                    return;

                _settings.Stores.RemoveAt(_currentIndex);

                if (_settings.Stores.Count == 0)
                    _settings.Stores.Add(new StoreConfig { Name = "Mağaza 1" });

                RefreshStoreList();
                lstStores.SelectedIndex = Math.Min(_currentIndex, _settings.Stores.Count - 1);
            };

            RefreshStoreList();
            if (_settings.Stores.Count > 0)
                lstStores.SelectedIndex = 0;
        }

        private void RefreshStoreList()
        {
            _updating = true;
            lstStores.Items.Clear();

            foreach (var st in _settings.Stores)
                lstStores.Items.Add(st);

            _updating = false;
        }

        private void LoadSelectedStoreToFields()
        {
            if (_updating) return;

            _currentIndex = lstStores.SelectedIndex;

            if (_currentIndex < 0 || _currentIndex >= _settings.Stores.Count)
            {
                _updating = true;
                txtName.Text = "";
                txtKey.Text = "";
                chkSandbox.Checked = true;
                _updating = false;
                return;
            }

            var st = _settings.Stores[_currentIndex];

            _updating = true;
            txtName.Text = st.Name;
            txtKey.Text = st.ApiKey;
            chkSandbox.Checked = st.UseSandbox;
            _updating = false;
        }

        private void SaveCurrentStoreFromFields()
        {
            if (_currentIndex < 0 || _currentIndex >= _settings.Stores.Count)
                return;

            var st = _settings.Stores[_currentIndex];

            st.Name = string.IsNullOrWhiteSpace(txtName.Text)
                ? $"Mağaza {_currentIndex + 1}"
                : txtName.Text.Trim();

            st.ApiKey = txtKey.Text.Trim();
            st.UseSandbox = chkSandbox.Checked;

            lstStores.Items[_currentIndex] = st;
        }
    }
}
