# YT Download

A small, password-protected web page that takes a YouTube URL and returns an mp4 (video) or mp3 (audio) file. It is for personal use and is not public. The shared password is there to keep bots out; it is not real account security.

Stack: ASP.NET Core minimal API on .NET 10 (LTS), [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) plus YoutubeExplode.Converter, and FFmpeg. It ships as one Docker container on Railway.

## How it works

1. The page (`/`) posts `{ url, format }` to `POST /api/convert`, with the password in the `X-App-Password` header.
2. The server checks the URL against an allowlist of YouTube hosts, checks the password, rate limit, and duration cap, and checks that no other conversion is running.
3. It picks streams that FFmpeg can copy without re-encoding: H.264 video at or below `MAX_VIDEO_HEIGHT`, plus AAC audio. It muxes them into a temp file (mp3 always needs an audio transcode) and streams that file back.
4. The temp file is deleted when the response finishes or fails. Leftovers from a crash are removed on the next startup.

A share-sheet trick for phones: `https://<your-domain>/?url=<youtube-url>` prefills the URL box.

## Environment variables

| Name | Required | Default | Purpose |
|---|---|---|---|
| `APP_PASSWORD` | yes | none | Shared password. If it is missing, the app refuses to start (exit code 1). |
| `MAX_DURATION_MINUTES` | no | `30` | Longest video accepted. Live streams are always rejected. |
| `MAX_VIDEO_HEIGHT` | no | `1080` | Highest video resolution picked for mp4. |
| `RATE_LIMIT_PERMITS` | no | `10` | `/api/*` requests allowed per IP per window, including failed password attempts. |
| `RATE_LIMIT_WINDOW_MINUTES` | no | `15` | Rate limit window. |
| `TEMP_DIR` | no | `/tmp/ytdl` (set in the Dockerfile) | Working directory for conversions. It is wiped at startup. |
| `FFMPEG_PATH` | no | `ffmpeg` (on PATH) | FFmpeg binary. |
| `PORT` | set by Railway | `8080` locally | Port the app listens on. |

Example values (placeholders only, never commit real ones):

```
APP_PASSWORD=change-me-to-a-long-random-string
MAX_DURATION_MINUTES=30
```

## API

- `GET /healthz`: returns `200 ok` with no auth. Used by the Railway healthcheck.
- `POST /api/convert`: body `{"url": "...", "format": "mp4" | "mp3"}`. Auth is the `X-App-Password` header (or an `app_password` cookie).
  - `200`: the file, with a `Content-Disposition` filename.
  - `400 invalid_url` / `invalid_format`
  - `401`: wrong or missing password.
  - `404 unavailable`: private, removed, or wrong ID.
  - `403 age_restricted`, `403 requires_purchase`
  - `451 region_blocked`
  - `422 rejected`: over the duration cap, a live stream, or no usable streams. `422 unplayable` covers any other reason YouTube gives.
  - `429`: per-IP rate limit hit, or `busy` (one conversion at a time; a second request is rejected, not queued).
  - `503 bot_check`: YouTube asked the server to "confirm you're not a bot". `503 blocked`: YouTube returned a bare 403/429.
  - `502 youtube_changed`: YoutubeExplode could not parse YouTube's response, which usually means the library needs an update.

## Build, test, and run locally

Requires the .NET 10 SDK, or Docker only.

```bash
dotnet build -c Release
dotnet test -c Release        # 61 tests: URL validation, auth, rate limit, duration, errors, temp files

# run without Docker (needs ffmpeg on PATH)
APP_PASSWORD=change-me dotnet run --project src/YtDownloader
# -> http://localhost:5000 (or whatever port the output shows)

# run with Docker
docker build -t ytdl .
docker run --rm -p 8080:8080 -e APP_PASSWORD=change-me ytdl
# -> http://localhost:8080
```

`global.json` opts `dotnet test` into Microsoft.Testing.Platform, which xunit.v3 needs on the .NET 10 SDK.

## Deploy on Railway

The deploy path is: merge to `master` (this repo's default branch), then Railway's GitHub integration builds the root `Dockerfile` and deploys it.

There is no `railway.json` in this repo on purpose. As of 2026-09, Railway's docs mark Config as Code as deprecated. New services cannot opt in, and existing files stop being read on 2026-12-01. The few settings this app needs are set once in the dashboard instead (all of it works from the phone):

1. New project, then Deploy from GitHub repo, then pick this repo (branch `master`). Railway logs "Using detected Dockerfile!".
2. Service, then Variables: add `APP_PASSWORD` (and optionally `MAX_DURATION_MINUTES`, etc.).
3. Service, then Settings, then Deploy:
   - Healthcheck Path: `/healthz`
   - Restart Policy: `On Failure` (this is Railway's default, with 10 max retries).
4. Service, then Settings, then Networking: Generate Domain.
5. Leave replicas at 1. The one-at-a-time gate and the temp-file sweep assume a single instance.

Railway notes worth knowing (from its docs, not verified on a live deploy):
- The healthcheck runs only at deploy time. It does not monitor the service afterwards.
- Edge limits: a request is closed after 5 minutes with no bytes flowing, with a hard cap of 15 minutes. The app sends nothing until conversion finishes, so a conversion slower than 5 minutes will be cut off. See Known risks.

## Updating YoutubeExplode

The version is pinned (`6.6.2`) in `src/YtDownloader/YtDownloader.csproj`, because YouTube changes its internals and YoutubeExplode releases follow. When downloads start failing with `youtube_changed` or odd `unplayable` errors:

1. Check the [releases](https://github.com/Tyrrrz/YoutubeExplode/releases) or `https://api.nuget.org/v3-flatcontainer/youtubeexplode/index.json` for a newer version.
2. Bump both `YoutubeExplode` and `YoutubeExplode.Converter` to the same version.
3. Run `dotnet build && dotnet test`, then read the release notes for API changes (stream selection and exception types are the likely spots).
4. Open a PR, merge, and let Railway redeploy.

## Known risks

- Datacenter IP blocking. YouTube often blocks cloud IP ranges, and Railway's are no exception. If the deployed app returns `bot_check` or `blocked` while it works locally, the options are:
  1. A residential proxy (paid; configured on the `HttpClient` passed to `YoutubeClient`).
  2. Self-hosting on a home machine behind a tunnel (Cloudflare Tunnel, Tailscale Funnel).
  3. Signed-in cookies (a ToS gray area, and the account can be banned).

  None of these is implemented. Decide first.
- Long conversions vs. Railway's 5-minute idle limit (above). If this bites, the fix is a job ID plus polling, or streaming bytes as they convert.
- YoutubeExplode breakage when YouTube changes internals. See Updating YoutubeExplode.
- Error matching for age, region, and bot checks relies on YouTube's reason text. New wording falls through to `unplayable`, with YouTube's reason included.
- Auth is a shared password in a header, stored in `localStorage` only if "Remember on this device" is ticked. It is fine for keeping bots out, and nothing more.
