-- Reference data. Idempotent.
-- CLINICAL CONTENT IS A STARTING DRAFT: severities, SLAs, CHW guidance and the
-- advice SMS must be reviewed and signed off by a nurse or midwife before any
-- use with real patients. The lexicon must be extended with CHWs from real
-- replies (Sheng spellings vary by area and change quickly).

INSERT INTO facilities (name, level, county, phone) VALUES
    ('Mwangaza Dispensary',          'Dispensary (level 2)',            'Nairobi', '+254700100201'),
    ('Upendo Health Centre',         'Health centre (level 3)',         'Nairobi', '+254700100202'),
    ('Sub-County Referral Hospital', 'Sub-county hospital (level 4)',   'Nairobi', '+254700100203')
ON CONFLICT (name) DO NOTHING;

INSERT INTO danger_signs (code, name_en, name_sw, severity, sla_minutes, menu_option, chw_guidance, advice_sw, advice_en, sort_order) VALUES
('BLEED',    'Vaginal bleeding',                 'Kutokwa na damu',                 'high',   120, 2,
 'Any bleeding in pregnancy is urgent. Call her now; if bleeding is heavy, arrange transport to a facility straight away.',
 'Mhudumu wako wa afya amearifiwa na atakupigia. Ikiwa damu ni nyingi, nenda kituo cha afya kilicho karibu SASA. Usisubiri.',
 'Your health worker has been alerted and will call you. If the bleeding is heavy, go to the nearest health facility NOW. Do not wait.', 10),
('CONVULS',  'Convulsions or fits',              'Degedege au kifafa',              'high',    60, NULL,
 'Fits in pregnancy can mean eclampsia, an emergency. Arrange emergency transport now.',
 'Hii ni dharura. Mpeleke kituo cha afya kilicho karibu SASA. Mhudumu wa afya amearifiwa.',
 'This is an emergency. Go to the nearest health facility NOW. Your health worker has been alerted.', 20),
('HEADVIS',  'Severe headache or blurred vision','Kichwa kuuma sana au kuona giza', 'high',   240, 3,
 'Possible pre-eclampsia. Check blood pressure; 140/90 or higher needs same-day referral.',
 'Mhudumu wako wa afya amearifiwa na atakupigia leo. Ukiona giza zaidi au kupata degedege, nenda kituo cha afya mara moja.',
 'Your health worker has been alerted and will call you today. If your vision gets worse or you have fits, go to a health facility at once.', 30),
('FETALMOV', 'Baby moving less or not at all',   'Mtoto hachezi tumboni',           'high',   240, 4,
 'Ask when she last felt movement. Fewer movements than usual after 28 weeks needs same-day assessment.',
 'Mhudumu wako wa afya amearifiwa na atakupigia leo. Lala kwa upande wa kushoto na uhesabu mtoto anavyocheza.',
 'Your health worker has been alerted and will call you today. Lie on your left side and count your baby''s movements.', 40),
('ABDPAIN',  'Severe abdominal pain',            'Maumivu makali ya tumbo',         'high',   240, 7,
 'Severe or constant pain can signal placental abruption or ectopic pregnancy. Arrange urgent review.',
 'Mhudumu wako wa afya amearifiwa na atakupigia. Ikiwa maumivu yanazidi, nenda kituo cha afya mara moja.',
 'Your health worker has been alerted and will call you. If the pain gets worse, go to a health facility at once.', 50),
('FLUID',    'Fluid leaking from the vagina',    'Maji kutoka ukeni',               'high',   240, NULL,
 'Leaking fluid before labour risks infection. Refer for same-day assessment.',
 'Mhudumu wako wa afya amearifiwa na atakupigia. Nenda kituo cha afya leo.',
 'Your health worker has been alerted and will call you. Please go to a health facility today.', 60),
('BREATH',   'Difficulty breathing',             'Shida ya kupumua',                'high',   120, NULL,
 'Difficulty breathing needs urgent facility assessment.',
 'Mhudumu wako wa afya amearifiwa. Ikiwa unashindwa kupumua, nenda kituo cha afya SASA.',
 'Your health worker has been alerted. If you are struggling to breathe, go to a health facility NOW.', 70),
('FEVER',    'Fever',                            'Homa',                            'medium', 720, 5,
 'Take her temperature. 38°C or higher in pregnancy needs assessment at a facility.',
 NULL, NULL, 80),
('SWELL',    'Swelling of face or hands',        'Kuvimba uso au mikono',           'medium', 720, 6,
 'Check blood pressure and whether the swelling is in the face or hands, not only the feet.',
 NULL, NULL, 90),
('UNCLEAR',  'Reply needs a human to read',      'Jibu linahitaji kusomwa',         'low',   1440, 8,
 'The reply could not be read with confidence. Call her to check how she is.',
 NULL, NULL, 100)
ON CONFLICT (code) DO NOTHING;

-- Negation words: a danger phrase directly after one of these is not counted
-- ("sina damu" = I have no bleeding). Negated replies never count as "well" either;
-- they go to the model or to a human.
INSERT INTO lexicon (kind, sign_code, phrase, language) VALUES
('negation', NULL, 'no', 'en'), ('negation', NULL, 'not', 'en'), ('negation', NULL, 'never', 'en'), ('negation', NULL, 'without', 'en'),
('negation', NULL, 'sina', 'sw'), ('negation', NULL, 'hakuna', 'sw'), ('negation', NULL, 'hana', 'sw'),
('negation', NULL, 'sijaona', 'sw'), ('negation', NULL, 'bila', 'sw'), ('negation', NULL, 'si', 'sw')
ON CONFLICT DO NOTHING;

INSERT INTO lexicon (kind, sign_code, phrase, language) VALUES
-- well
('well', NULL, 'fine', 'en'), ('well', NULL, 'ok', 'en'), ('well', NULL, 'okay', 'en'), ('well', NULL, 'good', 'en'),
('well', NULL, 'well', 'en'), ('well', NULL, 'all good', 'en'), ('well', NULL, 'feeling good', 'en'), ('well', NULL, 'im fine', 'en'),
('well', NULL, 'sawa', 'sw'), ('well', NULL, 'niko sawa', 'sw'), ('well', NULL, 'salama', 'sw'), ('well', NULL, 'mzima', 'sw'),
('well', NULL, 'nzuri', 'sw'), ('well', NULL, 'mzuri', 'sw'), ('well', NULL, 'sina shida', 'sw'), ('well', NULL, 'hakuna shida', 'sw'),
('well', NULL, 'tuko sawa', 'sw'), ('well', NULL, 'shwari', 'sw'), ('well', NULL, 'asante', 'sw'),
('well', NULL, 'poa', 'sheng'), ('well', NULL, 'niko poa', 'sheng'), ('well', NULL, 'fiti', 'sheng'), ('well', NULL, 'niko fiti', 'sheng'),
('well', NULL, 'safi', 'sheng'), ('well', NULL, 'freshi', 'sheng'), ('well', NULL, 'niko freshi', 'sheng'), ('well', NULL, 'mambo ni poa', 'sheng'),
-- bleeding
('danger', 'BLEED', 'bleeding', 'en'), ('danger', 'BLEED', 'blood', 'en'), ('danger', 'BLEED', 'bleed', 'en'), ('danger', 'BLEED', 'spotting', 'en'),
('danger', 'BLEED', 'damu', 'sw'), ('danger', 'BLEED', 'kutokwa na damu', 'sw'), ('danger', 'BLEED', 'natoka damu', 'sw'),
('danger', 'BLEED', 'blood inatoka', 'sheng'), ('danger', 'BLEED', 'natoka blood', 'sheng'), ('danger', 'BLEED', 'nina bleed', 'sheng'),
-- convulsions
('danger', 'CONVULS', 'fits', 'en'), ('danger', 'CONVULS', 'convulsion', 'en'), ('danger', 'CONVULS', 'convulsions', 'en'), ('danger', 'CONVULS', 'seizure', 'en'),
('danger', 'CONVULS', 'degedege', 'sw'), ('danger', 'CONVULS', 'kifafa', 'sw'), ('danger', 'CONVULS', 'alianguka akitetemeka', 'sw'),
-- headache / vision
('danger', 'HEADVIS', 'headache', 'en'), ('danger', 'HEADVIS', 'severe headache', 'en'), ('danger', 'HEADVIS', 'blurred vision', 'en'),
('danger', 'HEADVIS', 'blurry', 'en'), ('danger', 'HEADVIS', 'head is paining', 'en'), ('danger', 'HEADVIS', 'head paining', 'en'), ('danger', 'HEADVIS', 'head is aching', 'en'), ('danger', 'HEADVIS', 'seeing stars', 'en'), ('danger', 'HEADVIS', 'cant see well', 'en'),
('danger', 'HEADVIS', 'kichwa kinauma', 'sw'), ('danger', 'HEADVIS', 'kichwa inauma', 'sw'), ('danger', 'HEADVIS', 'maumivu ya kichwa', 'sw'),
('danger', 'HEADVIS', 'kuumwa na kichwa', 'sw'), ('danger', 'HEADVIS', 'naona giza', 'sw'), ('danger', 'HEADVIS', 'sioni vizuri', 'sw'),
('danger', 'HEADVIS', 'macho yanaona giza', 'sw'), ('danger', 'HEADVIS', 'kichwa inaniuma', 'sheng'), ('danger', 'HEADVIS', 'head inauma', 'sheng'),
('danger', 'HEADVIS', 'kichwa inadunda', 'sheng'), ('danger', 'HEADVIS', 'macho iko blurry', 'sheng'),
-- fetal movement
('danger', 'FETALMOV', 'baby not moving', 'en'), ('danger', 'FETALMOV', 'baby is not moving', 'en'), ('danger', 'FETALMOV', 'no movement', 'en'),
('danger', 'FETALMOV', 'baby moving less', 'en'), ('danger', 'FETALMOV', 'not kicking', 'en'),
('danger', 'FETALMOV', 'hachezi', 'sw'), ('danger', 'FETALMOV', 'mtoto hachezi', 'sw'), ('danger', 'FETALMOV', 'hasongi', 'sw'),
('danger', 'FETALMOV', 'hajacheza', 'sw'), ('danger', 'FETALMOV', 'mtoto hasogei', 'sw'),
('danger', 'FETALMOV', 'baby hachezi', 'sheng'), ('danger', 'FETALMOV', 'beby hachezi', 'sheng'), ('danger', 'FETALMOV', 'baby haikicki', 'sheng'),
-- abdominal pain
('danger', 'ABDPAIN', 'stomach pain', 'en'), ('danger', 'ABDPAIN', 'abdominal pain', 'en'), ('danger', 'ABDPAIN', 'severe pain', 'en'),
('danger', 'ABDPAIN', 'tumbo inauma sana', 'sw'), ('danger', 'ABDPAIN', 'tumbo linauma sana', 'sw'), ('danger', 'ABDPAIN', 'maumivu makali ya tumbo', 'sw'),
('danger', 'ABDPAIN', 'maumivu makali', 'sw'), ('danger', 'ABDPAIN', 'stomach inauma', 'sheng'), ('danger', 'ABDPAIN', 'tumbo inaniua', 'sheng'),
-- fluid
('danger', 'FLUID', 'water broke', 'en'), ('danger', 'FLUID', 'waters broke', 'en'), ('danger', 'FLUID', 'leaking water', 'en'), ('danger', 'FLUID', 'leaking fluid', 'en'),
('danger', 'FLUID', 'maji yanatoka', 'sw'), ('danger', 'FLUID', 'maji imevunja', 'sw'), ('danger', 'FLUID', 'maji yamevunja', 'sw'),
('danger', 'FLUID', 'chupa imepasuka', 'sw'), ('danger', 'FLUID', 'chupa kupasuka', 'sw'), ('danger', 'FLUID', 'maji inatoka', 'sheng'),
-- breathing
('danger', 'BREATH', 'difficulty breathing', 'en'), ('danger', 'BREATH', 'cant breathe', 'en'), ('danger', 'BREATH', 'short of breath', 'en'),
('danger', 'BREATH', 'nashindwa kupumua', 'sw'), ('danger', 'BREATH', 'shida ya kupumua', 'sw'), ('danger', 'BREATH', 'sipumui vizuri', 'sw'),
('danger', 'BREATH', 'pumzi inakata', 'sheng'),
-- fever
('danger', 'FEVER', 'fever', 'en'), ('danger', 'FEVER', 'hot body', 'en'), ('danger', 'FEVER', 'high temperature', 'en'),
('danger', 'FEVER', 'homa', 'sw'), ('danger', 'FEVER', 'nina joto', 'sw'), ('danger', 'FEVER', 'mwili moto', 'sw'), ('danger', 'FEVER', 'mwili una joto', 'sw'),
('danger', 'FEVER', 'mwili inachemka', 'sheng'), ('danger', 'FEVER', 'niko na fever', 'sheng'),
-- swelling
('danger', 'SWELL', 'swelling', 'en'), ('danger', 'SWELL', 'swollen face', 'en'), ('danger', 'SWELL', 'swollen hands', 'en'), ('danger', 'SWELL', 'face is swollen', 'en'),
('danger', 'SWELL', 'kuvimba', 'sw'), ('danger', 'SWELL', 'nimevimba', 'sw'), ('danger', 'SWELL', 'uso umevimba', 'sw'), ('danger', 'SWELL', 'mikono imevimba', 'sw'),
('danger', 'SWELL', 'uso imevimba', 'sheng'), ('danger', 'SWELL', 'face imeswell', 'sheng')
ON CONFLICT DO NOTHING;
