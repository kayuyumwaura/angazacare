using AngazaCare.Domain;
using AngazaCare.Services;

namespace AngazaCare.Data;

/// <summary>Applies db/*.sql (idempotent) on start, then seeds demo users and test personas once.</summary>
public sealed class DbInitializer(
    Db db, UserRepo users, MotherRepo mothers, PhoneProtector phones, CheckInService checkIns,
    InboundService inbound, FlagWorkflow workflow, CareOptions options, IConfiguration config, ILogger<DbInitializer> log)
{
    public async Task RunAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "db");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Order())
        {
            await db.ExecuteAsync(await File.ReadAllTextAsync(file));
            log.LogInformation("Applied {File}", Path.GetFileName(file));
        }

        if (await users.CountAsync() > 0) return;

        var password = config["Demo:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            log.LogWarning("No users exist and Demo:Password is empty, so no accounts were created.");
            return;
        }

        var hash = PasswordHasher.Hash(password);
        await users.InsertAsync("Programme Admin", "+254700000001", Roles.Admin, null, hash);
        var nurse = await users.InsertAsync("Amina Hassan", "+254700000002", Roles.Nurse, 2, hash);
        var grace = await users.InsertAsync("Grace Atieno", "+254700000003", Roles.Chw, 2, hash);
        var mary = await users.InsertAsync("Mary Wanjiru", "+254700000004", Roles.Chw, 1, hash);
        log.LogInformation("Created demo staff accounts");

        if (!config.GetValue("Demo:SeedPersonas", true)) return;
        await SeedPersonasAsync(nurse, grace, mary);
    }

    /// <summary>Fictional test personas that walk the loop, so every screen has something to show.</summary>
    private async Task SeedPersonasAsync(int nurseId, int grace, int mary)
    {
        var today = options.LocalToday;
        async Task<Mother> Enrol(string name, string phone, string lang, string village, int chw, int weeks, bool discreet = false, int ancInDays = 9)
        {
            var e164 = PhoneProtector.Normalize(phone)!;
            var edd = today.AddDays((40 - weeks) * 7);
            var id = await mothers.EnrolAsync(name, phones.Encrypt(e164), phones.Hash(e164), e164[^3..], lang, village, chw, discreet,
                options.ConsentVersion, edd, null, today.AddDays(ancInDays), DateTime.UtcNow + options.CheckInInterval);
            var m = (await mothers.ByIdAsync(id))!;
            await checkIns.SendCheckInAsync(id, m.PregnancyId!.Value);
            return m;
        }

        var nurse = new Actor(nurseId, Roles.Nurse);
        var faith = await Enrol("Faith", "0700000101", "sw", "Kibera, Laini Saba", grace, 32);
        await inbound.ScoreAndRespondAsync(faith, "1", "sms");

        var zawadi = await Enrol("Zawadi", "0700000102", "sw", "Kibera, Soweto", grace, 28);
        await inbound.ScoreAndRespondAsync(zawadi, "Nina damu kidogo tangu asubuhi", "sms");

        var mercy = await Enrol("Mercy", "0700000103", "en", "Kawangware", grace, 36);
        var r = await inbound.ScoreAndRespondAsync(mercy, "my head is paining sana and naona giza", "sms");
        await workflow.AcknowledgeAsync(r.FlagId!.Value, new Actor(grace, Roles.Chw));
        await workflow.AssessAsync(r.FlagId!.Value, new Actor(grace, Roles.Chw),
            "Visited at home. Headache for two days, some blurred vision, mild swelling of the face.", 152, 101, 36.8m, "refer");

        var halima = await Enrol("Halima", "0700000104", "sw", "Mathare, Area 4", mary, 20, discreet: true);
        await inbound.ScoreAndRespondAsync(halima, "fiti tu", "sms");

        var wairimu = await Enrol("Wairimu", "0700000105", "sw", "Mathare, Kosovo", mary, 34, ancInDays: 1);
        r = await inbound.ScoreAndRespondAsync(wairimu, "mtoto hachezi tangu jana", "sms");
        await db.ExecuteAsync("UPDATE flags SET sla_due_at = now() - interval '5 minutes' WHERE id = @id", new { id = r.FlagId });
        await workflow.EscalateOverdueAsync();

        var akinyi = await Enrol("Akinyi", "0700000106", "sw", "Kibera, Gatwekera", mary, 24);
        r = await inbound.ScoreAndRespondAsync(akinyi, "mwili inachemka since jana", "sms");
        await workflow.AcknowledgeAsync(r.FlagId!.Value, new Actor(mary, Roles.Chw));
        await workflow.AssessAsync(r.FlagId!.Value, new Actor(mary, Roles.Chw), "Temperature 38.6. Body aches, no bleeding.", 118, 76, 38.6m, "refer");
        await workflow.ApproveAsync(r.FlagId!.Value, nurse, 2, "same_day", "Malaria test and review at the health centre today.");

        var neema = await Enrol("Neema", "0700000107", "sw", "Kawangware", grace, 30);
        await inbound.ScoreAndRespondAsync(neema, "nimechoka tu na miguu inauma kidogo", "sms");

        var esther = await Enrol("Esther", "0700000108", "en", "Mathare, Area 2", mary, 14);
        _ = esther; // check-in sent, no reply yet

        log.LogInformation("Seeded {Count} test personas", 8);
    }
}
