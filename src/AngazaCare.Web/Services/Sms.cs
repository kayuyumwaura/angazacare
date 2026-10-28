using System.Text.Json;
using AngazaCare.Data;

namespace AngazaCare.Services;

public sealed record SendResult(bool Ok, string? GatewayId, string Status);

public interface ISmsGateway
{
    string Name { get; }
    Task<SendResult> SendAsync(string toE164, string body, CancellationToken ct = default);
}

/// <summary>
/// Demo gateway: nothing leaves the server. Outbound messages appear in the phone
/// simulator, which also lets you reply as the mother.
/// </summary>
public sealed class SimulatorGateway : ISmsGateway
{
    public string Name => "simulator";
    public Task<SendResult> SendAsync(string toE164, string body, CancellationToken ct = default)
        => Task.FromResult(new SendResult(true, "sim-" + Guid.NewGuid().ToString("N")[..12], "Success"));
}

/// <summary>
/// Africa's Talking bulk SMS API. Works against the sandbox (username "sandbox")
/// and production. Docs: https://developers.africastalking.com/docs/sms/sending
/// </summary>
public sealed class AfricasTalkingGateway(HttpClient http, IConfiguration config, ILogger<AfricasTalkingGateway> log) : ISmsGateway
{
    public string Name => "africastalking";

    public async Task<SendResult> SendAsync(string toE164, string body, CancellationToken ct = default)
    {
        var username = config["AfricasTalking:Username"] ?? "sandbox";
        var baseUrl = username == "sandbox" ? "https://api.sandbox.africastalking.com" : "https://api.africastalking.com";
        var form = new Dictionary<string, string> { ["username"] = username, ["to"] = toE164, ["message"] = body };
        if (config["AfricasTalking:SenderId"] is { Length: > 0 } sender) form["from"] = sender;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/version1/messaging") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("apiKey", config["AfricasTalking:ApiKey"]);
        req.Headers.Add("Accept", "application/json");
        try
        {
            using var res = await http.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Africa's Talking returned {Status}: {Body}", (int)res.StatusCode, json);
                return new(false, null, $"HTTP {(int)res.StatusCode}");
            }
            using var doc = JsonDocument.Parse(json);
            var recipient = doc.RootElement.GetProperty("SMSMessageData").GetProperty("Recipients")[0];
            var status = recipient.GetProperty("status").GetString() ?? "Unknown";
            return new(status == "Success", recipient.GetProperty("messageId").GetString(), status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            log.LogError(ex, "SMS send failed");
            return new(false, null, "Failed");
        }
    }
}

/// <summary>Every outbound message is written to the messages table first, then sent.</summary>
public sealed class Outbox(ISmsGateway gateway, MessageRepo messages, MotherRepo mothers, UserRepo users, PhoneProtector phones, ILogger<Outbox> log)
{
    public async Task<long> ToMotherAsync(int motherId, string purpose, string body, int? checkInId = null, string channel = "sms")
    {
        var id = await messages.InsertAsync(motherId, null, checkInId, "out", channel, purpose, body);
        if (channel == "ussd") { await messages.SetGatewayResultAsync(id, null, "Shown"); return id; }

        var enc = await mothers.PhoneEncAsync(motherId);
        if (enc is null) return id;
        var result = await gateway.SendAsync(phones.Decrypt(enc), body);
        await messages.SetGatewayResultAsync(id, result.GatewayId, result.Status);
        if (!result.Ok) log.LogWarning("SMS {MessageId} to mother {MotherId} not accepted: {Status}", id, motherId, result.Status);
        return id;
    }

    /// <summary>Alerts to CHWs and nurses. Sent by SMS so they arrive without data or the app open.</summary>
    public async Task ToStaffAsync(int userId, string purpose, string body)
    {
        var user = await users.ByIdAsync(userId);
        if (user is null || !user.Active) return;
        var id = await messages.InsertAsync(null, userId, null, "out", "sms", purpose, body);
        var result = await gateway.SendAsync(user.Phone, body);
        await messages.SetGatewayResultAsync(id, result.GatewayId, result.Status);
    }

    /// <summary>Replies to a number that is not enrolled; stored without a mother link.</summary>
    public async Task ToUnknownAsync(string e164, string body)
    {
        var id = await messages.InsertAsync(null, null, null, "out", "sms", "system", body);
        var result = await gateway.SendAsync(e164, body);
        await messages.SetGatewayResultAsync(id, result.GatewayId, result.Status);
    }
}
