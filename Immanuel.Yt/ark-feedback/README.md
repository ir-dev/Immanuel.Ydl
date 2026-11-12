# ark-feedback — ARK-RAPID comment sentiment

Paste a YouTube link, get a report on what the comments say: sentiment (positive / neutral / negative), the languages
people wrote in, the themes they talk about, the voices that matter (most liked, most positive, most negative, the
creator's own replies), mood over time, toxicity and a 28-emotion mix — and who the video is for: its **genre**, the
audience's **geographic reach** and **age group**, and a **parents' & teachers' guide** that says whether the comment
section is fit to share and what a class could do with the video. Everything runs on the server's CPU with open
models; nothing is sent to a cloud AI.

ASP.NET Core MVC (.NET 10) serving a responsive single-page app. Comments come from yt-dlp (no API key) or the YouTube
Data API v3 (when a key is configured). Every request, analysis, scored comment and "was this useful?" answer is written
to an audit log through **Ark.Rapid.Database**: one SQLite file per month, a table set per day.

## Run it

```
cd src/Ark.Feedback
dotnet run --launch-profile http          # http://localhost:5090
```

First start downloads, SHA-256 verified, into `App_Data/`:

| What | From | Size |
|---|---|---|
| yt-dlp | github.com/yt-dlp/yt-dlp (latest release, through the API) | 35 MB |
| XLM-T sentiment (XLM-RoBERTa base, 8-bit) | onnx-community/twitter-xlm-roberta-base-sentiment-ONNX | 279 MB |
| DistilBERT multilingual sentiment (8-bit) | Xenova/distilbert-base-multilingual-cased-sentiments-student | 136 MB |
| MiniLM multilingual sentence embeddings (8-bit) | sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2 | 118 MB |
| DistilBERT multilingual toxicity (8-bit) | onnx-community/distilbert-multilingual-toxicity-classifier-ONNX | 136 MB |
| Multilingual GoEmotions, mBERT (8-bit) | jdp8/multilingual_go_emotions-ONNX | 179 MB |
| DB-IP country lite (GeoIP for the audit log) | download.db-ip.com | 10 MB |

fastText lid.176 (language identification, 176 languages) ships inside the Panlingo NuGet package.

The page works before the downloads finish: analyses queue and start as soon as the models are on disk. The admin
console at `/admin` shows every download, the audit writer and the CPU settings.

## Who is the video for? (schema 2, 1.1.0)

Every report opens with an **At a glance** strip (verdict · genre · reach · age group · parents & teachers), an
**In short** paragraph a parent can read in ten seconds, a **Who is watching** card and a **For parents & teachers**
card. None of it needs a model: it is word lists and arithmetic over the comments and the video's own labels
(`Analysis/AudienceProfiler.cs`, lists in `Analysis/Lexicons*.cs`), so it adds ~0.2 s to an 800-comment report and
is also computed on the fly when an older (schema 1) report is opened.

| Section | What it reads | What it says |
|---|---|---|
| **Genre** | title, tags, description, channel name, YouTube category, duration, and which genre words the comments use (30 genres, multilingual) | the genre with a confidence bar, up to two secondary genres, the format (Short / clip / standard / long-form / feature-length / live replay) and the evidence |
| **Geographic reach** | comment languages (fastText, only confident guesses on comments long enough to guess), flag emoji, "love from …" phrases, country / city / demonym mentions (190 countries, native names included) and places the video itself names | a reach label (global / multi-regional / regional / mostly one country / wide English-speaking / hard to place), a 0–100 spread score, regions with shares, countries split into *from here* (flags, "from …") and *mentions*, and an honest note on how many comments could be placed |
| **Age group** | a prior per genre, then what commenters say about themselves: stated ages ("I'm 14", "born in 1998", in 12 languages), parents watching with a child, teachers using it with a class, school / college / work-and-marriage cues, teen slang, nostalgia, swearing, comment length, YouTube's 18+ flag, kids' words in the title | five bands (2–6, 7–12, 13–17, 18–24, 25+) with shares, the leading band or span, a confidence, the stated-age median and the evidence |
| **Parents & teachers** | swearing (multilingual, masked in the report), toxicity (Deep profile), adult / gambling / substances / violence / dangerous stunts / scam / mental-health / disputed-accuracy / religion-and-politics mentions, learning words, warm words, questions, polarisation, duration | a green / amber / red rating with reasons, a printable checklist, a 0–100 learning signal with example quotes, "things to check" with counts, suggested classroom uses, conversation starters, language exposure and a disclaimer that it reads comments, not the video |

The audit row of every analysis carries the same answers (`genre`, `reach`, `regions`, `countries`, `age_group`,
`age_bands`, `stated_ages`, `family_rating`, `family_reasons`, `learning_score`, `profanity_pct`, `red_flags`,
`summary`); day tables created by 1.0.0 get the new columns added in place on first use.

## Profiles — the CPU dial

| Profile | Models | Comments | Typical cost on one core |
|---|---|---|---|
| Quick | DistilBERT sentiment + fastText | 300 | ~15 ms / comment |
| Standard (default) | XLM-T sentiment + fastText + MiniLM themes | 600 | ~65 ms / comment |
| Deep | Standard + toxicity + emotions | 800 | ~120 ms / comment |

`Feedback:Analysis` in `appsettings.json` holds the rest of the dial: `InferenceThreads` (0 = half the cores, at most 4),
`MaxConcurrentAnalyses` (1 on shared hosting), `TimeBudgetSeconds` (when all comments would take longer, a
like-weighted, time-spread **sample** is scored and the report says so), `UnloadIdleMinutes` (models are released after
idle, giving memory back), `CacheHours` (a report for the same video and profile is reused), `MaxComments`,
`AllowedProfiles`. ONNX Runtime is set to never spin-wait between batches, which is what shared hosts penalise.

## Audit log: monthly files, daily tables

`App_Data/audit/ark-feedback-audit-2026-11.db` holds, for every day of the month, `requests_20261110`,
`analyses_20261110` and `comments_20261110`, plus `audit_days` (which days exist, with counts) and `audit_meta`
(schema version). An analysis row records **who asked** (IP, country, device, browser, signed-in user), **for what**
(video, channel, views, likes, category, tags), **what the sentiment is** (counts, percentages, score, likes-weighted
score, polarisation, verdict, languages, themes, keywords, emoji, toxicity, emotions, the pinned and top comments,
models and timings) and **whether the asker liked the report** (`liked`, `feedback_note`, `feedback_utc`,
`feedback_count`, `report_views`). Every scored comment is a row of `comments_*` (switch off with
`Feedback:Analysis:StoreComments`). Retention deletes whole month files (`Feedback:Audit:RetentionMonths`).

Schema, writer and queries follow the Ark.Rapid.Database implementation guide: `ColumnProp` dictionaries, `CREATE TABLE
IF NOT EXISTS` on first use, values only through `GetSqlValueAsync`, one transaction per batch per file.

## Admin console

`/admin` — sign in with `Feedback:Admin:Key` (16+ characters), with an Ark role from `Feedback:Admin:Roles` when
sign-in is on, or from localhost in Development. Tabs: Overview (KPIs vs the previous period, daily lines, verdicts,
languages, countries, devices, most analysed videos, request heatmap), Analyses (searchable log, click a row for the full
audit row, its scored comments and its requests), Feedback (the thumbs and notes), Requests, Storage (every month file
and day table with counts and CSV export), System (models, engine, YouTube access, audit writer, GeoIP, server).

## Configuration that matters

| Setting | Why |
|---|---|
| `Feedback:YouTube:ApiKey` | Use the official Data API (exact totals, fast). yt-dlp stays as the fallback. |
| `Feedback:Tools:JsRuntimePath` | A deno/node for yt-dlp. Optional for comments; silences a warning. |
| `Feedback:Audit:TimeZone` | Which day a table belongs to. |
| `Feedback:Audit:IpMode` | Full, Masked or Hashed IPs in the log. |
| `Feedback:Analysis:Models:<role>` | Swap a model: `Repo`, `File`, `Sha256`, `Enabled`. |
| `ark_oauth_client:Authority` + `ClientId` | Require Ark sign-in for every page and call. |

Secrets go in `appsettings.Production.json` (git-ignored) or environment variables (`Feedback__Admin__Key`).

## Deploy

`Dockerfile` builds a Linux image for App Service (`docker build -t ark-feedback .`). For Windows IIS publish
framework-dependent `win-x64` (`dotnet publish -c Release -r win-x64 --self-contained false`); the app pool must be
**64-bit** (ONNX Runtime, SQLite and fastText ship native x64 libraries), must not idle out, and should allow a long
start (the first model download). Point `Feedback:DataRoot` at fast local disk.

## Tests

```
dotnet test
```
Unit tests cover URL parsing, the WordPiece and XLM-R tokenizers (against Hugging Face reference ids when the model
files are present), sampling, the report builder, the audit schema/writer against an in-memory SQLite, and the HTTP API
with the real app booted in-process.
