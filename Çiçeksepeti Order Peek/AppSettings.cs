namespace Çiçeksepeti_Order_Peek
{
    public sealed class AppSettings
    {
        public string ApiKey { get; set; } = "";
        public bool UseSandbox { get; set; } = true;

        public int PrefetchPastDays { get; set; } = 3;
        public int PrefetchFutureDays { get; set; } = 2;

        public string BaseUrl =>
            UseSandbox ? "https://sandbox-apis.ciceksepeti.com" : "https://apis.ciceksepeti.com";
    }
}
