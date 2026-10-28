# Angaza Care Maternal

Antenatal follow-up for community health workers (CHWs) and nurses in low-bandwidth settings.

Pregnant women answer a weekly check-in by SMS or USSD from any phone. A rules-first risk engine reads each reply for WHO antenatal danger signs, in English, Swahili and Sheng. A danger sign alerts her CHW by SMS at once. The CHW visits and records findings, and **a nurse approves every referral before anything reaches the mother**. Every step is timestamped in an append-only audit trail.

Built for the UNICEF Femtech Ventures call (Pillar 1: continuity of care across the maternal continuum). Open source under MIT.

## How a reply becomes a referral

```
Mother replies (SMS / USSD)
        │
        ▼
Rules engine ── menu number or known phrase ──► danger sign ──► flag raised
        │                                                     │
        │ no match                                            ├─► CHW alerted by SMS
        ▼                                                     └─► high severity: pre-approved safety SMS to her,
Language model (optional) ── sign it can quote ─► flag            nurses alerted
        │
        └── unsure / offline / error ─────────────► flag "needs a person"

Flag: New ─► Seen by CHW ─► Visited (findings) ─► Nurse approves ─► referral SMS to her + CHW
          └─ response time missed ─► Escalated to nurse queue ─┘   └─ or closes with a note
```

Safety properties, each covered by tests:

- A danger phrase always raises a flag, whatever else the reply says ("niko sawa lakini damu inatoka" is bleeding).
- "Well" needs a short reply of well-words with no "but" (lakini, ila) and no negated well-word.
- The model is asked only when the rules found nothing. It can add a sign only if it quotes the words from her message; it can never close a case, and it may not call a reply with a "but" or a denial "well".
- Model errors, timeouts and low confidence send the reply to a person.
- Only a nurse can approve or close. The system can only escalate. Closed cases cannot move.
- Nothing the model writes is ever sent to a mother; every SMS is a reviewed template.
- `flag_events` rejects UPDATE and DELETE at the database level.

## What is in the repository

| Path | What it is |
| --- | --- |
| `src/AngazaCare.Web` | ASP.NET Core 8 MVC app: CHW PWA, nurse queue, rules editor, phone simulator, Africa's Talking webhooks |
| `db/` | PostgreSQL schema (FHIR-shaped tables) and reference data: danger signs, SLAs, advice SMS, lexicon |
| `ai/` | Optional FastAPI service that asks an open-weight model to read free-text replies |
| `eval/` | Harness to compare models on labelled replies (recall by language and sign) |
| `tests/` | xUnit tests for the risk engine, the flag state machine and helpers |

Stack: C#, ADO.NET over Npgsql with raw SQL (no ORM), Razor views, plain JavaScript, a hosted background service in place of a job queue. No Docker required.

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and PostgreSQL 14 or later.

```bash
# 1. Database
createdb angaza            # or: psql -c "create database angaza"

# 2. Point the app at it (Development settings expect user postgres / password postgres)
cd src/AngazaCare.Web
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Angaza" "Host=localhost;Database=angaza;Username=postgres;Password=YOUR_PASSWORD"

# 3. Run
dotnet run
```

Open http://localhost:5080. On first start the app creates the tables, the demo staff and eight fictional mothers who walk through every state of the loop. Demo password: `angaza-demo`.

| Who | Phone |
| --- | --- |
| CHW Grace Atieno | 0700 000 003 |
| CHW Mary Wanjiru | 0700 000 004 |
| Nurse Amina Hassan | 0700 000 002 |
| Admin | 0700 000 001 |

Development settings speed time up for demos: check-ins every 30 minutes, and response times at one tenth of their real length (`Care:SlaScale = 0.1`), so a 2-hour bleeding SLA lasts 12 minutes.

### Demo script (about 3 minutes)

1. Sign in as **Nurse Amina**, open **Phone**, pick Faith and send *kichwa inaniuma sana na naona giza* (Sheng: bad headache and seeing darkness).
2. Faith's phone shows the pre-approved safety SMS; the staff alerts panel shows the SMS to CHW Grace.
3. Switch to **CHW Grace** on a phone-sized window: Faith is at the top of **Cases** with a draining response clock. Mark as seen, record a visit with BP 150/100, recommend referral.
4. Back as the nurse, open the **Queue**, approve the referral to Upendo Health Centre for today. Faith's phone receives the referral SMS.
5. Open the case's **audit trail** and the **Numbers** page.
6. On the **USSD** tab, dial `*384#` as Esther and choose 4 (baby not moving).

## Configuration

All settings live in `appsettings.json`; override them with environment variables (`Care__SlaScale=1`) or user secrets.

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings:Angaza` | PostgreSQL connection |
| `Security:PhoneHashKey` | Random secret, 32+ characters, for looking up phone numbers. Never change it after enrolling mothers |
| `DataProtection:KeyPath` | Folder for the keys that encrypt phone numbers. **Back it up**: losing it makes stored numbers unreadable |
| `Sms:Gateway` | `simulator` (default) or `africastalking` |
| `AfricasTalking:Username`, `ApiKey`, `SenderId` | Use `sandbox` while testing |
| `Webhooks:Secret` | Shared secret appended to callback URLs |
| `AiService:BaseUrl` | URL of the `ai/` service. Leave empty to send every unreadable reply to a person |
| `Care:CheckInInterval` | Time between check-ins, `7.00:00:00` in production |
| `Care:SlaScale` | `1` in production |
| `Demo:Password`, `Demo:SeedPersonas`, `Demo:ShowSimulator` | Turn all three off in production |

### Africa's Talking

In the Africa's Talking dashboard (sandbox first), set:

- SMS inbox callback: `https://YOUR_HOST/webhooks/sms?key=WEBHOOK_SECRET`
- SMS delivery reports: `https://YOUR_HOST/webhooks/delivery?key=WEBHOOK_SECRET`
- USSD callback: `https://YOUR_HOST/webhooks/ussd?key=WEBHOOK_SECRET`

Africa's Talking does not sign callbacks, so the secret in the URL is the guard; serve the app over HTTPS only. Apply for a live shortcode and USSD code early, as provisioning can take weeks.

## The language model (optional)

```bash
cd ai
pip install -r requirements.txt
# any OpenAI-compatible server: Ollama, llama.cpp, vLLM, or a hosted open-weight provider
LLM_BASE_URL=http://localhost:11434/v1 LLM_MODEL=qwen3:32b uvicorn app:app --port 8001
```

Then set `AiService:BaseUrl` to `http://localhost:8001`. Only the reply text is sent to the model.

### Choosing a model

No open model handles Sheng reliably yet, so measure on your own replies:

1. With 2 to 3 CHWs, write 150 to 200 replies in the format of `eval/starter_set.csv` (the starter set is only a template).
2. Run each candidate behind the AI service and score it:
   ```bash
   cd eval && python run_eval.py --set your_set.csv --label qwen3-32b
   ```
3. Pick the smallest model whose danger-sign recall clears your bar and which never reads a danger reply as "well". Add every phrase the model missed to the lexicon on the **Rules** page, so the rules catch it next time without the model.

## Production checklist

- [ ] A nurse or midwife reviews and signs off every danger sign, SLA, CHW guidance line and SMS in `db/002_seed_reference.sql` and `Services/Templates.cs`
- [ ] A Swahili speaker reviews all message wording
- [ ] Demo settings off; strong `Security:PhoneHashKey`; key folder backed up
- [ ] HTTPS, and a managed PostgreSQL with backups
- [ ] Registration with the Office of the Data Protection Commissioner; data handling aligned with the Data Protection Act 2019 and Digital Health Act 2023
- [ ] Ethics approval before any pilot with real patients (usability tests with test personas do not need it)
- [ ] SMS one-time PIN login for CHWs (password login is used in the prototype)

## Roadmap

Document intelligence for ANC booklets, Swahili voice (text-to-speech and IVR) for low-literacy mothers, postnatal and newborn pathways to the first 1,000 days, a live DHIS2 / KenyaEMR link through the FHIR-shaped tables, and moving message templates into the database.

## Licence

Code: MIT. Message content, guidance and lexicon: CC BY 4.0. Font: SIL Open Font License 1.1.
