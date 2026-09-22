using System.Text;
using System.Text.Json;

namespace HRSystem.Services;

/// <summary>
/// HTTP Client to communicate with the Node.js Baileys WhatsApp microservice
/// running on http://localhost:3000/send-whatsapp
/// </summary>
public class WhatsAppService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppService> _logger;
    private readonly string _baseUrl;

    public WhatsAppService(HttpClient httpClient, IConfiguration config,
        ILogger<WhatsAppService> logger)
    {
        _httpClient = httpClient;
        _logger     = logger;
        _baseUrl    = config["WhatsApp:ServiceUrl"] ?? "http://localhost:3000";
    }

    /// <summary>Sends a WhatsApp message via the external Node.js microservice.</summary>
    public async Task<bool> SendAsync(string phone, string message)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;

        // Normalize phone: strip +, spaces, leading 0; add Egypt country code
        phone = phone.Replace("+", "").Replace(" ", "").Replace("-", "").Trim();
        phone = phone.TrimStart('0');
        if (!phone.StartsWith("20")) phone = "20" + phone;

        try
        {
            var payload = new { phone, message };
            var json    = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response  = await _httpClient.PostAsync(
                $"{_baseUrl}/send-whatsapp", content, cts.Token);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("WhatsApp send returned {Code} for {Phone}",
                    (int)response.StatusCode, phone);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp send failed for {Phone}", phone);
            return false;
        }
    }
}
