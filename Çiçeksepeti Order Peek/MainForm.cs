using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Çiçeksepeti_Order_Peek
{
    public partial class MainForm : Form
    {
        private readonly AppSettings _settings;
        private readonly HttpClient _http = new HttpClient();
        private CancellationTokenSource? _cts;

        // UI
        private readonly TextBox txtOrderItemNo = new();
        private readonly Button btnRefresh = new();
        private readonly Button btnSettings = new();
        private readonly Button btnCancel = new();
        private readonly ListView lv = new();
        private readonly Label lblStatus = new();
        private readonly RichTextBox txtLog = new();

        // Cache
        private readonly Dictionary<int, CachedOrderItem> _cacheByOrderItemId = new();
        private DateTime _lastPrefetchUtc = DateTime.MinValue;

        // Prefetch re-entry guard
        private readonly SemaphoreSlim _prefetchLock = new(1, 1);

        // GetOrders global throttle (farklı request bile olsa 5 sn)
        private static readonly SemaphoreSlim _getOrdersLock = new(1, 1);
        private static DateTime _lastGetOrdersUtc = DateTime.MinValue;
        private const int GetOrdersMinIntervalMs = 5100;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public MainForm()
        {
            _settings = SettingsStore.Load();

            BuildUi();
            ApplyAlwaysOnTopBottomRight();

            // Uygulama açılır açılmaz sipariş çekme yok.
            Shown += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                {
                    OpenSettings();
                    if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                        SetStatus("API Key yok. ⚙ ile gir.");
                }
                else
                {
                    SetStatus("Hazır. ↻ ile cache çek, sonra alt sipariş no gir.");
                }
            };
        }

        private void BuildUi()
        {
            Text = "Çiçeksepeti Order Peek";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            TopMost = true;
            Width = 300;
            Height = 400;

            // ÜST BAR
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(8, 8, 8, 6)
            };

            txtOrderItemNo.PlaceholderText = "Alt Sipariş No (orderItemNo) - Enter";
            txtOrderItemNo.Dock = DockStyle.Fill;
            txtOrderItemNo.Font = new Font(Font.FontFamily, 12f);
            txtOrderItemNo.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await LookupAndRenderAsync();
                }
            };

            btnCancel.Text = "✕";
            btnCancel.Dock = DockStyle.Right;
            btnCancel.Width = 34;
            btnCancel.Enabled = false;
            btnCancel.Click += (_, __) =>
            {
                _cts?.Cancel();
                LogWarn("İptal istendi.");
            };

            btnRefresh.Text = "↻";
            btnRefresh.Dock = DockStyle.Right;
            btnRefresh.Width = 42;
            btnRefresh.Click += async (_, __) => await PrefetchRangeAsync(force: true);

            btnSettings.Text = "⚙";
            btnSettings.Dock = DockStyle.Right;
            btnSettings.Width = 42;
            btnSettings.Click += (_, __) => OpenSettings();

            topPanel.Controls.Add(txtOrderItemNo);
            topPanel.Controls.Add(btnSettings);
            topPanel.Controls.Add(btnRefresh);
            topPanel.Controls.Add(btnCancel);

            // Liste
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.MultiSelect = false;
            lv.Font = new Font("Segoe UI", 8.5f);
            lv.Columns.Clear();
            lv.Columns.Add("Alan", 140);
            lv.Columns.Add("Müşteri", 140);

            // Tek tık: değeri kopyala (isim alanlarında formatlı)
            lv.ItemSelectionChanged += (_, __) =>
            {
                if (lv.SelectedItems.Count == 0) return;

                if (lv.SelectedItems[0].Tag is not RowTag tag) return;
                if (string.IsNullOrWhiteSpace(tag.Raw)) return;

                var toCopy = FormatForCopy(tag.Field, tag.Raw);
                Clipboard.SetText(toCopy);

                SetStatus("Değer kopyalandı ✅");
                LogOk($"Kopya: {Preview(toCopy)}");
            };

            // Log
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            txtLog.Dock = DockStyle.Bottom;
            txtLog.Height = 110;
            txtLog.Font = new Font("Consolas", 7.75f);
            txtLog.BackColor = SystemColors.Window;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.DetectUrls = false;

            lblStatus.Text = "Hazır.";
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Padding = new Padding(8, 6, 8, 6);

            Controls.Clear();
            Controls.Add(lv);
            Controls.Add(lblStatus);
            Controls.Add(txtLog);
            Controls.Add(topPanel);
        }

        private void ApplyAlwaysOnTopBottomRight()
        {
            StartPosition = FormStartPosition.Manual;
            PositionBottomRight();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, __) => PositionBottomRight();
        }

        private void PositionBottomRight()
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            int margin = 12;
            Location = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);
        }

        private void SetStatus(string msg)
        {
            lblStatus.Text = msg;
            LogInfo(msg);
        }

        // ========= RENKLİ LOG =========
        private void LogColored(string msg, Color color)
        {
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = color;
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            txtLog.SelectionColor = txtLog.ForeColor;
            txtLog.ScrollToCaret();
        }

        private void LogInfo(string msg) => LogColored(msg, Color.DimGray);
        private void LogOk(string msg) => LogColored(msg, Color.SeaGreen);
        private void LogWarn(string msg) => LogColored(msg, Color.DarkOrange);
        private void LogErr(string msg) => LogColored(msg, Color.Firebrick);

        private void LogWhatToDo(Exception ex)
        {
            var m = (ex.ToString() ?? "").ToLowerInvariant();

            if (m.Contains("limit aşımı"))
            {
                LogErr("Ne yapmalı: Rate limit. Uygulama otomatik bekleyip tekrar deniyor. Çok olursa ↻ spamleme.");
                return;
            }

            if (m.Contains("401") || m.Contains("403"))
                LogErr("Ne yapmalı: API Key yanlış/eksik. ⚙ → x-api-key’i kontrol et. Sandbox açık/kapalı doğru mu bak.");

            if (m.Contains("429"))
                LogErr("Ne yapmalı: Rate limit. 5 saniye bekle, sonra tekrar dene.");

            if (m.Contains("400"))
                LogErr("Ne yapmalı: İstek hatalı. orderItemNo sayı mı? Sandbox/prod doğru mu?");

            if (m.Contains("timeout") || m.Contains("timed out") || m.Contains("taskcanceled"))
                LogErr("Ne yapmalı: Zaman aşımı. İnternet yavaş veya API yoğun. Biraz sonra tekrar dene.");

            if (m.Contains("name resolution") || m.Contains("nameresolutionfailure") || m.Contains("nodename nor servname"))
                LogErr("Ne yapmalı: DNS/İnternet sorunu. VPN/Proxy varsa kapat, bağlantıyı kontrol et.");
        }

        // ============== CORE ==============

        private async Task LookupAndRenderAsync()
        {
            lv.Items.Clear();

            if (!int.TryParse(txtOrderItemNo.Text.Trim(), out var orderItemId))
            {
                SetStatus("Alt sipariş no sayı olmalı.");
                return;
            }

            // Cache hit
            if (_cacheByOrderItemId.TryGetValue(orderItemId, out var cached))
            {
                RenderPersonalizations(cached);
                CopyOnlyPersonalizationValues(cached);
                SetStatus("Cache’den bulundu ✅");
                LogOk($"Cache hit: {orderItemId}");
                return;
            }

            // Cache yoksa API (tek atımlık)
            SetStatus("Cache’de yok → API’den sorguluyorum…");
            LogWarn($"Cache miss: {orderItemId}");

            try
            {
                var resp = await ApiGetOrdersAsync(
                    new { pageSize = 100, page = 0, orderItemNo = orderItemId },
                    CancellationToken.None);

                var found = resp?.SupplierOrderListWithBranch?.FirstOrDefault(x => x.OrderItemId == orderItemId)
                           ?? resp?.SupplierOrderListWithBranch?.FirstOrDefault();

                if (found == null)
                {
                    SetStatus("Bulunamadı.");
                    LogWarn("API response boş/uyuşmadı.");
                    return;
                }

                var cacheItem = ToCached(found);
                _cacheByOrderItemId[cacheItem.OrderItemId] = cacheItem;

                RenderPersonalizations(cacheItem);
                CopyOnlyPersonalizationValues(cacheItem);
                SetStatus("API’den bulundu ✅");
                LogOk($"API ok: {orderItemId}");
            }
            catch (Exception ex)
            {
                SetStatus("API hata ❌");
                LogErr(ex.Message);
                LogWhatToDo(ex);
                LogErr(ex.ToString());
            }
        }

        private async Task PrefetchRangeAsync(bool force = false)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                SetStatus("API Key yok. ⚙ ile gir.");
                return;
            }

            if (!await _prefetchLock.WaitAsync(0))
            {
                LogWarn("Prefetch zaten çalışıyor.");
                return;
            }

            btnRefresh.Enabled = false;
            btnCancel.Enabled = true;
            txtOrderItemNo.Enabled = false;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                var pastDays = Math.Max(0, _settings.PrefetchPastDays);
                var futureDays = Math.Max(0, _settings.PrefetchFutureDays);

                var now = DateTime.UtcNow;
                var start = now.AddDays(-pastDays);
                var end = now.AddDays(+futureDays);

                SetStatus($"Siparişler çekiliyor… ({pastDays} gün geri, {futureDays} gün ileri)");
                LogInfo($"Prefetch range: {start:O} -> {end:O}");

                int page = 0;
                int before = _cacheByOrderItemId.Count;

                while (true)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var body = new
                    {
                        startDate = start.ToString("O"),
                        endDate = end.ToString("O"),
                        pageSize = 100,
                        page = page
                    };

                    var resp = await ApiGetOrdersAsync(body, _cts.Token);
                    var list = resp?.SupplierOrderListWithBranch ?? new List<OrderItem>();
                    if (list.Count == 0) break;

                    foreach (var oi in list)
                    {
                        var c = ToCached(oi);
                        _cacheByOrderItemId[c.OrderItemId] = c;
                    }

                    LogInfo($"Page {page} -> {list.Count} kayıt (cache: {_cacheByOrderItemId.Count})");
                    page++;
                }

                _lastPrefetchUtc = DateTime.UtcNow;
                var added = _cacheByOrderItemId.Count - before;
                SetStatus($"Cache hazır ✅ (+{added}, toplam {_cacheByOrderItemId.Count})");
                LogOk("Prefetch bitti.");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Prefetch iptal.");
                LogWarn("Prefetch iptal edildi.");
            }
            catch (Exception ex)
            {
                SetStatus("Prefetch hata ❌");
                LogErr(ex.Message);
                LogWhatToDo(ex);
                LogErr(ex.ToString());
            }
            finally
            {
                btnRefresh.Enabled = true;
                btnCancel.Enabled = false;
                txtOrderItemNo.Enabled = true;
                _prefetchLock.Release();
            }
        }

        // ============== RENDER + COPY ==============

        private void RenderPersonalizations(CachedOrderItem item)
        {
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();

                if (item.Personalizations.Count == 0)
                {
                    var row = new ListViewItem("Kişiselleştirme");
                    row.SubItems.Add("(Yok)");
                    row.Tag = new RowTag("Kişiselleştirme", "");
                    lv.Items.Add(row);
                    return;
                }

                foreach (var p in item.Personalizations)
                {
                    var field = string.IsNullOrWhiteSpace(p.Field) ? "(alan)" : p.Field.Trim();
                    var raw = p.RawText ?? "";

                    var row = new ListViewItem(field);
                    row.SubItems.Add(raw);                 // EKRANDA RAW
                    row.Tag = new RowTag(field, raw);      // KOPYADA smart davran
                    lv.Items.Add(row);
                }
            }
            finally
            {
                lv.EndUpdate();
            }
        }

        private void CopyOnlyPersonalizationValues(CachedOrderItem item)
        {
            // SADECE değerler kopyalanır.
            // "isim/ad soyad" gibi alanlar formatlanır ama "yilmaz -> Yılmaz" asla olmaz.
            var lines = new List<string>();

            foreach (var p in item.Personalizations)
            {
                var field = p.Field ?? "";
                var raw = (p.RawText ?? "").Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                lines.Add(FormatForCopy(field, raw));
            }

            Clipboard.SetText(lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines));
            LogInfo(lines.Count == 0 ? "Kopya: boş" : $"Kopya: {lines.Count} satır");
        }

        private static string FormatForCopy(string field, string raw)
        {
            if (!LooksLikeNameField(field)) return raw;

            // Kural: harf “düzeltme” yok, sadece büyük-küçük.
            // yilmaz -> Yilmaz (Yılmaz değil)
            return NameFormatter.FormatCustomerNameInvariant(raw);
        }

        private static bool LooksLikeNameField(string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return false;
            var f = field.Trim().ToLowerInvariant();

            // Buraya istersen keyword ekleriz
            return f.Contains("isim")
                || f.Contains("ad soyad")
                || f.Contains("adı soyadı")
                || f.Contains("ad-soyad")
                || f.Contains("isim soyisim")
                || f.Contains("name")
                || f.Contains("full name")
                || f.Contains("fullname");
        }

        private static string Preview(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 50 ? s : s.Substring(0, 50) + "…";
        }

        // ============== API LOW-LEVEL (THROTTLE + RETRY) ==============

        private async Task<GetOrdersResponse> ApiGetOrdersAsync(object body, CancellationToken ct)
        {
            var url = $"{_settings.BaseUrl}/api/v1/Order/GetOrders";
            var jsonBody = JsonSerializer.Serialize(body);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _getOrdersLock.WaitAsync(ct);
                try
                {
                    // 5 sn dolmadan asla istek atma
                    var elapsedMs = (int)(DateTime.UtcNow - _lastGetOrdersUtc).TotalMilliseconds;
                    var waitMs = GetOrdersMinIntervalMs - elapsedMs;
                    if (waitMs > 0) await Task.Delay(waitMs, ct);

                    _lastGetOrdersUtc = DateTime.UtcNow;

                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("x-api-key", _settings.ApiKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    using var res = await _http.SendAsync(req, ct);
                    var json = await res.Content.ReadAsStringAsync(ct);

                    if (res.IsSuccessStatusCode)
                    {
                        var parsed = JsonSerializer.Deserialize<GetOrdersResponse>(json, JsonOpts);
                        return parsed ?? throw new Exception("Response parse edilemedi (null).");
                    }

                    // Rate limit bazen 400 ile dönüyor
                    if (json.Contains("Limit aşımı", StringComparison.OrdinalIgnoreCase))
                    {
                        var sec = ParseRemainingSeconds(json);
                        LogWarn($"Rate limit: {sec}s bekleyip tekrar deneyeceğim…");
                        await Task.Delay(Math.Max(1000, sec * 1000 + 250), ct);
                        continue;
                    }

                    throw new Exception($"API {(int)res.StatusCode} {res.ReasonPhrase}\nRequest: {jsonBody}\nResponse: {json}");
                }
                finally
                {
                    _getOrdersLock.Release();
                }
            }

            throw new Exception("Rate limit nedeniyle istek 3 kez ertelendi ama yine başarısız oldu.");
        }

        private static int ParseRemainingSeconds(string response)
        {
            var idx = response.IndexOf("Kalan Süre:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 5;

            var tail = response.Substring(idx);
            var digits = new string(tail.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var s) ? s : 5;
        }

        private static CachedOrderItem ToCached(OrderItem oi)
        {
            var pers = new List<CachedPersonalization>();

            if (oi.OrderItemTextListModel != null && oi.OrderItemTextListModel.Count > 0)
            {
                foreach (var t in oi.OrderItemTextListModel)
                {
                    pers.Add(new CachedPersonalization(
                        Field: t.Value ?? "",
                        RawText: t.Text ?? ""
                    ));
                }
            }

            return new CachedOrderItem(
                OrderItemId: oi.OrderItemId,
                ProductName: oi.ProductName ?? "",
                Personalizations: pers
            );
        }

        // ============== SETTINGS ==============

        private void OpenSettings()
        {
            bool wasTopMost = TopMost;

            try
            {
                TopMost = false;

                using var f = new SettingsForm(_settings)
                {
                    TopMost = true,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.CenterParent
                };

                var result = f.ShowDialog(this);

                if (result == DialogResult.OK)
                {
                    SettingsStore.Save(_settings);
                    SetStatus($"Ayarlar kaydedildi. Sandbox={_settings.UseSandbox}  Range=-{_settings.PrefetchPastDays}/+{_settings.PrefetchFutureDays}");
                    LogOk("Ayarlar kaydedildi.");
                    // Otomatik prefetch YOK. Kullanıcı ↻ basacak.
                }
                else
                {
                    SetStatus("Ayarlar kapatıldı.");
                    LogInfo("Ayarlar kapatıldı.");
                }
            }
            finally
            {
                TopMost = wasTopMost;
                Activate();
            }
        }

        // ============== DATA MODELS ==============

        private sealed record CachedOrderItem(int OrderItemId, string ProductName, List<CachedPersonalization> Personalizations);
        private sealed record CachedPersonalization(string Field, string RawText);
        private sealed record RowTag(string Field, string Raw);

        // ============== NAME FORMATTER (NO TR CORRECTION, ONLY CASE) ==============

        private static class NameFormatter
        {
            public static string FormatCustomerNameInvariant(string input)
            {
                if (string.IsNullOrWhiteSpace(input)) return input ?? "";

                var raw = input.Trim();

                // Birden fazla boşluğu tek boşluğa indir
                var parts = Regex.Split(raw, @"\s+")
                                 .Where(p => !string.IsNullOrWhiteSpace(p))
                                 .ToArray();

                if (parts.Length == 0) return raw;
                if (parts.Length == 1) return TitleWord(parts[0]);

                // Soyad (son kelime)
                var surnameRaw = parts[^1];
                var nameParts = parts.Take(parts.Length - 1).Select(TitleWord);

                bool surnameWasAllCaps = IsAllLettersUpperInvariant(surnameRaw);

                var surname = surnameWasAllCaps
                    ? surnameRaw.ToUpperInvariant()
                    : TitleWord(surnameRaw);

                return string.Join(" ", nameParts.Append(surname));
            }

            // Sadece "tamamı büyük" kontrolü
            private static bool IsAllLettersUpperInvariant(string s)
            {
                bool hasLetter = false;
                foreach (var ch in s)
                {
                    if (!char.IsLetter(ch)) continue;
                    hasLetter = true;
                    if (char.ToUpperInvariant(ch) != ch) return false;
                }
                return hasLetter;
            }

            // Tire ve apostrof gibi ayırıcıları koruyarak Title Case
            private static string TitleWord(string w)
            {
                if (string.IsNullOrWhiteSpace(w)) return w;

                string TitleToken(string token)
                {
                    if (token.Length == 0) return token;

                    // DİKKAT: Invariant => türkçe harf “düzeltme” yok.
                    // Ek olarak: 'İ' -> 'i̇' gibi combining dot saçmalığını engelliyoruz.
                    var lower = ToLowerInvariantNoCombiningDot(token);
                    var first = char.ToUpperInvariant(lower[0]);
                    return lower.Length == 1 ? first.ToString() : first + lower.Substring(1);
                }

                // - ve ' ayırıcılarını koru
                var pieces = Regex.Split(w, @"([\-'])");
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (pieces[i] == "-" || pieces[i] == "'") continue;
                    pieces[i] = TitleToken(pieces[i]);
                }
                return string.Concat(pieces);
            }

            // "İ" -> "i̇" üretmesin diye basit fix:
            private static string ToLowerInvariantNoCombiningDot(string s)
            {
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s)
                {
                    if (ch == 'İ') sb.Append('i'); // düz "i" yap (harf düzeltme değil, case fix)
                    else sb.Append(char.ToLowerInvariant(ch));
                }
                return sb.ToString();
            }
        }
    }
}
