using System.Text.Json;
using AngazaCare.Data;
using AngazaCare.Domain;

namespace AngazaCare.Services;

public sealed record ReplyResult(long MessageId, Outcome Outcome, IReadOnlyList<string> Signs, int? FlagId, string Method);

/// <summary>
/// Handles every message a mother sends, by SMS or USSD: store it, score it,
/// raise or update a flag, and answer her with a pre-written message.
/// </summary>
public sealed class InboundService(
    Db db, MotherRepo mothers, MessageRepo messages, PhoneProtector phones, Outbox outbox, RulesCache rules,
    LlmClient llm, FlagWorkflow workflow, CareOptions options, ILogger<InboundService> log)
{
    public async Task<ReplyResult?> HandleSmsAsync(string fromRaw, string text)
    {
        var phone = PhoneProtector.Normalize(fromRaw);
        var motherId = phone is null ? null : await mothers.IdByPhoneHashAsync(phones.Hash(phone));
        if (motherId is null)
        {
            log.LogInformation("SMS from a number that is not enrolled");
            if (phone is not null) await outbox.ToUnknownAsync(phone, Templates.UnknownNumber);
            return null;
        }

        var mother = (await mothers.ByIdAsync(motherId.Value))!;
        var keyword = RiskEngine.Normalize(text);
        if (keyword is "stop" or "acha" or "simama")
        {
            var id = await messages.InsertAsync(mother.Id, null, null, "in", "sms", "opt_out", text);
            await mothers.SetOptOutAsync(mother.Id, true);
            await outbox.ToMotherAsync(mother.Id, "system", Templates.OptedOut(mother.Language));
            return new(id, Outcome.Well, [], null, "keyword");
        }
        if (keyword is "start" or "anza")
        {
            var id = await messages.InsertAsync(mother.Id, null, null, "in", "sms", "opt_in", text);
            await mothers.SetOptOutAsync(mother.Id, false);
            await outbox.ToMotherAsync(mother.Id, "system", Templates.OptedIn(mother.Language));
            return new(id, Outcome.Well, [], null, "keyword");
        }
        return await ScoreAndRespondAsync(mother, text, "sms");
    }

    /// <summary>Africa's Talking USSD: text is "" on dial, then choices joined by '*'.</summary>
    public async Task<string> HandleUssdAsync(string phoneRaw, string text)
    {
        var phone = PhoneProtector.Normalize(phoneRaw);
        var motherId = phone is null ? null : await mothers.IdByPhoneHashAsync(phones.Hash(phone));
        if (motherId is null) return "END " + Templates.UnknownNumber;
        var mother = (await mothers.ByIdAsync(motherId.Value))!;

        var parts = (text ?? "").Split('*', 2);
        var choice = parts[0].Trim();
        var detail = parts.Length > 1 ? parts[1].Trim() : null;
        if (choice == "") return Templates.UssdMenu(mother);

        // Discreet menu: 1 = well, 2 = not well (then describe). Full menu: 8 = other (then describe).
        var describeOption = mother.Discreet ? "2" : "8";
        if (choice == describeOption && string.IsNullOrEmpty(detail)) return Templates.UssdDescribe(mother);

        var reply = choice == describeOption ? (mother.Discreet ? detail! : $"8 {detail}") : choice;
        if (mother.Discreet && choice is not ("1" or "2")) return Templates.UssdMenu(mother);

        var result = await ScoreAndRespondAsync(mother, reply!, "ussd");
        var last = await db.ScalarAsync<string>(
            "SELECT body FROM messages WHERE mother_id = @id AND direction = 'out' ORDER BY id DESC LIMIT 1", new { id = mother.Id });
        return "END " + (last ?? Templates.AckWell(mother));
    }

    public async Task<ReplyResult> ScoreAndRespondAsync(Mother mother, string text, string channel)
    {
        var checkInId = await db.ScalarAsync<int?>("""
            SELECT c.id FROM check_ins c JOIN pregnancies p ON p.id = c.pregnancy_id
            WHERE p.mother_id = @id AND c.kind = 'check_in' AND c.replied_at IS NULL AND c.sent_at > now() - interval '3 days'
            ORDER BY c.sent_at DESC LIMIT 1
            """, new { id = mother.Id });
        var messageId = await messages.InsertAsync(mother.Id, null, checkInId, "in", channel, "reply", text);
        if (checkInId is not null)
            await db.ExecuteAsync("UPDATE check_ins SET replied_at = now() WHERE id = @checkInId", new { checkInId });

        var (engine, signs) = await rules.GetAsync();
        var result = engine.Evaluate(text);
        var outcome = result.Outcome;
        var codes = result.Signs.ToList();
        var matched = result.Matched.ToList();
        var method = result.Method;
        decimal? confidence = result.Outcome == Outcome.Unclear ? null : 1m;
        string? model = null, promptVersion = null, raw = null;
        int? latency = null;

        if (result.NeedsModel && llm.Enabled)
        {
            var allowed = signs.Where(s => s.Code != RiskEngine.Unclear).Select(s => s.Code).ToList();
            var ai = await llm.ParseAsync(text, mother.Language, allowed);
            latency = ai.LatencyMs;
            raw = ai.RawJson;
            if (ai.Parse is { } p)
            {
                model = p.Model;
                promptVersion = p.PromptVersion;
                var normalized = RiskEngine.Normalize(text);
                // Trust a sign only if it is a known code, confident enough, and its evidence is really in the text.
                var trusted = p.Signs
                    .Where(s => allowed.Contains(s.Code) && s.Confidence >= options.ModelSignThreshold
                                && !string.IsNullOrWhiteSpace(s.Evidence) && normalized.Contains(RiskEngine.Normalize(s.Evidence!)))
                    .ToList();
                if (trusted.Count > 0)
                {
                    outcome = Outcome.Danger; method = "llm";
                    codes = trusted.Select(s => s.Code).Distinct().ToList();
                    matched.AddRange(trusted.Select(s => s.Evidence!));
                    confidence = (decimal)trusted.Min(s => s.Confidence);
                }
                else if (result.ModelMayClear && p.Signs.Count == 0 && p.Wellbeing == "well" && p.WellbeingConfidence >= options.ModelWellThreshold)
                {
                    outcome = Outcome.Well; method = "llm"; codes = [];
                    confidence = (decimal)p.WellbeingConfidence;
                }
                else
                {
                    method = "llm"; // the model could not settle it either: a person reads it
                    confidence = p.Signs.Count > 0 ? (decimal)p.Signs.Max(s => s.Confidence) : (decimal)p.WellbeingConfidence;
                }
            }
            else method = "fallback";
        }

        await db.ExecuteAsync("""
            INSERT INTO assessments (message_id, outcome, method, signs, matched, confidence, model, prompt_version, raw_output, latency_ms)
            VALUES (@messageId, @outcome, @method, @codes, @matched, @confidence, @model, @promptVersion, CAST(@raw AS jsonb), @latency)
            """, new
        {
            messageId, outcome = outcome.ToString().ToLowerInvariant(), method, codes = codes.ToArray(), matched = matched.ToArray(),
            confidence, model, promptVersion, raw = IsJson(raw) ? raw : null, latency,
        });

        int? flagId = null;
        if (outcome == Outcome.Well)
        {
            await outbox.ToMotherAsync(mother.Id, "ack", Templates.AckWell(mother), channel: channel);
        }
        else if (mother.PregnancyId is int pregnancyId)
        {
            flagId = await workflow.RaiseAsync(mother.Id, pregnancyId, codes, messageId, "engine", null);
            // High-severity signs get the pre-approved safety advice at once; the AI never writes to her.
            var advice = signs.Where(s => codes.Contains(s.Code) && s.Severity == "high")
                .Select(s => Templates.Advice(mother, s)).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            await outbox.ToMotherAsync(mother.Id, advice is null ? "ack" : "advice", advice ?? Templates.AckFollowUp(mother), channel: channel);
            // A USSD screen disappears when she hangs up, so safety advice also goes by SMS.
            if (advice is not null && channel == "ussd")
                await outbox.ToMotherAsync(mother.Id, "advice", advice);
        }
        else
        {
            await outbox.ToMotherAsync(mother.Id, "ack", Templates.AckFollowUp(mother), channel: channel);
        }

        return new(messageId, outcome, codes, flagId, method);
    }

    private static bool IsJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { using var _ = JsonDocument.Parse(s); return true; } catch (JsonException) { return false; }
    }
}
