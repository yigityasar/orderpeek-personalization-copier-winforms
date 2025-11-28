using System.Text.Json.Serialization;

public sealed class GetOrdersResponse
{
    [JsonPropertyName("orderListCount")]
    public int OrderListCount { get; set; }

    [JsonPropertyName("supplierOrderListWithBranch")]
    public List<OrderItem> SupplierOrderListWithBranch { get; set; } = new();
}

public sealed class OrderItem
{
    [JsonPropertyName("orderId")]
    public int OrderId { get; set; }

    [JsonPropertyName("orderItemId")]
    public int OrderItemId { get; set; }

    [JsonPropertyName("name")]
    public string? ProductName { get; set; }

    [JsonPropertyName("receiverName")]
    public string? ReceiverName { get; set; } // :contentReference[oaicite:1]{index=1}

    [JsonPropertyName("senderName")]
    public string? SenderName { get; set; }   // :contentReference[oaicite:2]{index=2}

    [JsonPropertyName("orderItemTextListModel")]
    public List<TextItem> OrderItemTextListModel { get; set; } = new(); // :contentReference[oaicite:3]{index=3}
}

public sealed class TextItem
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }   // müşteri girdisi (ASLA değiştirmiyoruz)
    [JsonPropertyName("value")]
    public string? Value { get; set; }  // alan adı (örn. "Çerçeveye Yazılacak Yazı")
}
