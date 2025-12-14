using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Çiçeksepeti_Order_Peek
{
    public sealed class AppSettings
    {
        // Eski tekli alanlar (geri uyum için dursun; multi-store yoksa bunlardan 1 mağaza türetiyoruz)
        public string ApiKey { get; set; } = "";
        public bool UseSandbox { get; set; } = true;

        public int PrefetchPastDays { get; set; } = 3;
        public int PrefetchFutureDays { get; set; } = 2;

        // Çoklu mağaza listesi
        public List<StoreConfig> Stores { get; set; } = new();

        [JsonIgnore]
        public string BaseUrl =>
            UseSandbox ? "https://sandbox-apis.ciceksepeti.com" : "https://apis.ciceksepeti.com";
    }

    public sealed class StoreConfig
    {
        public string Name { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public bool UseSandbox { get; set; } = true;

        [JsonIgnore]
        public string BaseUrl =>
            UseSandbox ? "https://sandbox-apis.ciceksepeti.com" : "https://apis.ciceksepeti.com";

        public override string ToString()
            => string.IsNullOrWhiteSpace(Name) ? "(İsimsiz Mağaza)" : Name;
    }
}
