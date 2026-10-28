using AngazaCare.Data;
using AngazaCare.Domain;
using AngazaCare.Models;
using AngazaCare.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AngazaCare.Controllers;

[Authorize(Policy = "Chw")]
public sealed class ChwController(FlagRepo flags, MotherRepo mothers, ConfigRepo config, PhoneProtector phones, CareOptions options, CheckInService checkIns) : AppController
{
    public async Task<IActionResult> Index()
    {
        var all = await flags.ForChwAsync(UserId, liveOnly: false);
        var live = all.Where(f => f.IsLive && f.State != FlagStates.Assessed).ToList();
        var waiting = all.Where(f => f.State == FlagStates.Assessed).ToList();
        return View("Cases", new CaseListVm(
            "Your cases",
            live.Count switch { 0 => "Nobody needs a visit right now.", 1 => "One mother needs you.", var n => $"{n} mothers need you, most urgent first." },
            live, waiting, await config.DangerSignsAsync(),
            "When a mother reports a danger sign, she appears here and you get an SMS."));
    }

    [HttpGet]
    public IActionResult Enrol() => View(new EnrolInput());

    [HttpPost]
    public async Task<IActionResult> Enrol(EnrolInput input)
    {
        var phone = PhoneProtector.Normalize(input.Phone);
        if (phone is null) ModelState.AddModelError(nameof(input.Phone), "Use a Kenyan mobile number, for example 0712 345 678.");
        else if (await mothers.IdByPhoneHashAsync(phones.Hash(phone)) is not null)
            ModelState.AddModelError(nameof(input.Phone), "This number is already enrolled.");

        var today = options.LocalToday;
        DateOnly? edd = input.DateBasis == "edd" ? input.Edd : input.Lmp is { } lmp ? Gestation.EddFromLmp(lmp) : null;
        if (edd is null) ModelState.AddModelError(input.DateBasis == "edd" ? nameof(input.Edd) : nameof(input.Lmp), "Enter a date so we can work out her weeks.");
        else if (edd < today || edd > today.AddDays(280)) ModelState.AddModelError(input.DateBasis == "edd" ? nameof(input.Edd) : nameof(input.Lmp), "That date gives a pregnancy outside 0 to 40 weeks. Check it.");
        if (input.NextAnc is { } anc && anc < today) ModelState.AddModelError(nameof(input.NextAnc), "The next clinic date is in the past.");
        if (!input.ConsentRead || !input.ConsentGiven) ModelState.AddModelError(nameof(input.ConsentGiven), "Read the consent statement to her and record her agreement before enrolling.");
        if (!ModelState.IsValid) return View(input);

        var id = await mothers.EnrolAsync(input.DisplayName.Trim(), phones.Encrypt(phone!), phones.Hash(phone!), phone![^3..], input.Language,
            input.Village.Trim(), UserId, input.Discreet, options.ConsentVersion, edd!.Value, input.DateBasis == "lmp" ? input.Lmp : null,
            input.NextAnc, DateTime.UtcNow + options.CheckInInterval);
        var mother = (await mothers.ByIdAsync(id))!;
        await checkIns.SendCheckInAsync(id, mother.PregnancyId!.Value);   // first check-in goes out now, so she sees it working
        return Done($"{mother.DisplayName} is enrolled. Her first check-in SMS has been sent.", "Details", "Mothers", new { id });
    }
}

[Authorize(Policy = "Clinical")]
public sealed class NurseController(FlagRepo flags, ConfigRepo config, MetricsRepo metrics) : AppController
{
    public async Task<IActionResult> Index()
    {
        var needs = await flags.ByStatesAsync([FlagStates.Assessed, FlagStates.Escalated]);
        var watching = (await flags.ByStatesAsync([FlagStates.Open, FlagStates.Acknowledged])).ToList();
        return View(new NurseQueueVm(needs, watching, await flags.RecentlyDecidedAsync(8), await config.DangerSignsAsync(), await metrics.HeadlineAsync()));
    }

    public async Task<IActionResult> Metrics() =>
        View(new MetricsVm((await metrics.HeadlineAsync())!, await metrics.FlagsBySignAsync(), await metrics.AssessmentsByMethodAsync(), await metrics.FlagsByStateAsync()));
}

[Authorize]
public sealed class MothersController(MotherRepo mothers, MessageRepo messages, FlagRepo flags, ConfigRepo config, FlagWorkflow workflow, CareOptions options, IConfiguration appConfig) : AppController
{
    public async Task<IActionResult> Index(string? filter)
    {
        var list = Role == Roles.Chw ? await mothers.ForChwAsync(UserId) : await mothers.AllAsync();
        if (!string.IsNullOrWhiteSpace(filter))
            list = list.Where(m => m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || m.Village.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || m.PhoneLast3 == filter.Trim()).ToList();
        return View(new MotherListVm(list, Role != Roles.Chw, filter));
    }

    public async Task<IActionResult> Details(int id)
    {
        var mother = await mothers.ByIdAsync(id);
        if (mother is null) return NotFound();
        if (Role == Roles.Chw && mother.ChwId != UserId) return Forbid();
        return View(new MotherVm(mother, await messages.ForMotherAsync(id), await messages.AssessmentsForMotherAsync(id),
            await flags.ForMotherAsync(id), await config.DangerSignsAsync(), Role, appConfig.GetValue("Demo:ShowSimulator", false)));
    }

    [HttpPost]
    public async Task<IActionResult> UpdateAnc(int id, DateOnly? nextAnc)
    {
        var mother = await mothers.ByIdAsync(id);
        if (mother?.PregnancyId is null) return NotFound();
        if (Role == Roles.Chw && mother.ChwId != UserId) return Forbid();
        if (nextAnc is { } d && d < options.LocalToday) return Done("The next clinic date is in the past. Nothing was changed.", nameof(Details), routeValues: new { id });
        await mothers.UpdateCareAsync(mother.PregnancyId.Value, nextAnc);
        return Done(nextAnc is null ? "Clinic date cleared." : "Clinic date saved. She gets a reminder the day before.", nameof(Details), routeValues: new { id });
    }

    [HttpPost]
    public async Task<IActionResult> EndPregnancy(int id, string outcome)
    {
        var mother = await mothers.ByIdAsync(id);
        if (mother?.PregnancyId is null) return NotFound();
        if (Role == Roles.Chw && mother.ChwId != UserId) return Forbid();
        if (mother.LiveFlagState is not null) return Done("Resolve her open case first.", nameof(Details), routeValues: new { id });
        await mothers.CloseAsync(mother.PregnancyId.Value, outcome == "delivered" ? "delivered" : "closed");
        return Done(outcome == "delivered" ? "Recorded as delivered. Antenatal check-ins have stopped." : "Pregnancy record closed. Check-ins have stopped.",
            nameof(Details), routeValues: new { id });
    }

    [HttpPost]
    public async Task<IActionResult> SetOptOut(int id, bool optedOut)
    {
        var mother = await mothers.ByIdAsync(id);
        if (mother is null) return NotFound();
        if (Role == Roles.Chw && mother.ChwId != UserId) return Forbid();
        await mothers.SetOptOutAsync(id, optedOut);
        return Done(optedOut ? "Messages stopped for her." : "Messages restarted for her.", nameof(Details), routeValues: new { id });
    }

    /// <summary>A worried call from a CHW never waits for the system: staff can open a case directly.</summary>
    [HttpPost]
    public async Task<IActionResult> OpenCase(int id, string sign, string note)
    {
        var mother = await mothers.ByIdAsync(id);
        if (mother?.PregnancyId is null) return NotFound();
        if (Role == Roles.Chw && mother.ChwId != UserId) return Forbid();
        if (string.IsNullOrWhiteSpace(note)) return Done("Add a short note on why you are opening the case.", nameof(Details), routeValues: new { id });
        var flagId = await workflow.RaiseAsync(id, mother.PregnancyId.Value, [sign], null, Role == Roles.Chw ? "chw" : "nurse", Actor, note.Trim());
        return Done("Case opened.", "Details", "Cases", new { id = flagId });
    }
}

[Authorize]
public sealed class CasesController(FlagRepo flags, MotherRepo mothers, MessageRepo messages, ConfigRepo config, FlagWorkflow workflow, PhoneProtector phones) : AppController
{
    public async Task<IActionResult> Details(int id)
    {
        var flag = await flags.ByIdAsync(id);
        if (flag is null) return NotFound();
        if (Role == Roles.Chw && flag.ChwId != UserId) return Forbid();
        var mother = (await mothers.ByIdAsync(flag.MotherId))!;
        var role = Role;
        // The full number is shown only on the case page, to the people who need to call her.
        if (flag.IsLive && await mothers.PhoneEncAsync(flag.MotherId) is { } enc) ViewBag.Phone = phones.Decrypt(enc);
        return View(new CaseVm(flag, mother, await flags.EventsAsync(id), await messages.ForMotherAsync(flag.MotherId, 12),
            await messages.AssessmentsForMotherAsync(flag.MotherId), await config.DangerSignsAsync(), await config.FacilitiesAsync(),
            await flags.ReferralAsync(id), role,
            FlagTransitions.Can(FlagAction.Acknowledge, flag.State, role),
            FlagTransitions.Can(FlagAction.Assess, flag.State, role),
            FlagTransitions.Can(FlagAction.Approve, flag.State, role)));
    }

    [HttpPost]
    public async Task<IActionResult> Acknowledge(int id)
    {
        if (!await MayActAsync(id)) return Forbid();
        return await Try(id, () => workflow.AcknowledgeAsync(id, Actor), "Marked as seen. Call or visit her next.");
    }

    [HttpPost]
    public async Task<IActionResult> Assess(int id, AssessInput input)
    {
        if (!await MayActAsync(id)) return Forbid();
        if (!ModelState.IsValid)
            return Done(string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)), nameof(Details), routeValues: new { id });
        return await Try(id, () => workflow.AssessAsync(id, Actor, input.Findings, input.BpSystolic, input.BpDiastolic, input.TemperatureC, input.Recommendation),
            "Findings sent to the nurse for a decision.");
    }

    [HttpPost, Authorize(Roles = Roles.Nurse)]
    public async Task<IActionResult> Decide(int id, DecisionInput input)
    {
        if (input.Decision == "approve")
        {
            if (input.FacilityId is null) return Done("Choose the facility she should go to.", nameof(Details), routeValues: new { id });
            return await Try(id, () => workflow.ApproveAsync(id, Actor, input.FacilityId.Value, input.Urgency, input.Note),
                "Referral approved. She and her CHW have been sent the details.");
        }
        if (string.IsNullOrWhiteSpace(input.Note)) return Done("Add a note explaining why no referral is needed.", nameof(Details), routeValues: new { id });
        return await Try(id, () => workflow.RejectAsync(id, Actor, input.Note, input.TellMother), "Case closed. Her CHW has been told.");
    }

    private async Task<bool> MayActAsync(int id)
    {
        if (Role != Roles.Chw) return true;
        var flag = await flags.ByIdAsync(id);
        return flag is not null && flag.ChwId == UserId;
    }

    private async Task<IActionResult> Try(int id, Func<Task> action, string success)
    {
        try { await action(); return Done(success, nameof(Details), routeValues: new { id }); }
        catch (FlagConflictException ex) { return Done(ex.Message, nameof(Details), routeValues: new { id }); }
        catch (ArgumentException ex) { return Done(ex.Message, nameof(Details), routeValues: new { id }); }
    }
}
