# FBXService — System Document

## What It Is
HTTP service that accepts a binary `.fbx` file upload and returns JSON inspection data — nodes, meshes, materials, textures, settings, coordinate system. Used by other local tools and services that need to read FBX data without a Unity or DCC dependency.

---

## Local Code — `C:\tools\FBXService`

| File | Purpose |
|------|---------|
| `FBXService.cs` | **Everything** — ASP.NET Core minimal API + full FBX binary parser. Single file, no other source files. |
| `FBXService.csproj` | .NET 8 web project. Output goes flat to `bin\` (no nested Debug/Release/net8.0 subfolders). |
| `Dockerfile` | Multi-stage build. Stage 1: `dotnet/sdk:8.0`, restore with `-r linux-x64`, publish. Stage 2: `dotnet/aspnet:8.0`, non-root user. |
| `cloudbuild.yaml` | Cloud Build pipeline: docker build → push to GCR → deploy to Cloud Run. |
| `dev.ps1` | `dotnet watch run` — hot reload, auto-restarts on save. |
| `run.ps1` | `dotnet build` to `bin\` then runs `bin\FBXService.exe` — stable, no restarts. |
| `build.ps1` | Compile only, no run. |
| `deploy.ps1` | Manual deploy via `gcloud builds submit` + `gcloud run deploy`. Bypasses GitHub trigger. |
| `setup.ps1` | One-time setup: creates GitHub repo, pushes code, enables GCP APIs, grants IAM, creates Cloud Build trigger. |
| `tests\test.ps1` | Runs all commands against a real `.fbx` file, reports pass/fail + timing. |
| `.gitignore` | Ignores `bin/` and `obj/`. |

### Port
- **Local: always `25290`** — hardcoded in `FBXService.cs`, not an env var. Works regardless of how the process is launched.
- **Cloud Run: `PORT` env var** injected by GCP (default 8080). `FBXService.cs` checks for `PORT` first; falls back to `25290`.

---

## GitHub — `github.com/noa7/FBXService`

- Branch: `main`
- Every push to `main` automatically triggers Cloud Build
- Remote: `https://github.com/noa7/FBXService.git`

---

## GCP — Project `ioloc-491009`

### Cloud Build
- **Trigger name:** `FBXService-main`
- **Trigger:** push to `main` branch on `noa7/FBXService`
- **Config:** `cloudbuild.yaml` in repo root
- **What it does:** builds Docker image tagged with `$SHORT_SHA` + `latest`, pushes to GCR, deploys to Cloud Run
- **Image:** `gcr.io/ioloc-491009/fbx-service`
- **Build logs:** `https://console.cloud.google.com/cloud-build/builds?project=ioloc-491009`

### Cloud Run
- **Service name:** `fbx-service`
- **Region:** `europe-west1`
- **URL:** `https://fbx-service-yrqmjmpt5q-ew.a.run.app`
- **Memory:** 512Mi, **CPU:** 1, **Concurrency:** 80
- **Min instances:** 0 (scales to zero when idle), **Max:** 10
- **Console:** `https://console.cloud.google.com/run/detail/europe-west1/fbx-service/metrics?project=ioloc-491009`

---

## API

All inspect endpoints: `POST` with `multipart/form-data`, field name `file`.

| Endpoint | Returns |
|----------|---------|
| `GET /health` | `{ "status": "ok" }` |
| `GET /` | Service info + command list |
| `POST /inspect` | Full dump (all) |
| `POST /inspect/list` | Category counts |
| `POST /inspect/settings` | Coordinate system, unit scale, frame rate |
| `POST /inspect/nodes` | All scene nodes + transforms |
| `POST /inspect/node/{name}` | Single node |
| `POST /inspect/meshes` | All mesh summaries |
| `POST /inspect/mesh/{name}` | Mesh detail (`?raw` adds vertex/index/UV arrays) |
| `POST /inspect/materials` | All materials |
| `POST /inspect/material/{name}` | Single material |
| `POST /inspect/textures` | Texture references + embedded data info |
| `POST /inspect/tree` | Raw FBX node tree (6 levels deep) |
| `POST /inspect/animations` | Stub — not yet implemented |

Query flags: `?raw` (vertex arrays), `?verbose` (stack traces in errors)

---

## Dev Workflow
```
Edit FBXService.cs
    ↓
.\dev.ps1                              # hot reload on localhost:25290
    ↓
.\tests\test.ps1 model.fbx            # run all commands, see pass/fail
    ↓
git add -A ; git commit -m "msg" ; git push   # triggers auto Cloud Build deploy
    ↓
.\tests\test.ps1 model.fbx -Host https://fbx-service-yrqmjmpt5q-ew.a.run.app
```

---

## Key Implementation Notes
- **FBX parser is pure C#** — no Unity, no native libs. Handles binary FBX v7100–v7700. ASCII FBX returns a `ParseError`.
- **Unity types replaced** — `Vector2/3` and `Color` are local `record struct` types (`Vec2`, `Vec3`, `Col`).
- **All source in one file** — `FBXService.cs` contains the web host setup, all HTTP endpoints, FBX parser, scene extractor, and command dispatch.
- **Animation extraction not implemented** — the `animations` endpoint returns a stub. Use `tree` command to inspect raw animation nodes.
