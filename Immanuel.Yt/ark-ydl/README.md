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
- passes a **JavaScript runtime** (deno, else node ≥ 22) via `--js-runtimes`;
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

Prerequisites: .NET 10 SDK and **deno** (recommended) or **node ≥ 22**.
On first start the app downloads what it is missing into `src/Ark.Ydl/App_Data/tools`, checking the
SHA-256 of each file:

* **yt-dlp**: the official release, if it is not on `PATH`. The app keeps it updated.
* **ffmpeg + ffprobe**: a self-contained static build (in `tools/ffmpeg/`), used when no ffmpeg on
  this machine works. macOS and Linux get [ffmpeg.martin-riedl.de](https://ffmpeg.martin-riedl.de)
  release builds (arm64 and x64); Windows gets [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds).
  It takes about 60 MB (about 200 MB on Windows), once. Delete the folder to fetch a newer build.

```bash
dotnet run --project src/Ark.Ydl          # http://localhost:5080
dotnet test                               # 161 tests
```

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
| `Tools:JsRuntime` / `JsRuntimePath` | `auto` | `deno`, `node`, `bun`, `quickjs` or `none`. |
| `Tools:AutoDownloadYtDlp` | true | Fetch the official release when none is found. |
| `Tools:AutoDownloadFfmpeg` | true | Fetch a static ffmpeg + ffprobe into `{tools}/ffmpeg` when none that works is found. |
| `Tools:FfmpegReleaseBase` / `FfmpegWindowsReleaseBase` | martin-riedl.de / yt-dlp FFmpeg-Builds | Download sources (for a mirror). |
| `Tools:AutoUpdateYtDlp` / `UpdateIntervalHours` | true / 24 | Self-update a yt-dlp this app owns. |

## Bot checks ("Sign in to confirm you're not a bot")

YouTube challenges traffic from datacenter IPs far more than from home connections, so a cloud host
will hit this sooner or later. In order of effort:

1. Keep yt-dlp current (automatic) and keep a JS runtime installed (deno is in the image).
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
  wwwroot/                index.html, css/app.css, js/{app,clip,jobs,api,util}.js — no build step
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
