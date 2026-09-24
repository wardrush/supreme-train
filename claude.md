# claude.md: session handoff

Read this first. It is 30 seconds of context instead of re-reading the code.

## Session goals

Goal: a personal, password-gated web tool that turns a YouTube URL into an mp4 or mp3, built on YoutubeExplode plus Converter, hosted on Railway, and operable from a phone.

Done (branch `claude/youtube-downloader-tool-ic7kfr`, PR open against `main`):
- ASP.NET Core minimal API on .NET 10 LTS: `POST /api/convert`, `GET /healthz`, and a static page.
- Shared-password middleware (header or cookie, constant-time over SHA-256). The app refuses to start without `APP_PASSWORD`.
- Per-IP fixed-window rate limit on all `/api/*` (failed logins count), a duration cap, and a one-at-a-time gate (429 when busy).
- Temp files: per-job prefix, `DeleteOnClose` plus an `OnCompleted` backstop, and a full sweep at startup.
- Distinct errors: unavailable, age-restricted, region-blocked, bot check, bare 403/429 block, purchase, and library breakage.
- 61 tests pass. The Docker image builds with ffmpeg 6.1.1, and the container smoke test passes.

Remains:
- First real deploy on Railway (dashboard steps in README "Deploy on Railway").
- An end-to-end download has never been tested. YouTube is blocked from the build sandbox, so no real video has been fetched or converted yet.
- Check on Railway whether YouTube blocks the datacenter IP (the most likely failure).

## Key decisions

- No Railway config file (no `railway.json`, no `.railway/railway.ts`). The original spec asked for `railway.json`. Railway's docs (source read 2026-09-24 from github.com/railwayapp/docs, because docs.railway.com was egress-blocked) say Config as Code is deprecated, new services cannot opt in, and the hard cutoff is 2026-12-01. The spec also called IaC "experimental", but the docs now list TypeScript IaC as GA. The user chose Dockerfile only plus dashboard settings. Revisit IaC (`.railway/railway.ts` plus the `railwayapp/config` GitHub Action with a `RAILWAY_TOKEN` secret) if a second service, volume, or database is added.
- Single container, single replica. The concurrency gate and the startup sweep are in-process; more than one replica would break both assumptions.
- Reject, don't queue, a second conversion. A phone user gets an instant 429 instead of a hanging request that might hit Railway's 5-minute idle cutoff.
- Custom stream selection instead of the Converter's default. The default picks the highest quality regardless of codec (it could be 4K VP9, which means a slow re-encode). The selector prefers `avc1` in mp4 at or below `MAX_VIDEO_HEIGHT` plus mp4/AAC audio, so ffmpeg only copies.
- Auth is the `X-App-Password` header from `fetch`, with optional localStorage "remember". The cookie is accepted too, but nothing sets it yet.
- Forwarded headers: `ForwardLimit=1`, known proxies cleared, so the right-most X-Forwarded-For entry (Railway's edge) is the client IP. UNRESOLVED: this has not been verified on Railway. If every request shows the same IP, the per-IP limit is effectively global.
- Error classifier: "bot_check" is used only when YouTube's reason text says so. A bare HTTP 403/429 is "blocked". The sandbox proxy's 403 was initially mislabelled as a bot check, which is why the two are separate.

## Files

```
Dockerfile                  SDK 10.0 build -> aspnet 10.0 (Ubuntu noble) runtime + apt ffmpeg, runs as non-root $APP_UID
global.json                 SDK 10.0.x, opts dotnet test into Microsoft.Testing.Platform (needed for xunit.v3 on .NET 10 SDK)
YtDownloader.sln
README.md                   env vars, API codes, local run, Railway dashboard steps, updating YoutubeExplode, known risks
claude.md                   this file
src/YtDownloader/
  Program.cs                wiring: PORT, forwarded headers, rate limiter, auth, static files, endpoints, startup refusal (exit 1)
  AppOptions.cs             env var parsing and validation
  PasswordAuthMiddleware.cs /api/* guard, constant-time compare
  YoutubeUrlValidator.cs    host allowlist + VideoId parse; bare IDs rejected
  DurationPolicy.cs         max duration / live-stream rejection
  ConversionService.cs      ConversionGate, ConversionService, StreamSelector, FileNames
  ErrorClassifier.cs        exception -> (status, code, message)
  TempFiles.cs              temp dir, startup sweep, per-job delete
  wwwroot/index.html        phone UI (plain HTML/JS, dark mode, ?url= prefill)
tests/YtDownloader.Tests/
  UnitTests.cs              URL validation, duration, options, password match, classifier, gate, temp files
  HttpTests.cs              WebApplicationFactory: healthz, page, 401s, bad url/format, cookie auth, rate limit
```

## Next steps

1. Railway: create the service from the repo, set `APP_PASSWORD`, healthcheck `/healthz`, restart `On Failure`, and generate a domain.
2. Try one short video in each format from the phone. If you get `bot_check` or `blocked`, see README "Known risks" and ask the user before adding a proxy or cookies.
3. Check in Railway's logs that different clients get different rate-limit partitions (forwarded-IP handling).
4. If long videos time out (Railway's 5-minute idle limit), move to a job-plus-poll design.

Known blockers from this sandbox: the Railway API/CLI (backboard.railway.com), docs.railway.com, and youtube.com are all egress-blocked (proxy 403), and no Railway token is present. Nothing Railway-side was run or changed.

## Commands

```bash
# build + test (no local SDK needed)
docker run --rm -v "$PWD":/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build -c Release && dotnet test -c Release'

# with a local .NET 10 SDK
dotnet build -c Release && dotnet test -c Release

# run locally
APP_PASSWORD=change-me dotnet run --project src/YtDownloader
docker build -t ytdl . && docker run --rm -p 8080:8080 -e APP_PASSWORD=change-me ytdl

# deploy: merge the PR into main; Railway's GitHub integration builds the Dockerfile and deploys.
```

In this cloud sandbox, Docker needs `dockerd &` started first, and builds need the proxy CA. A throwaway `Dockerfile.sandbox` with a `--build-context ccr=/root/.ccr` CA step was used for this. It is not committed.
