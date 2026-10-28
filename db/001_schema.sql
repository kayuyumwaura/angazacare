-- Angaza Care Maternal: schema
-- Idempotent: safe to run on every start. Table shapes follow FHIR R4 resources
-- (noted per table) so a DHIS2 / KenyaEMR link later is a mapping job.

CREATE TABLE IF NOT EXISTS schema_version (
    version     int PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

-- FHIR Organization
CREATE TABLE IF NOT EXISTS facilities (
    id          serial PRIMARY KEY,
    name        text NOT NULL UNIQUE,
    level       text NOT NULL,              -- dispensary, health centre, sub-county hospital...
    county      text NOT NULL,
    phone       text
);

-- FHIR Practitioner
CREATE TABLE IF NOT EXISTS users (
    id             serial PRIMARY KEY,
    full_name      text NOT NULL,
    phone          text NOT NULL UNIQUE,     -- staff phone, used to log in and for alerts
    role           text NOT NULL CHECK (role IN ('chw','nurse','admin')),
    facility_id    int REFERENCES facilities(id),
    password_hash  text NOT NULL,
    active         boolean NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now()
);

-- FHIR Patient. Phone is encrypted at rest; phone_hash (HMAC) is used for lookup.
CREATE TABLE IF NOT EXISTS mothers (
    id               serial PRIMARY KEY,
    display_name     text NOT NULL,          -- first name only
    phone_enc        text NOT NULL,
    phone_hash       text NOT NULL UNIQUE,
    phone_last3      text NOT NULL,          -- for staff recognition without exposing the number
    language         text NOT NULL DEFAULT 'sw' CHECK (language IN ('sw','en')),
    village          text NOT NULL,
    chw_id           int NOT NULL REFERENCES users(id),
    discreet         boolean NOT NULL DEFAULT false,
    consent_version  text NOT NULL,
    consent_at       timestamptz NOT NULL,
    opted_out_at     timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now()
);

-- FHIR EpisodeOfCare
CREATE TABLE IF NOT EXISTS pregnancies (
    id                     serial PRIMARY KEY,
    mother_id              int NOT NULL REFERENCES mothers(id),
    edd                    date NOT NULL,
    lmp                    date,
    status                 text NOT NULL DEFAULT 'active' CHECK (status IN ('active','delivered','closed')),
    next_check_in_at       timestamptz,
    next_anc_date          date,
    anc_reminder_sent_for  date,
    created_at             timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_one_active_pregnancy ON pregnancies(mother_id) WHERE status = 'active';

-- Danger-sign rules. Severity, SLA and the immediate advice are clinical
-- decisions owned by the nurse lead, not the model.
CREATE TABLE IF NOT EXISTS danger_signs (
    code              text PRIMARY KEY,
    name_en           text NOT NULL,
    name_sw           text NOT NULL,
    severity          text NOT NULL CHECK (severity IN ('high','medium','low')),
    sla_minutes       int  NOT NULL CHECK (sla_minutes > 0),
    menu_option       int UNIQUE,            -- reply number on the SMS / USSD menu
    chw_guidance      text NOT NULL,
    advice_sw         text,                  -- pre-approved SMS sent at once for high severity
    advice_en         text,
    sort_order        int NOT NULL DEFAULT 100
);

-- Words and phrases (English, Swahili, Sheng) mapped to danger signs or to "well".
-- Matched by the rules engine before any model is called.
CREATE TABLE IF NOT EXISTS lexicon (
    id          serial PRIMARY KEY,
    kind        text NOT NULL CHECK (kind IN ('danger','well','negation')),
    sign_code   text REFERENCES danger_signs(code),
    phrase      text NOT NULL,
    language    text NOT NULL CHECK (language IN ('en','sw','sheng')),
    added_by    int REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE NULLS NOT DISTINCT (kind, sign_code, phrase),
    CHECK ((kind = 'danger') = (sign_code IS NOT NULL))
);

-- FHIR Communication (scheduled outreach)
CREATE TABLE IF NOT EXISTS check_ins (
    id             serial PRIMARY KEY,
    pregnancy_id   int NOT NULL REFERENCES pregnancies(id),
    kind           text NOT NULL CHECK (kind IN ('check_in','anc_reminder')),
    gestation_wk   int,
    sent_at        timestamptz NOT NULL DEFAULT now(),
    replied_at     timestamptz
);

-- Every SMS / USSD message in or out (FHIR Communication / QuestionnaireResponse)
CREATE TABLE IF NOT EXISTS messages (
    id              bigserial PRIMARY KEY,
    mother_id       int REFERENCES mothers(id),
    user_id         int REFERENCES users(id),      -- staff recipient for alerts
    check_in_id     int REFERENCES check_ins(id),
    direction       text NOT NULL CHECK (direction IN ('in','out')),
    channel         text NOT NULL CHECK (channel IN ('sms','ussd')),
    purpose         text NOT NULL,                 -- check_in, reply, ack, advice, referral, alert, reminder, system
    body            text NOT NULL,
    gateway_id      text,
    gateway_status  text,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_messages_mother ON messages(mother_id, created_at);

-- FHIR RiskAssessment: one row per scored reply, including model provenance
CREATE TABLE IF NOT EXISTS assessments (
    id              bigserial PRIMARY KEY,
    message_id      bigint NOT NULL REFERENCES messages(id),
    outcome         text NOT NULL CHECK (outcome IN ('well','danger','unclear')),
    method          text NOT NULL CHECK (method IN ('menu','lexicon','llm','fallback')),
    signs           text[] NOT NULL DEFAULT '{}',
    matched         text[] NOT NULL DEFAULT '{}',  -- phrases or evidence that produced the signs
    confidence      numeric(4,3),
    model           text,
    prompt_version  text,
    raw_output      jsonb,
    latency_ms      int,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- FHIR Flag
CREATE TABLE IF NOT EXISTS flags (
    id                  serial PRIMARY KEY,
    pregnancy_id        int NOT NULL REFERENCES pregnancies(id),
    mother_id           int NOT NULL REFERENCES mothers(id),
    chw_id              int NOT NULL REFERENCES users(id),
    source              text NOT NULL CHECK (source IN ('engine','nurse','chw')),
    first_message_id    bigint REFERENCES messages(id),
    signs               text[] NOT NULL,
    severity            text NOT NULL CHECK (severity IN ('high','medium','low')),
    state               text NOT NULL CHECK (state IN ('open','acknowledged','assessed','escalated','approved','rejected')),
    sla_due_at          timestamptz NOT NULL,
    chw_findings        text,
    bp_systolic         int,
    bp_diastolic        int,
    temperature_c       numeric(4,1),
    chw_recommendation  text CHECK (chw_recommendation IN ('refer','close')),
    acknowledged_at     timestamptz,
    assessed_at         timestamptz,
    escalated_at        timestamptz,
    decided_at          timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_one_live_flag ON flags(pregnancy_id)
    WHERE state IN ('open','acknowledged','assessed','escalated');

-- FHIR Provenance: append-only audit trail of every flag change
CREATE TABLE IF NOT EXISTS flag_events (
    id           bigserial PRIMARY KEY,
    flag_id      int NOT NULL REFERENCES flags(id),
    actor_id     int REFERENCES users(id),       -- null = system
    from_state   text,
    to_state     text NOT NULL,
    note         text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_flag_events_flag ON flag_events(flag_id, created_at);

CREATE OR REPLACE FUNCTION forbid_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'flag_events is append-only';
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_flag_events_append_only ON flag_events;
CREATE TRIGGER trg_flag_events_append_only
    BEFORE UPDATE OR DELETE ON flag_events
    FOR EACH ROW EXECUTE FUNCTION forbid_change();

-- FHIR ServiceRequest
CREATE TABLE IF NOT EXISTS referrals (
    id            serial PRIMARY KEY,
    flag_id       int NOT NULL UNIQUE REFERENCES flags(id),
    facility_id   int NOT NULL REFERENCES facilities(id),
    urgency       text NOT NULL CHECK (urgency IN ('emergency','same_day','routine')),
    approved_by   int NOT NULL REFERENCES users(id),
    nurse_note    text,
    sms_sent_at   timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now()
);

INSERT INTO schema_version(version) VALUES (1) ON CONFLICT DO NOTHING;
