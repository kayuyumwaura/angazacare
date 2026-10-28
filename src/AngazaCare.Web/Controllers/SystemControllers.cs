using System.Security.Cryptography;
using System.Text;
using AngazaCare.Data;
using AngazaCare.Domain;
using AngazaCare.Models;
using AngazaCare.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AngazaCare.Controllers;

/// <summary>Danger-sign rules and the lexicon. Owned by the nurse lead; changes apply to the next reply.</summary>
[Authorize(Policy = "Clinical")]
public sealed class RulesController(ConfigRepo config, RulesCache cache) : AppController
{
    public async Task<IActionResult> Index() => View(new RulesVm(await config.DangerSignsAsync(), await config.LexiconAsync()));

    [HttpPost, Authorize(Roles = Roles.Nurse + "," + Roles.Admin)]
    public async Task<IActionResult> UpdateSign(string code, string severity, int slaMinutes, string chwGuidance, string? adviceSw, string? adviceEn)
    {
        if (severity is not ("high" or "medium" or "low") || slaMinutes < 5 || slaMinutes > 10080 || string.IsNullOrWhiteSpace(chwGuidance))
            return Done("Check the values: response time must be 5 minutes to 7 days and guidance cannot be empty.", nameof(Index));
        await config.UpdateDangerSignAsync(code, severity, slaMinutes, chwGuidance.Trim(), adviceSw?.Trim(), adviceEn?.Trim());
        cache.Invalidate();
        return Done("Rule saved. It applies from the next reply.", nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> AddPhrase(string kind, string? signCode, string phrase, string language)
    {
        var tokens = RiskEngine.Tokenize(phrase ?? "");
        if (tokens.Length == 0 || kind is not ("danger" or "well" or "negation") || (kind == "danger" && string.IsNullOrEmpty(signCode)))
            return Done("Enter a phrase and choose what it means.", nameof(Index));
        await config.AddLexiconAsync(kind, signCode, string.Join(' ', tokens), language, UserId);
        cache.Invalidate();
        return Done($"Added \"{string.Join(' ', tokens)}\".", nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> DeletePhrase(int id)
    {
        await config.DeleteLexiconAsync(id);
        cache.Invalidate();
        return Done("Phrase removed.", nameof(Index));
    }

    /// <summary>Try a reply against the current rules without sending anything.</summary>
    [HttpPost]
    public async Task<IActionResult> Test(string text)
    {
        var (engine, _) = await cache.GetAsync();
        var r = engine.Evaluate(text);
        return Json(new { outcome = r.Outcome.ToString().ToLowerInvariant(), r.Method, r.Signs, r.Matched, needsModel = r.NeedsModel });
    }
}

[Authorize(Policy = "Admin")]
public sealed class StaffController(UserRepo users, ConfigRepo config) : AppController
{
    public async Task<IActionResult> Index()
    {
        ViewBag.Facilities = await config.FacilitiesAsync();
        return View(await users.AllAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Add(string fullName, string phone, string role, int? facilityId, string password)
    {
        var e164 = PhoneProtector.Normalize(phone);
        if (string.IsNullOrWhiteSpace(fullName) || e164 is null || role is not (Roles.Chw or Roles.Nurse or Roles.Admin) || (password?.Length ?? 0) < 10)
            return Done("Enter a name, a Kenyan mobile number, a role and a password of at least 10 characters.", nameof(Index));
        if (await users.ByPhoneAsync(e164) is not null) return Done("Someone already uses that phone number.", nameof(Index));
        await users.InsertAsync(fullName.Trim(), e164, role, facilityId, PasswordHasher.Hash(password!));
        return Done($"{fullName.Trim()} can now sign in.", nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetActive(int id, bool active)
    {
        if (id == UserId) return Done("You cannot deactivate your own account.", nameof(Index));
        await users.SetActiveAsync(id, active);
        return Done(active ? "Account reactivated." : "Account deactivated. They can no longer sign in.", nameof(Index));
    }
}

/// <summary>
/// Africa's Talking callbacks. Configure the callback URLs with ?key=&lt;Webhooks:Secret&gt;
/// because Africa's Talking does not sign its requests.
/// </summary>
[AllowAnonymous, IgnoreAntiforgeryToken, Route("webhooks")]
public sealed class WebhooksController(InboundService inbound, MessageRepo messages, IConfiguration config) : Controller
{
    [HttpPost("sms")]
    public async Task<IActionResult> Sms([FromQuery] string? key, [FromForm] string from, [FromForm] string? text)
    {
        if (!Authorized(key)) return Unauthorized();
        await inbound.HandleSmsAsync(from, text ?? "");
        return Ok();
    }

    [HttpPost("ussd")]
    public async Task<IActionResult> Ussd([FromQuery] string? key, [FromForm] string phoneNumber, [FromForm] string? text)
    {
        if (!Authorized(key)) return Unauthorized();
        return Content(await inbound.HandleUssdAsync(phoneNumber, text ?? ""), "text/plain");
    }

    [HttpPost("delivery")]
    public async Task<IActionResult> Delivery([FromQuery] string? key, [FromForm] string id, [FromForm] string status)
    {
        if (!Authorized(key)) return Unauthorized();
        await messages.SetStatusByGatewayIdAsync(id, status);
        return Ok();
    }

    private bool Authorized(string? key)
    {
        var secret = config["Webhooks:Secret"];
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(key)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(secret));
    }
}

/// <summary>
/// Demo phone: shows what each mother receives and lets you reply as her by SMS or USSD,
/// through the same code path as the real webhooks. Disable with Demo:ShowSimulator=false.
/// </summary>
[Authorize(Policy = "Clinical")]
public sealed class SimulatorController(
    MotherRepo mothers, MessageRepo messages, InboundService inbound, CheckInService checkIns, FlagWorkflow workflow,
    PhoneProtector phones, ISmsGateway gateway, IConfiguration config) : AppController
{
    public async Task<IActionResult> Index(int? id)
    {
        if (!config.GetValue("Demo:ShowSimulator", false)) return NotFound();
        var all = await mothers.AllAsync();
        var selected = id is null ? all.FirstOrDefault() : all.FirstOrDefault(m => m.Id == id);
        return View(new SimulatorVm(all, selected, selected is null ? [] : await messages.ForMotherAsync(selected.Id),
            await messages.StaffAlertsAsync(6), gateway.Name));
    }

    [HttpPost]
    public async Task<IActionResult> Reply(int id, string text)
    {
        if (!config.GetValue("Demo:ShowSimulator", false)) return NotFound();
        if (string.IsNullOrWhiteSpace(text)) return RedirectToAction(nameof(Index), new { id });
        var enc = await mothers.PhoneEncAsync(id);
        if (enc is null) return NotFound();
        await inbound.HandleSmsAsync(phones.Decrypt(enc), text);   // same path as a real inbound SMS
        return RedirectToAction(nameof(Index), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Ussd(int id, string text)
    {
        if (!config.GetValue("Demo:ShowSimulator", false)) return NotFound();
        var enc = await mothers.PhoneEncAsync(id);
        if (enc is null) return NotFound();
        var screen = await inbound.HandleUssdAsync(phones.Decrypt(enc), text ?? "");
        return Json(new { screen = screen[4..], done = screen.StartsWith("END") });
    }

    [HttpPost]
    public async Task<IActionResult> CheckIn(int id)
    {
        if (!config.GetValue("Demo:ShowSimulator", false)) return NotFound();
        var mother = await mothers.ByIdAsync(id);
        if (mother?.PregnancyId is null) return NotFound();
        await checkIns.SendCheckInAsync(id, mother.PregnancyId.Value);
        return RedirectToAction(nameof(Index), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> RunEscalations(int? id)
    {
        if (!config.GetValue("Demo:ShowSimulator", false)) return NotFound();
        var n = await workflow.EscalateOverdueAsync();
        TempData["Toast"] = n == 0 ? "No case has passed its response time." : $"{n} overdue case{(n == 1 ? "" : "s")} moved to the nurse queue.";
        return RedirectToAction(nameof(Index), new { id });
    }
}
