using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Çiçeksepeti_Order_Peek
{
    public static class Updater
    {
        private static readonly HttpClient _http = new HttpClient();

        // Her zaman en güncel setup'ı koyacağın URL
        private const string SetupUrl =
            "https://github.com/yigityasar/orderpeek-personalization-copier-winforms/releases/download/main/ciceksepetiorderpeeksetup.exe";

        public static async Task CheckAndUpdateAsync(IWin32Window owner)
        {
            try
            {
                var ask = MessageBox.Show(owner,
                    "Order Peek’in en güncel kurulum dosyasını indirip çalıştırmak istiyor musun?\n\n" +
                    "Kurulum başlayınca bu uygulamayı kapatabilirsin.",
                    "Güncelleme",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (ask != DialogResult.Yes)
                    return;

                // Geçici klasör
                var tempDir = Path.Combine(Path.GetTempPath(), "OrderPeekUpdate");
                Directory.CreateDirectory(tempDir);

                var setupPath = Path.Combine(tempDir, "OrderPeekSetup_latest.exe");

                // EXE'yi indir
                using (var res = await _http.GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();

                    using var input = await res.Content.ReadAsStreamAsync();
                    using var output = File.Create(setupPath);
                    await input.CopyToAsync(output);
                }

                // Kurulumu başlat
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    UseShellExecute = true
                });

                // İstersen burada app'i kapat
                // (kullanıcı kurulum sırasında açık kalmasını istemiyorsa iyi olur)
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner,
                    "Güncelleme indirilemedi:\n" + ex.Message,
                    "Güncelleme",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
