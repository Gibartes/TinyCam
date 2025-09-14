# TinyCam

TinyCam is a **USB-camera worker node** written in C# (.NET).
It captures video from a local camera, **hardware-encodes** it (Intel QSV / NVIDIA NVENC / CPU fallback), **segments to local files**, and **securely streams** the same bitstream over **WebSocket** with authenticated, PSK-based **AES-GCM** encryption.

* **OS**: Windows
* **Build**: .NET (single-file, self-contained `.exe` or ELF)
* **Mux**: MP4 / WebM / Matroska (live tuning for browser/native)
* **Security**: PSK → HKDF session key, AES-GCM w/ counter nonce, AAD-bound
* **Control**: Minimal web server for start/stop/apply/update-key, device probe
* **Rotation**: Optional async file rotation by count

---

## Table of contents

* [Features](#features)
* [Dependencies](#dependencies)
* [Build & Run](#build--run)
* [Configuration (`config.json`)](#configuration-configjson)
* [Key file (`keys.json`)](#key-file-keysjson)
* [REST & WebSocket API](#rest--websocket-api)
* [Security design](#security-design)
* [Device selection](#device-selection)
* [Logging](#logging)
* [Troubleshooting](#troubleshooting)
* [License](#license)

---

## Features

* **Background capture & encode**

  * Intel **QSV**: `h264_qsv`, `hevc_qsv`, `av1_qsv`, `vp9_qsv` (Recommended)
  * NVIDIA **NVENC**: `h264_nvenc`, `hevc_nvenc`, `av1_nvenc` (if supported)
  * **CPU** fallback: `libx264`, `libx265`, `libaom-av1`, `libvpx-vp9`
* **Dual output** (tee)

  * **Local file** segmentation (hour/second/day)
  * **Live pipe** for WebSocket clients (WebM / fMP4 / MKV)
* **Web control**

  * `/start`, `/stop`, `/apply-config`, `/update-key`, `/stream`, `/device`
* **Security**

  * HMAC (management key) for control endpoints
  * WebSocket payloads: AES-GCM per-frame, **HKDF** session key (PSK), **aad** binding, 96-bit counter **nonce**
* **File rotation** (optional)

  * Keep last *N* files; delete the oldest asynchronously
* **Cross-platform process guarding**

  * Ensures ffmpeg is the single active child; graceful shutdown on restart/exit

---

## Dependencies

### Required

* **.NET 8+ SDK** (build) / .NET runtime (if not self-contained)
* **FFmpeg** recent build, with encoders as needed:

  * Windows: dshow, QSV, NVENC, aac/libopus etc.
  * Linux: v4l2, vaapi (if you use VAAPI/QSV), aac/libopus etc.

> Put `ffmpeg` in `PATH`, or point to it via `config.json → "ffmpegPath"`
> (If missing, TinyCam tries the app’s current directory too.)
> Download link for ffmpeg: https://www.ffmpeg.org/download.html

---

## Build & Run

### Build (single-file, self-contained)

Windows x64:

```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true
```

Linux x64:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true
```

The binary will be in:

```
./TinyCam/bin/Release/net8.0/<RID>/publish/TinyCam(.exe)
```

### Run

```bash
# same folder as config/keys or pass env vars
set TINY_CAM_CONFIG=config.json
set TINY_CAM_KEYS=keys.json
TinyCam.exe
# Linux:
# TINY_CAM_CONFIG=config.json TINY_CAM_KEYS=keys.json ./TinyCam
```

Server listens on `http://0.0.0.0:8080` by default.

---

## Configuration (`config.yaml`)

> Place next to the binary or point via `TINY_CAM_CONFIG`.

```yaml
# TinyCam config.yaml

# ── Basics ────────────────────────────────────────────────────────────────
platform: auto
device: '@device_pnp_\\?\usb#vid_045e&pid_0812&mi_00#6&151e9577&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global'
width: 1280
height: 720
fps: 30
ffmpegPath: C:/ffmpeg/ffmpeg.exe
ffmpegDebug: false

# ── Encoding (lightweight first: h264_qsv recommended) ───────────────────
# codec: h264 | vp9 | av1
encoder: qsv          # qsv (Intel QSV), nvenc (NVIDIA GPU), cpu
videoCodec: vp9       # vp9 av1 h264 h265
useLowPower: true
gop: 60               # -g
keyintMin: 60         # -keyint_min (optional)

# h264_qsv-only bitrates (kbps)
useBitrate: false
bitrateKbps: 3000
maxrateKbps: 3000
bufsizeKbps: 6000

# AV1 / VP9 constant quality (lower = higher quality)
globalQuality: 28

# ── Output / Segments / Filenames ────────────────────────────────────────
outputDir: 'C:/Recordings'     # Pre-create recommended (path without spaces)
segmentUnit: second            # second | hour
segmentSeconds: 60             # if 'second' → seconds, if 'hour' → hours
fileNamePattern: 'camera_%Y-%m-%dT%H-%M-%S'
streamOnly: false

# Container auto/forced
# - auto: h264 -> mp4 (files) & mkv (pipe),  av1/vp9 -> webm (files/pipe)
segmentFormat: auto            # auto | webm | mp4 | matroska
pipeFormat: auto               # auto | webm | mp4 | matroska
pipeLive: true                 # apply live tuning when pipe is webm
clusterTimeLimitMs: 1000       # webm live cluster time
clusterSizeLimitBytes: 1048576 # webm live cluster size (1MB)

# ── Input stabilization (common for dshow/v4l2) ──────────────────────────
rtbufSize: '512M'
threadQueueSize: 1024
useWallclockTimestamps: true

# ── Encrypted storage ────────────────────────────────────────────────────
saveEncryptedConfig: false
isEncrypted: false
cipher:

# ── Extra ffmpeg arguments ───────────────────────────────────────────────
extraInputArgs:
extraEncoderArgs:
extraOutputArgs:

# ── Audio ────────────────────────────────────────────────────────────────
enableAudio: false
audioDevice: 'Microphone (USB Camera XYZ)'  # full dshow device name
audioCodec: auto          # auto|aac|libopus|copy
audioBitrateKbps: 96
audioSampleRate: 48000
audioChannels: 2

# ── File management ──────────────────────────────────────────────────────
useFileRotation: true        # enable file rotation
retentionSweepSeconds: 30    # rotation sweep interval (seconds)
retainSafeWindowSeconds: 60
retainMaxFiles: 2
retainFilePrefix: "camera_"  # prefix of files to keep/clean (default: "camera_")

# ── System logging ───────────────────────────────────────────────────────
LogMode: stdout
LogMaxSizeMB: 1
LogMaxFiles: 2
LogRollDaily: false

```

---

### Recommended options (storage-size friendly)

If disk usage is a concern, prefer **hardware-accelerated** codecs:

- **Intel QSV + VP9** (`encoder: qsv`, `videoCodec: vp9`)
  - Good compression, **great browser compatibility** (WebM).
  - Smooth for long live streams (low CPU on iGPU).

If you must use CPU encoders, reduce **resolution/FPS** or raise **CRF/global_quality** (e.g., VP9 `globalQuality: 32~36`, H.264 CRF 24~28).

### VP9 vs H.265 (HEVC) compression

- At the **same visual quality**, HEVC typically compresses **slightly better** than VP9 (often within **~5–15%** bitrate difference), but it varies by content, encoder, and settings.
- **VP9 advantages:** widely playable in browsers (WebM), solid quality per bit, mature tooling.
- **HEVC advantages:** marginally better compression in many cases, broad support in native players/OS apps; but **web compatibility** can be trickier.
- **Practical tip:**  
  - For **web streaming & compatibility** → **VP9** (WebM).  
  - For **archival / smallest files** with supported players → **HEVC**.

---

**Notes**

* **H.264** → default file container = `mp4` (with `+faststart` on segments).
  Pipe can be **fMP4** (browser/MSE) or **MKV** (native players).
* **AV1/VP9** → default = `webm` for both file/pipe.
* Set `"streamOnly": true` to disable disk write entirely.

---

Here’s a README section you can drop in:

---

## Detect your camera & set `device` (use **Alternative name**)

On Windows (DirectShow), TinyCam works best when you set the camera by its **Alternative name** (the stable PnP path that starts with `@device_pnp_…`). After detecting the device, **copy the Alternative name** into your `config.yaml`’s `device` field.

### Option A: Use TinyCam’s `/device` endpoint

```bash
# TinyCam must be running
python stream_downloader.py  --http http://127.0.0.1:8080 --keys {KEY_FILE_PATH}  --device 
```

You’ll see entries like:

```
[video] Your Camera
- Alt : @device_pnp_\\?\usb#vid_045e&pid_0812&mi_00#6&151e9577&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global
- rec : video="@device_pnp_\\?\usb#vid_045e&pid_0812&mi_00#6&151e9577&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
- frd : video="Your Camera"
```

Then set it in `config.yaml` (quote it to preserve backslashes):

```yaml
device: 'video="@device_pnp_\\?\usb#vid_045e&pid_0812&mi_00#6&3a91f6c1&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"'
```

> Tip: The line labeled `recommended:` from `/devices` can be pasted directly into `device`.

### Option B: Use FFmpeg directly

```bash
ffmpeg -hide_banner -f dshow -list_devices true -i dummy
```

Find your camera under **DirectShow video devices**, copy the **Alternative name**, and set it in `config.yaml` as shown above.

### Linux (v4l2)

Use the device node (e.g. `/dev/video0`):

```yaml
device: /dev/video0
```

---

**Why Alternative name?**
Friendly names can change (or be ambiguous) across reboots/ports. The PnP **Alternative name** is stable and avoids “device busy”/“could not run graph” issues from selecting the wrong camera.

---

## Key file (`keys.json`)

> Place next to the binary or point via `TINY_CAM_KEYS`.

```json
{
  "managementKey": "BASE64_32_BYTES",   // control endpoints (HMAC)
  "accessKey":     "BASE64_32_BYTES"    // data streaming (HKDF/AES-GCM)
}
```

* Use **random 32-byte** values (Base64).
* Rotate access key via `/update-key`.

---

## REST & WebSocket API

### Auth header

Management endpoints require:

```
X-TinyCam-Auth: base64(HMAC-SHA256(bodyJson, managementKey))
```

### `POST /start`

Starts (or restarts) the background capture.

```bash
curl -X POST http://127.0.0.1:8080/start \
  -H "Content-Type: application/json" \
  -H "X-TinyCam-Auth: <b64 hmac of body>" \
  -d '{"force":true}'
```

### `POST /stop`

Gracefully stops ffmpeg; ensures last MP4/WebM segment is playable.

### `POST /apply-config`

Reloads `config.json` and restarts the muxer.

### `POST /update-key`

Rotates access key at runtime.

```json
{ "accessKey": "BASE64_32_BYTES" }
```

### `GET /device`

Lists capture devices (Windows: **dshow** friendly & alternative names; Linux: `/dev/video*` + v4l2 capabilities).

### `GET /stream` (WebSocket)

Query:

```
/stream?token=<b64_hmac(stream:exp, accessKey)>
        &exp=<unix_ts>
        &cnonce=<base64_16bytes>
```

* Server sends a **hello JSON**:

  ```json
  { "type":"hello","snonce":"...","conn":"...","w":1280,"h":720,"fps":30,"codec":"vp9","exp":1699999999 }
  ```
* Then **binary frames**: `[12B nonce][16B tag][ciphertext]`

  * nonce = `connId(4B) || counter(8B BE)` (monotonic)
  * AES-GCM decrypt with AAD: `"{connB64}|{exp}|{codec}|{w}x{h}|{fps}"`

---

## Security design

* **Control** (REST): HMAC-SHA256 with **managementKey** (pre-shared).
* **Data** (WS):

  * Client sends `exp` + `token=HMAC("stream:exp", accessKey)` + `cnonce(16B)`.
  * Server replies with `snonce(16B)` and `connId(4B)`.
  * **HKDF-SHA256**: `sessionKey = HKDF(accessKey, salt=cnonce||snonce, info="tinycam hkdf v1", 32B)`.
  * Per-frame **AES-GCM**:

    * Nonce(12B): `connId(4B)||counter(8B big-endian)`
    * **AAD** bound to stream parameters to prevent cross-use.
  * Verification failure → connection rejected/closed.

* **HTTPS** : TinyCam Server does not implement HTTPS.
  * Put TinyCam behind NGINX (or any TLS reverse proxy) for HTTPS termination and HTTP/2.
  * TinyCam listens on http://0.0.0.0:8080 (configurable).
  * NGINX terminates TLS (443), proxies REST and WebSocket (/stream) to TinyCam.
  * Keep latency low by disabling proxy buffering for live streams.

---

## Device selection

* **Windows (DirectShow)**

  * TinyCam prefers **Alternative name** (stable PNP path).
  * Input token examples:

    * Friendly: `video="LifeCam Cinema(TM)"`
    * Alternative: `video="@device_pnp_\\?\usb#vid_045e&pid_0812&...\\global"`
* **Linux (v4l2)**: typical `"/dev/video0"`

You can query `/devices` and copy the **recommended** token.

---

## Logging

Three modes:

* `"none"`   – disable app logs
* `"stdout"` – console output
* `"file"`   – write to `logging.filePath` with **size-based rotation** (`maxSizeMB`, `maxFiles`)

Enable FFmpeg stderr capture by `"ffmpegDebug": true`.

---

## Troubleshooting

**MP4 file won’t play**

* Ensure **graceful shutdown** (TinyCam sends `q` to ffmpeg before killing).
* Avoid killing ffmpeg abruptly; let it finalize the `moov` atom.
* Recommend to use vp9 or av1 encoding option for the accident.

**Device busy / I/O error**

* Another process is using the camera. Close it or point TinyCam at a different device.
* On Windows, pass the **Alternative name** (PNP path) for reliability.

**High CPU**

* Prefer **QSV/NVENC** encoders.
* For CPU encoders, reduce resolution/FPS or raise CRF.

---

## License

MIT (see `LICENSE`).

---
