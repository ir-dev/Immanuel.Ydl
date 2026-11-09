<p align="center"><img src="src/Ark.Ydl/wwwroot/img/logo.svg" alt="ARK-RAPID video downloader" width="240"></p>

# ark-ydl

**ARK-RAPID** is the product name users see; `ark-ydl` is the code base. Paste a YouTube link, download it the way you want. ASP.NET Core (.NET 10) on top of
[yt-dlp](https://github.com/yt-dlp/yt-dlp) and ffmpeg. This is the current-generation replacement
for `Immanuel.Yl` (2017) and `Immanuel.Yt.Cmments.Feedback` (2020) in this repo.

## Why the old versions stopped working, and why this one keeps working

The old apps extracted YouTube's stream URLs themselves (`YoutubeExtractor`, `VideoLibrary`).
YouTube changes its player, its signature cipher and its anti-bot checks every few weeks, and since
late 2025 extraction needs a JavaScript runtime to solve the player's challenges. Code that does the
extraction in-process breaks with the first change and stays broken.

ark-ydl delegates extraction to **yt-dlp**, which is maintained daily, and:

- finds yt-dlp, or downloads the official release and **verifies its SHA-256**;
- **updates yt-dlp itself** on start and every 24 h (`yt-dlp -U`) when the binary is its own;
- passes a **JavaScript runtime** (deno, else node ≥ 22) via `--js-runtimes`, and downloads deno
  itself when the machine has none, so it also runs on shared hosting where nothing can be installed;
- turns yt-dlp's errors into sentences a user can act on (bot check, private, age-restricted, …).

## What a user can do

| | |
|---|---|
| **Paste & go** | Paste anywhere on the page (Ctrl/⌘+V), drag a link in, use the Paste button, or open `/?url=…`. Accepts watch, `youtu.be`, Shorts, live, embed, YouTube Music, playlists, channels, bare video ids. |
| **Video** | Every quality the video has (144p–8K) with a size estimate. Codec: *Smart* (H.264 when it exists at that resolution, so it plays everywhere), *Best efficiency* (AV1/VP9), or forced H.264/VP9/AV1. MP4, MKV or WebM. Keep or avoid HDR. |
| **Audio only** | MP3, M4A (AAC), Opus, FLAC, WAV, OGG or the original stream; best-VBR or 96–320 kbps; cover art (optionally cropped square); smart "Artist - Title" tags; album/track number from a playlist. |
| **A clip** | Embedded player with a timeline: YouTube's *most replayed* heatmap, chapter ticks, draggable handles, exact time boxes, "set from player", preview the clip, several ranges in one go, precise (re-encoded) or fast (keyframe) cuts. Links with `t=1m30s` / `start=` / `end=` open straight into clip mode. |
| **Chapters** | Pick chapters, each becomes its own file named after it; pick all to split the whole video (albums, courses). |
| **Playlists & channels** | List with thumbnails, filter, select all/none, shift-click ranges; video or audio for each; numbered files; save one by one or as a ZIP. |
| **Subtitles** | Creator-uploaded and auto-generated (plus auto-translated) languages; embed in the video, `.srt` files, or both; or download subtitles only. |
| **Extras** | Thumbnail as JPG, SponsorBlock (cut out or mark sponsors, intros, reminders…), embedded metadata/chapters, file-name styles. |
| **Downloads panel** | Live progress over Server-Sent Events (stage, %, speed, ETA, item *n* of *m*), cancel, retry, in-page preview, auto-save when ready, browser notification when the tab is in the background; the list survives a reload. |
| **Everywhere** | Light/dark, mobile layout, installable (PWA) with an Android **share target** (share from the YouTube app), a bookmarklet, and *swap the domain*: replace `youtube.com` with this site's host in any YouTube URL. |

Choices (format, quality, codec, toggles…) are remembered in the browser.

## Run it locally

Prerequisites: .NET 10 SDK. Nothing else has to be installed.
On first start the app downloads what it is missing into `src/Ark.Ydl/App_Data/tools`, checking the
SHA-256 of each file:

* **yt-dlp**: the official release, if it is not on `PATH`, fetched through the GitHub REST API like
  ffmpeg's below. The app keeps it updated.
* **ffmpeg + ffprobe**: a self-contained static build (in `tools/ffmpeg/`), used when no ffmpeg on
  this machine works. macOS and Linux get [ffmpeg.martin-riedl.de](https://ffmpeg.martin-riedl.de)
  release builds (arm64 and x64); Windows gets [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds),
  fetched through the GitHub REST API (github.com's `/releases/download/latest/` route times out with
  504 for that repo), then on x64 falls back to [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)'s release
  essentials build. Each request is retried on a timeout or a 408/429/5xx answer. If every source fails,
  the app tries again in the background (10 min, then doubling, up to every 6 h) until one works.
  It takes about 60 MB (about 200 MB on Windows), once. Delete the folder to fetch a newer build.
* **deno** (in `tools/deno/`): the JavaScript runtime yt-dlp needs for YouTube, used when no deno ≥ 2.3
  or node ≥ 22 works on this machine. It comes from [dl.deno.land](https://dl.deno.land), the server
  deno's own install scripts use (not github.com), with the `.zip.sha256sum` beside each file. It is a
  single file: a 43 MB download, about 100 MB on disk. It is retried in the background like ffmpeg.

```bash
dotnet run --project src/Ark.Ydl          # http://localhost:5080
dotnet test                               # 279 tests
ARK_YDL_GEOIP_LIVE=1 dotnet test --filter GeoIpLiveTests   # also downloads the real DB-IP file (4 MB) and looks IPs up
```

Locally (the Development environment, a request from this machine) the admin console at
`http://localhost:5080/admin` opens without a key, with a banner saying so.

In VS Code, **F5** (`ark-ydl (http)`, or `ark-ydl (https)` after `dotnet dev-certs https --trust`)
builds, starts the app under the debugger with the `Properties/launchSettings.json` profile and opens
the browser. `.vscode/` works whether you open this folder or the repo root, and also carries
build / test / watch / "publish for IIS (win-x64)" tasks.

`GET /api/system` (or the pill in the top bar) shows which yt-dlp / ffmpeg / JS runtime were found.
A broken install is reported with the reason. A Homebrew ffmpeg whose libraries were removed by an
upgrade fails with `Library not loaded …`. The app then skips it and uses its own copy. Lookup
order: `Tools:FfmpegPath`, then `tools/ffmpeg/`, then `PATH`, then download.

## Run it in a container

```bash
docker build -t ark-ydl .
docker run --rm -p 8080:8080 ark-ydl      # http://localhost:8080
```

The image is `aspnet:10.0-noble` + deno 2.9 + the official yt-dlp build (`--build-arg
YTDLP_VERSION=2026.10.26` pins one) + the static ffmpeg/ffprobe release build the app would download
anyway (`--build-arg FFMPEG_CHANNEL=snapshot` for the latest master). Both are checksum-verified
and baked into the tools folder `/opt/ark-ydl/tools`, so a container start downloads nothing. It runs
as the non-root `app` user, writes downloads to `/tmp/ark-ydl`, and has a `/healthz` health check.

### Azure App Service (custom container)

Push the image to ACR and point the web app at it; set `WEBSITES_PORT=8080`. Configuration is plain
environment variables (`Ydl__RetentionMinutes=60`, `Ydl__CookiesFile=/home/cookies.txt`, …). The
`aas-deploy` skill can generate the `az acr build` / slot commands.

## Shared hosting (Windows / IIS)

Publish with the "publish for IIS (win-x64)" task and upload it. Nothing has to be installed on the
server. On the first start the app fetches yt-dlp, ffmpeg and deno into `App_Data\tools` (about
320 MB in all) and keeps yt-dlp current. The page shows *Getting the download engine ready* until
that is done. What the host has to allow:

* **Write access to `App_Data`** for the app-pool account (or set `Ydl:DataRoot` to a folder it can
  write). Tools, downloads and caches all live there. deno's cache is pointed at `App_Data\cache\deno`,
  because an app-pool account usually has no user profile to keep one in.
* **Starting programs from that folder**, which yt-dlp needs anyway. A 32-bit app pool is fine: the
  tools run as their own 64-bit processes.
* **Outbound HTTPS** to `api.github.com` and `release-assets.githubusercontent.com` (yt-dlp,
  ffmpeg), `www.gyan.dev` (ffmpeg fallback) and `dl.deno.land` (deno). A failed download is retried in
  the background (10 min, then doubling, up to every 6 h), so a blip at start-up fixes itself.
  Optional: `download.db-ip.com` (GeoIP for the audit log, monthly) and `raw.githubusercontent.com`
  (the latest-release check). Neither failing affects downloads.
* **SQLite's native library** (`e_sqlite3`, part of the publish output) for the audit log. A win-x64
  publish needs a 64-bit app pool. If it can't load, downloads still work and `/admin` says why.

If the host blocks those downloads, upload the files yourself over FTP or the file manager. The app
picks them up on its next start, and ffmpeg and deno also at the next background retry:

```
App_Data\tools\yt-dlp.exe              github.com/yt-dlp/yt-dlp/releases          yt-dlp.exe
App_Data\tools\ffmpeg\ffmpeg.exe       gyan.dev ffmpeg-release-essentials.zip     bin\ffmpeg.exe
App_Data\tools\ffmpeg\ffprobe.exe        (same zip)                               bin\ffprobe.exe
App_Data\tools\deno\deno.exe           dl.deno.land/release/v2.9.8/deno-x86_64-pc-windows-msvc.zip
```

Files kept anywhere else are used through `Ydl:Tools:YtDlpPath`, `FfmpegPath` and `JsRuntimePath`.
Set them in `appsettings.json`, or as `<environmentVariable name="Ydl__Tools__JsRuntimePath"
value="…"/>` in `web.config`. A node ≥ 22 the host already provides works too (`JsRuntimePath` to its
`node.exe`). The older node many Windows hosts ship for iisnode is skipped. `/api/system` (or the pill
in the top bar) shows what was found, and when something is missing its `note` says why.

## Configuration (`Ydl` section)

| Key | Default | Meaning |
|---|---|---|
| `DataRoot` | `{ContentRoot}/App_Data` | Runtime data (downloads, tools, cache). |
| `DownloadRoot` | `{DataRoot}/downloads` | One folder per job; use local disk. |
| `MaxConcurrentJobs` | 2 | Downloads running at once; the rest queue (position shown). |
| `MaxConcurrentProbes` | 4 | Link look-ups at once. |
| `MaxActiveJobsPerClient` | 4 | Queued + running per IP (or signed-in user). |
| `RetentionMinutes` | 120 | Finished files are deleted after this. |
| `MaxPlaylistItems` | 100 | Entries listed and downloaded per playlist job. |
| `MaxClipSections` | 10 | Ranges per clip job. |
| `MaxDurationMinutes` | 0 | Refuse longer whole videos (0 = no limit). |
| `MaxFileSize` | – | yt-dlp `--max-filesize`, e.g. `4G`. |
| `ConcurrentFragments` | 4 | Parallel DASH/HLS fragments. |
| `AllowedSites` | YouTube hosts | Hosts users may paste. |
| `AllowAnySite` | false | Accept any yt-dlp site (private/loopback addresses are still refused). |
| `CookiesFile` | – | Netscape `cookies.txt` (see *Bot checks*). Each run gets a private copy. |
| `Proxy` | – | yt-dlp `--proxy`. |
| `ExtraArgs` | [] | Admin-only extra yt-dlp arguments, e.g. `["--extractor-args","youtube:player_client=default"]`. |
| `RateLimit:ProbesPerMinute` / `JobsPerMinute` | 30 / 20 | Per client. |
| `Tools:YtDlpPath` / `FfmpegPath` | auto | Explicit tool paths. |
| `Tools:JsRuntime` / `JsRuntimePath` | `auto` | `deno`, `node`, `bun`, `quickjs` or `none`. A `JsRuntimePath` is tried first; with `auto` its file name says which runtime it is (`deno.exe`, `node`, `qjs`). |
| `Tools:AutoDownloadJsRuntime` | true | Fetch deno into `{tools}/deno` when no runtime that works is found (only with `JsRuntime` `auto` or `deno`). |
| `Tools:DenoReleaseBase` / `DenoVersion` | `https://dl.deno.land/` / `latest` | deno download server (for a mirror) and version (`2.9.8` pins one). |
| `Tools:AutoDownloadYtDlp` | true | Fetch the official release when none is found. |
| `Tools:AutoDownloadFfmpeg` | true | Fetch a static ffmpeg + ffprobe into `{tools}/ffmpeg` when none that works is found. |
| `Tools:FfmpegReleaseBase` / `FfmpegWindowsReleaseBase` | martin-riedl.de / yt-dlp FFmpeg-Builds | Download sources (for a mirror). Empty `FfmpegWindowsReleaseBase` skips it. |
| `Tools:FfmpegWindowsFallbackUrl` | gyan.dev release essentials | Windows x64 fallback zip (`.sha256` beside it). Empty = none. |
| `Tools:AutoUpdateYtDlp` / `UpdateIntervalHours` | true / 24 | Self-update a yt-dlp this app owns. |
| `Audit:Enabled` | true | Keep the audit log (below). Off = nothing is written. |
| `Audit:LogRequests` / `IncludeStaticFiles` | true / false | Log every request, or downloads only; static files (scripts, icons) are left out unless asked for. |
| `Audit:ExcludePaths` | `["/healthz"]` | Path prefixes never logged. |
| `Audit:RetentionDays` | 365 | Raw rows older than this are deleted; the per-day totals stay. 0 = keep everything. |
| `Audit:IpMode` | `Full` | `Full`, `Masked` (last IPv4 octet / last 80 IPv6 bits zeroed) or `Hashed` (salted SHA-256). |
| `Audit:TimeZone` | `UTC` | IANA or Windows id deciding which day and hour a row counts towards. |
| `Audit:CaptureItemMetadata` | true | yt-dlp writes each item's genre, channel and duration to the job folder (`--print-to-file`). |
| `Audit:CountryHeader` | – | A header a trusted CDN sets with the country code (e.g. `CF-IPCountry`); used before GeoIP. |
| `Audit:GeoIp:Enabled` / `Edition` / `AutoDownload` | true / `city` / true | DB-IP lite into `{DataRoot}/geoip`, refreshed monthly. `country` is 4 MB instead of 60 MB. |
| `Audit:GeoIp:DatabasePath` | – | Your own `.mmdb` (DB-IP or MaxMind GeoLite2 layout); nothing is downloaded. |
| `Admin:Key` | – | Admin console key, 16+ characters. Unset = key sign-in off. |
| `Admin:Roles` | `["ark-ydl.admin"]` | With Ark sign-in on, these Ark claims open the console without the key. |
| `Admin:SessionHours` | 12 | How long a key sign-in lasts. |
| `Release:FeedUrl` | this repo's `releases.json` on master | Where "latest release" comes from. Empty = only the notes built into the app. |
| `Release:CheckIntervalHours` | 12 | How often the feed is read. |

`ArkDatabase:Databases:audit` (outside the `Ydl` section) picks the audit store: `Provider` `Sqlite`
(the tested one; `Maria`/`Postgres` exist in the library) and an optional `ConnectionString`, default
`Data Source={DataRoot}/audit/ark-ydl-audit.db`.

## Releases and versions

`src/Ark.Ydl/releases.json` is the release manifest: every version with its date, what changed, how
compatible it is with the one before (API, configuration, data, deployment, browsers; each
`compatible`, `config` or `breaking`) and the steps to upgrade to it. It also lists the two apps
this one replaced. It is embedded in the assembly, so a deployed build always carries its own notes.

The page shows a version chip in the footer: a green dot when up to date, gold with "v1.2.0
available" when the release feed has a newer one, and a reload prompt when the browser runs an
older copy of the page than the server (an installed app's cache). Clicking it opens *About this
release*. It shows the running version (and git revision), the version in `manifest.webmanifest`,
the latest release and where that came from, what changed, and a migration plan from any earlier
version. The plan chains every step in between, grouped for whoever runs the server and for users.
The chip needs only the small `release` block of `/api/system`. The notes are fetched when the
dialog opens, and the feed is read in the background, never during a request.

To cut a release, change the version in five places and add a `releases.json` entry:
`<Version>` in `Ark.Ydl.csproj`, `"current"` in `releases.json`, `"version"` in
`wwwroot/manifest.webmanifest`, `CLIENT_VERSION` in `wwwroot/js/version.js` and the cache name in
`wwwroot/sw.js`. `ReleaseTests` fails if any of them disagree, or if an entry is malformed.

## Audit log

Every request and every download is written to a local SQLite file,
`{DataRoot}/audit/ark-ydl-audit.db`, through [Ark.Rapid.Database](https://www.nuget.org/packages/Ark.Rapid.Database):

* **`audit_request`**, one row per request: time, method, path, kind (page, probe, job, file, admin…),
  status, duration, bytes, the video or job it was about, and who asked.
* **`audit_download`**, one row per download, updated as it runs: the link, video id, title, channel,
  **YouTube category (genre)**, tags, duration, views, upload date; what was asked for (mode,
  format, quality, codec, clip ranges, playlist items, subtitles, SponsorBlock); the outcome (status,
  error, queue and run time, files and **total size**, how often it was saved); and who asked.
* **Who** means: IP address (per `IpMode`), country, region and city (GeoIP), signed-in user, browser,
  OS, device type, bots, language, referrer, and what the page reports about itself: time zone,
  screen size and whether it runs as the installed app.
* **`audit_daily`**, one row per day with the totals, recomputed every 10 minutes for today and
  yesterday. It is kept when raw rows pass `RetentionDays`.

Writing never slows a request. Rows go into a bounded in-memory queue, and one background writer
saves them in batches, one transaction each. If the database can't be opened, downloads carry on and
the admin console says why. Values reach SQL only through the library's own literal encoding, and
identifiers only from code. The page tells users that requests are logged.

GeoIP data is [DB-IP](https://db-ip.com) IP-to-City Lite, licensed under CC BY 4.0. The attribution is
shown in the admin console.

## Admin console

`/admin` holds predefined reports over the audit log, for a date range chosen once above them all
(today, 7/30/90 days, 12 months or custom). Every chart has a table view and every number a tooltip:

* **Overview**: downloads, success rate, data prepared, saves, visitors, videos, media hours and
  average file, each compared with the previous period. Also downloads per day by outcome, visitors,
  data per day, genres, countries, devices, and an hour × weekday heat map. A range of a day or two
  is drawn per hour.
* **Content**: genres, formats, mode and scope splits, quality caps, top channels, top videos, why
  downloads failed, outcomes.
* **Audience**: countries, cities, languages, time zones, browsers, OS, devices, referrers, signed-in
  users, installed-app share, bots, and when people visit.
* **Downloads log / Requests log**: search and filters, paging, every field of a row in a side panel,
  CSV export of the range.
* **Releases**: versions, notes, migration planner, compatibility matrix, "check now".
* **System**: audit store health (file, size, rows, queue, errors, retention), GeoIP state, tools.

Who gets in: whoever enters `Ydl:Admin:Key`. The key is compared in constant time and rate-limited,
and a correct key earns an HttpOnly, SameSite=Strict cookie that changing the key revokes. With Ark
sign-in on, a user holding the Ark claim `ark-ydl.admin` also gets in. Without either, the console is
locked, except for a Development run on localhost.

## Bot checks ("Sign in to confirm you're not a bot")

YouTube challenges traffic from datacenter IPs far more than from home connections, so a cloud host
will hit this sooner or later. In order of effort:

1. Keep yt-dlp current and a JS runtime available. Both are automatic: deno is in the image, and
   anywhere else the app downloads it.
2. Lower the load: fewer `MaxConcurrentJobs`, shorter playlists, enable sign-in (below).
3. `Ydl:CookiesFile`: export `cookies.txt` from a **throwaway** YouTube account logged in in a
   private window (see the yt-dlp wiki *Extractors → Exporting YouTube cookies*). Cookies also unlock
   age-restricted videos. The account can be flagged, so never use your main one.
4. `Ydl:Proxy` through a residential/ISP proxy.

## Optional sign-in (Ark identity)

A public downloader gets found and abused. Set two values and every page and API call requires a
signed-in user:

```json
"ark_oauth_client": { "Authority": "https://<ark-host>/<tenant>", "ClientId": "<client-id>" }
```

Register both redirect URIs on the client (`{host}/signin-oidc` and `{host}/signout-callback-oidc`,
byte for byte) and map each user to the client. Without the mapping, sign-in fails with an error that
looks like a wrong password. Per-client job limits then apply per user instead of per IP.

## HTTP API

| Method & path | Purpose |
|---|---|
| `GET /api/system` | Tool versions, limits, auth state. |
| `POST /api/probe` `{url, playlist?}` | What a link is: title, qualities with sizes, audio tracks, chapters, heatmap, subtitles; or playlist entries. Cached 15 min. |
| `POST /api/jobs` `{url, mode, video, audio, clip, subtitles, embed, sponsorBlock, playlistItems, fileName, …}` | Queue a download → `202` + job snapshot. |
| `GET /api/jobs/{id}` | Snapshot. |
| `GET /api/jobs/{id}/events` | Server-Sent Events, one `job` event per change, closes when done. |
| `POST /api/jobs/{id}/cancel` · `DELETE /api/jobs/{id}` | Cancel · forget and delete files. |
| `GET /api/jobs/{id}/files/{n}[?inline=1]` | A file (range requests supported). |
| `GET /api/jobs/{id}/zip` | Every file of the job as one ZIP (built once, stored uncompressed). |
| `GET /healthz` | Liveness and tool versions. |
| `GET /api/version` | This app's version and revision, the web manifest's version, the latest release, `updateAvailable`. |
| `GET /api/releases` · `GET /api/releases/migration?from=1.0.0` | Release notes and compatibility matrix · the upgrade plan from a version (or `legacy-2017` / `legacy-2020`). |
| `GET /admin` · `/api/admin/*` | Admin console and its API (session, overview, breakdowns, downloads, requests, export, status). Key or Ark claim. |
| `GET /watch?…`, `/shorts/{id}`, `/playlist?…`, `/{11-char id}`, `/@handle` | "Swap the domain" redirects into the app. |

## How it is built

```
src/Ark.Ydl/
  Program.cs              DI, security headers (CSP allows only the YouTube player), rate limits, optional Ark sign-in
  Options/                YdlOptions + resolved paths
  Util/UrlGuard.cs        pasted text → normalised URL (allow-list, SSRF guard, t=/start=/end= hints)
  Tools/                  ToolLocator (find / download+verify / self-update), ProcessRunner (arg lists, kill tree)
  YtDlp/                  DownloadRequest + validator, YtDlpArgs (pure arg builder), ProgressTracker,
                          ProbeService + ProbeMapper (-J → UI model), YtDlpErrors (friendly messages)
  Jobs/                   DownloadJob (versioned state), JobManager (queue, zip), JobRunner, workers, janitor
  Endpoints/              API + shortcut routes
  Releases/               ReleaseCatalog (manifest, versions, migration plans), LatestReleaseService (feed), endpoints
  releases.json           the release manifest (embedded)
  Audit/                  schema (Ark.Rapid.Database), recorder, batched writer, GeoIP, reports, daily rollup + retention
  Admin/                  admin access (key / Ark claim) and the admin API
  wwwroot/                index.html, admin.html, css/{app,admin}.css,
                          js/{app,clip,jobs,api,util,release,version,admin,charts}.js — no build step
  wwwroot/img/            logo + icons, generated by branding/build.py (don't edit by hand)
tests/Ark.Ydl.Tests/      unit tests for every pure piece + in-process API tests (WebApplicationFactory)
branding/                 logo sources, the vector master and the icon generator (not shipped)
```

The look follows the ARK-RAPID logo: navy hull, teal arrow and sea, gold sun. The colours are tokens
at the top of `css/app.css`, for light and dark. The logo itself comes from `img/mark.svg` as
`<symbol>`s (`#mark`, `#wordmark`, `#tagline`), which the page draws with
`<svg><use href="/img/mark.svg#mark"/></svg>` and colours with `--mark-ink`, `--mark-sun` and
`--mark-sea`. That's how one file serves both themes. To change the logo or the icons, see
[branding/README.md](branding/README.md).

User input never reaches a shell: yt-dlp is started with an argument list, every option is an enum,
number or whitelisted value, and the URL comes after `--`. Jobs live in memory, so a restart forgets
them and the janitor deletes their folders, touching only folders named like a job id.

---

Download only content you own or have permission to save, and respect creators and YouTube's
Terms of Service.
