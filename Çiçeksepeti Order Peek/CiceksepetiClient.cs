using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace Çiçeksepeti_Order_Peek
{ 
public sealed class CiceksepetiClient
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public CiceksepetiClient(HttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<GetOrdersResponse> GetOrdersByOrderNoAsync(int orderNo, CancellationToken ct)
    {
        // POST /api/v1/Order/GetOrders :contentReference[oaicite:4]{index=4}
        var url = $"{_settings.BaseUrl}/api/v1/Order/GetOrders";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", _settings.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // orderNo doluysa start/end zorunlu değil :contentReference[oaicite:5]{index=5}
        var body = new { pageSize = 100, page = 0, orderNo = orderNo }; // page 0’dan başlar, pageSize max 100 :contentReference[oaicite:6]{index=6}
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new Exception($"API {(int)res.StatusCode}: {json}");

        return JsonSerializer.Deserialize<GetOrdersResponse>(json)
            ?? throw new Exception("Response boş/parse edilemedi.");
    }
}
}
