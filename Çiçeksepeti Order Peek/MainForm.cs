using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
        private readonly Button btnRecord = new();
        private readonly Button btnPlayback = new();
        private readonly ListView lv = new();
        private readonly Label lblStatus = new();
        private readonly RichTextBox txtLog = new();

        // Thumbnails
        private readonly ImageList _thumbs = new()
        {
            ImageSize = new Size(48, 48),
            ColorDepth = ColorDepth.Depth32Bit
        };
        private const string PlaceholderKey = "__placeholder__";

        // Cache
        private readonly Dictionary<int, CachedOrderItem> _cacheByOrderItemId = new();

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

        // Image download/cache
        private readonly Dictionary<string, string> _localFileByUrl = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _thumbLoading = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _imgDlLock = new(3, 3); // aynı anda max 3 download

        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrderPeek", "image-cache");

        // KAYIT MODU
        private bool _isRecording = false;
        private bool _isPlayback = false;
        private readonly List<int> _recordedOrderItemNos = new();
        private int _recordIndex = -1;

        public MainForm()
        {
            _settings = SettingsStore.Load();

            BuildUi();
            ApplyAlwaysOnTopBottomRight();
            Directory.CreateDirectory(CacheDir);

            KeyPreview = true; // ok tuşları formda yakalansın

            // Uygulama açılır açılmaz sipariş çekme yok.
            // Sadece API key boşsa ayar ekranını aç.
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
            Width = 360;
            Height = 440;

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

            btnRecord.Text = "Rec";
            btnRecord.Dock = DockStyle.Right;
            btnRecord.Width = 50;
            btnRecord.Click += (_, __) => ToggleRecording();

            btnPlayback.Text = "▶";
            btnPlayback.Dock = DockStyle.Right;
            btnPlayback.Width = 42;
            btnPlayback.Click += async (_, __) => await StartPlaybackAsync();

            topPanel.Controls.Add(txtOrderItemNo);
            topPanel.Controls.Add(btnSettings);
            topPanel.Controls.Add(btnRefresh);
            topPanel.Controls.Add(btnPlayback);
            topPanel.Controls.Add(btnRecord);
            topPanel.Controls.Add(btnCancel);

            // Liste
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;

            // FOTO için çoklu seçim şart
            lv.MultiSelect = true;

            lv.Font = new Font("Segoe UI", 8.5f);
            lv.SmallImageList = _thumbs;

            if (!_thumbs.Images.ContainsKey(PlaceholderKey))
                _thumbs.Images.Add(PlaceholderKey, MakePlaceholderThumb());

            lv.Columns.Clear();
            lv.Columns.Add("Alan", 150);
            lv.Columns.Add("Müşteri", 170);

            // Tek seçimde kopyala (çoklu seçimde kopyalama yapma)
            lv.ItemSelectionChanged += (_, __) =>
            {
                if (lv.SelectedItems.Count != 1) return;
                if (lv.SelectedItems[0].Tag is not RowTag tag) return;
                if (string.IsNullOrWhiteSpace(tag.Raw)) return;

                Clipboard.SetText(tag.Raw);
                SetStatus("Değer kopyalandı ✅");
                LogOk("Seçili değer panoya kopyalandı.");
            };

            // Foto sürükle-bırak
            lv.ItemDrag += (_, e) =>
            {
                try
                {
                    var urls = lv.SelectedItems
                        .Cast<ListViewItem>()
                        .Select(i => i.Tag as RowTag)
                        .Where(t => t != null && t.IsImageUrl)
                        .Select(t => t!.Raw.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (urls.Count == 0)
                    {
                        // Foto seçili değilse drag yapma (metin sürükleme istemedin)
                        return;
                    }

                    SetStatus($"Foto hazırlanıyor… ({urls.Count})");
                    LogInfo($"Drag başlıyor (foto): {urls.Count} adet");

                    var files = new List<string>();
                    foreach (var url in urls)
                    {
                        var local = EnsureLocalImageFile(url);
                        if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
                            files.Add(local);
                    }

                    if (files.Count == 0)
                    {
                        SetStatus("Foto indirilemedi.");
                        return;
                    }

                    // Photoshop/Explorer için FileDrop
                    var data = new DataObject();
                    data.SetData(DataFormats.FileDrop, files.ToArray());

                    // Bonus: bazı uygulamalar URL text sever
                    data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, urls));

                    SetStatus("Sürükleyebilirsin ✅");
                    DoDragDrop(data, DragDropEffects.Copy);
                }
                catch (Exception ex)
                {
                    SetStatus("Drag hata ❌");
                    LogErr(ex.Message);
                }
            };

            // Log
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            txtLog.Dock = DockStyle.Bottom;
            txtLog.Height = 115;
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (!_isPlayback || _recordedOrderItemNos.Count == 0)
                return;

            if (e.KeyCode == Keys.Up)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _ = NavigateRecordedAsync(-1);
            }
            else if (e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _ = NavigateRecordedAsync(+1);
            }
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

        // ================== KAYIT MODU ==================

        private void ToggleRecording()
        {
            if (!_isRecording)
            {
                _isRecording = true;
                _isPlayback = false;
                _recordedOrderItemNos.Clear();
                _recordIndex = -1;

                btnRecord.Text = "Dur";
                SetStatus("Kayıt modu: AÇIK. Sipariş numaralarını sırayla gir.");
                LogInfo("Kayıt modu AÇIK.");
            }
            else
            {
                _isRecording = false;
                btnRecord.Text = "Rec";
                SetStatus($"Kayıt modu: KAPALI. Kaydedilen sipariş: {_recordedOrderItemNos.Count}");
                LogInfo("Kayıt modu KAPALI.");
            }
        }

        private void RegisterRecording(int orderItemId)
        {
            if (!_isRecording) return;

            if (!_recordedOrderItemNos.Contains(orderItemId))
            {
                _recordedOrderItemNos.Add(orderItemId);
                _recordIndex = _recordedOrderItemNos.Count - 1;
                LogInfo($"Kayıt eklendi: {orderItemId} (toplam: {_recordedOrderItemNos.Count})");
            }
        }

        private async Task StartPlaybackAsync()
        {
            if (_recordedOrderItemNos.Count == 0)
            {
                MessageBox.Show(this,
                    "Kaydedilmiş sipariş yok.\n\nÖnce 'Rec' ile kayıt başlatıp birkaç sipariş gir.",
                    "Kayıt yok",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            _isPlayback = true;

            if (_recordIndex < 0 || _recordIndex >= _recordedOrderItemNos.Count)
                _recordIndex = 0;

            await ShowRecordedAsync();
            SetStatus($"Kayıttan okuma: {_recordIndex + 1}/{_recordedOrderItemNos.Count}. Yukarı/Aşağı ok ile gez.");
        }

        private async Task ShowRecordedAsync()
        {
            if (!_isPlayback) return;
            if (_recordIndex < 0 || _recordIndex >= _recordedOrderItemNos.Count) return;

            var id = _recordedOrderItemNos[_recordIndex];
            txtOrderItemNo.Text = id.ToString();
            await LookupAndRenderByIdFromCacheAsync(id);
        }

        private async Task NavigateRecordedAsync(int delta)
        {
            if (!_isPlayback || _recordedOrderItemNos.Count == 0) return;

            var newIndex = _recordIndex + delta;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _recordedOrderItemNos.Count) newIndex = _recordedOrderItemNos.Count - 1;
            if (newIndex == _recordIndex) return;

            _recordIndex = newIndex;
            await ShowRecordedAsync();
            SetStatus($"Kayıttan okuma: {_recordIndex + 1}/{_recordedOrderItemNos.Count}");
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

            await LookupAndRenderByIdFromCacheAsync(orderItemId);
        }

        private async Task LookupAndRenderByIdFromCacheAsync(int orderItemId)
        {
            if (_cacheByOrderItemId.TryGetValue(orderItemId, out var cached))
            {
                RegisterRecording(orderItemId);
                RenderPersonalizations(cached);
                CopyOnlyPersonalizationValues(cached);
                SetStatus("Cache’den bulundu ✅");
                LogOk($"Cache hit: {orderItemId}");
            }
            else
            {
                SetStatus("Cache’de bulunamadı.");
                LogWarn($"Cache miss (tekil API sorgusu YOK): {orderItemId}");

                MessageBox.Show(this,
                    "Bu alt sipariş cache’de bulunamadı.\n\n" +
                    "Önce ↻ ile siparişleri güncelle, sonra numarayı tekrar dene.",
                    "Sipariş bulunamadı",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            await Task.CompletedTask;
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
                    row.Tag = new RowTag("Kişiselleştirme", "", false, null);
                    row.ImageKey = PlaceholderKey;
                    lv.Items.Add(row);
                    return;
                }

                foreach (var p in item.Personalizations)
                {
                    var field = string.IsNullOrWhiteSpace(p.Field) ? "(alan)" : p.Field.Trim();
                    var raw = p.RawText ?? "";

                    bool isImg = LooksLikeImageUrl(field, raw);

                    var row = new ListViewItem(field);
                    row.SubItems.Add(raw); // ekranda RAW
                    row.Tag = new RowTag(field, raw, isImg, null);

                    if (isImg)
                    {
                        row.ImageKey = PlaceholderKey;

                        // thumbnail yükle (asenkron)
                        var url = raw.Trim();
                        var key = ThumbKey(url);
                        row.Name = key; // item identity gibi kullanacağız

                        _ = LoadThumbAsync(url, key, row);
                    }

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
            var values = item.Personalizations
                .Select(x => (x.RawText ?? "").Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            Clipboard.SetText(values.Count == 0 ? "" : string.Join(Environment.NewLine, values));
            LogInfo(values.Count == 0 ? "Kopya: boş" : $"Kopya: {values.Count} satır");
        }

        private static bool LooksLikeImageUrl(string field, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            var f = (field ?? "").ToLowerInvariant();
            if (f.Contains("foto") || f.Contains("resim") || f.Contains("photo") || f.Contains("image"))
                return true;

            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") || path.EndsWith(".webp") || path.EndsWith(".gif"))
                return true;

            var q = uri.Query.ToLowerInvariant();
            if (q.Contains(".jpg") || q.Contains(".jpeg") || q.Contains(".png") || q.Contains(".webp") || q.Contains(".gif"))
                return true;

            return f.Contains("link");
        }

        // ============== THUMBNAILS & DRAG FILES ==============

        private async Task LoadThumbAsync(string url, string key, ListViewItem row)
        {
            try
            {
                lock (_thumbLoading)
                {
                    if (_thumbLoading.Contains(url)) return;
                    _thumbLoading.Add(url);
                }

                byte[] bytes;

                if (_localFileByUrl.TryGetValue(url, out var local) && File.Exists(local))
                {
                    bytes = await File.ReadAllBytesAsync(local);
                }
                else
                {
                    await _imgDlLock.WaitAsync();
                    try
                    {
                        bytes = await _http.GetByteArrayAsync(url);
                    }
                    finally
                    {
                        _imgDlLock.Release();
                    }

                    try
                    {
                        var path = EnsureLocalPathForUrl(url);
                        if (!File.Exists(path))
                            await File.WriteAllBytesAsync(path, bytes);

                        _localFileByUrl[url] = path;
                    }
                    catch { }
                }

                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                using var img = new Bitmap(tmp);

                var thumbBmp = (Bitmap)MakeThumb(img, _thumbs.ImageSize.Width, _thumbs.ImageSize.Height);

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_thumbs.Images.ContainsKey(key))
                        {
                            _thumbs.Images.Add(key, thumbBmp);
                        }
                        else
                        {
                            thumbBmp.Dispose();
                        }

                        row.ImageKey = key;
                        lv.Invalidate();
                    }
                    catch
                    {
                        thumbBmp.Dispose();
                        throw;
                    }
                }));
            }
            catch (Exception ex)
            {
                LogWarn($"Thumb alınamadı: {ex.Message}");
            }
            finally
            {
                lock (_thumbLoading) _thumbLoading.Remove(url);
            }
        }

        private string EnsureLocalPathForUrl(string url)
        {
            Directory.CreateDirectory(CacheDir);
            var ext = GuessImageExtension(url);
            var fileName = $"orderpeek_{Sha1(url)}{ext}";
            return Path.Combine(CacheDir, fileName);
        }

        private string EnsureLocalImageFile(string url)
        {
            if (_localFileByUrl.TryGetValue(url, out var existing) && File.Exists(existing))
                return existing;

            Directory.CreateDirectory(CacheDir);

            var ext = GuessImageExtension(url);
            var fileName = $"orderpeek_{Sha1(url)}{ext}";
            var fullPath = Path.Combine(CacheDir, fileName);

            if (File.Exists(fullPath))
            {
                _localFileByUrl[url] = fullPath;
                return fullPath;
            }

            var bytes = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(fullPath, bytes);

            _localFileByUrl[url] = fullPath;
            return fullPath;
        }

        private static string GuessImageExtension(string url)
        {
            try
            {
                var u = new Uri(url);
                var p = u.AbsolutePath.ToLowerInvariant();
                if (p.EndsWith(".jpeg")) return ".jpeg";
                if (p.EndsWith(".jpg")) return ".jpg";
                if (p.EndsWith(".png")) return ".png";
                if (p.EndsWith(".webp")) return ".webp";
                if (p.EndsWith(".gif")) return ".gif";
            }
            catch { }
            return ".jpg";
        }

        private static string ThumbKey(string url) => "img_" + Sha1(url);

        private static string Sha1(string s)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static Image MakeThumb(Image src, int w, int h)
        {
            var scale = Math.Min((double)w / src.Width, (double)h / src.Height);
            var nw = Math.Max(1, (int)(src.Width * scale));
            var nh = Math.Max(1, (int)(src.Height * scale));

            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            var x = (w - nw) / 2;
            var y = (h - nh) / 2;
            g.DrawImage(src, new Rectangle(x, y, nw, nh));
            g.DrawRectangle(Pens.Gainsboro, 0, 0, w - 1, h - 1);
            return bmp;
        }

        private static Image MakePlaceholderThumb()
        {
            var bmp = new Bitmap(48, 48);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.WhiteSmoke);
            g.DrawRectangle(Pens.Gainsboro, 0, 0, 47, 47);
            using var f = new Font("Segoe UI", 7f, FontStyle.Bold);
            var s = "IMG";
            var sz = g.MeasureString(s, f);
            g.DrawString(s, f, Brushes.Gray, (48 - sz.Width) / 2, (48 - sz.Height) / 2);
            return bmp;
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
        private sealed record RowTag(string Field, string Raw, bool IsImageUrl, string? LocalPath);
    }
}
