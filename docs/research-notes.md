# Research notes — low-latency Android streaming

Collected from existing open-source projects while tuning CLGame2Phone.
Each section ends with what we actually applied (or deliberately skipped).

## 1. scrcpy / QtScrcpy — the reference design

- Architecture: `adb` → `scrcpy-server` runs on the **guest**, encodes with
  `MediaCodec` on the guest GPU, and the stream goes straight to the client over
  the USB tunnel. No host-side screen capture, no host-side encode.
- Latency 35–70 ms over USB at 1080p; QtScrcpy claims ~30 ms.
- `scrcpy` disables the guest's video downscale/AA and pushes
  `KEY_LOW_LATENCY` (`--low-latency`), which makes MediaCodec return frames as
  soon as they are ready instead of buffering.
- Touch is injected **inside the guest** via `InputManager.injectInputEvent`
  (exactly what our `TouchServer` does).

**Applied:** our touch path already matches scrcpy's approach.
**Not applied:** moving the encoder into the guest. LDPlayer's x86 Android 9
image has no usable MediaCodec encoder for a 1600x720 60 fps stream, and
scrcpy-server cannot capture another app's surface on Android 9 without
`MediaProjection` + a foreground service. Host-side `ddagrab` + NVENC stays.

## 2. Sunshine / Moonlight — encoder settings for game streaming

- Hardware encoder is mandatory; NVENC at `p1`/`ll` (low latency) preset is the
  recommended starting point for 60 fps.
- `CBR` is the usual recommendation for network streaming because it keeps
  latency predictable — but only when the link is the bottleneck. Over USB
  (`adb reverse`, ~300 Mbps) the link is *not* the bottleneck, so the constant
  bitrate only wastes bits on static scenes and starves motion scenes.
- Keyframe interval of 1–2 s is enough; `-g 60` at 60 fps = 1 s.
- B-frames must be off (`-bf 0`) for latency; `-zerolatency 1` removes the
  encoder's internal frame queue.
- Lookahead must be 0, otherwise the encoder delays output to improve quality.

**Applied:** `-preset p1 -tune ll -rc vbr -cq 20 -b:v 0 -maxrate 30M
-bufsize 3M -g 60 -bf 0 -zerolatency 1 -rc-lookahead 0 -delay 0`.

## 3. NVENC preset cost vs quality (measured on this machine)

Lossless reference: 6 s of live gameplay, 1600x720@60, captured with
`h264_nvenc -rc constqp -qp 0` (139.8 MB). PSNR is measured against it.

| Setting | Bitrate | PSNR Y | GPU util | Encoder util |
|---|---|---|---|---|
| `p3 hq cbr 12M` (old) | 11.4 Mbps | 38.46 dB | ~20 % | 20 % |
| `p5 hq vbr cq20` | 19.7 Mbps | 43.01 dB | ~11 % | 20 % |
| `p7 hq vbr cq19` | 22.1 Mbps | 43.76 dB | 11 % | 20 % |
| `p1 ll vbr cq20` (new) | 19.4 Mbps | 42.44 dB | 13 % | **6–8 %** |
| `p1 ll vbr cq23` | 14.0 Mbps | 40.56 dB | 13 % | 6 % |

`p1 ll` gives the same quality as `p5 hq` within 0.6 dB while using a third of
the encoder — that is the trade we took, because the user's GPU was the
complaint, not the bitrate.

## 4. Colour range — the "washed out" bug

Two independent mistakes were possible: the encoder tagging the stream as
full-range while the decoder assumed limited-range, and vice versa. The phone
(MediaCodec → SurfaceView) always treats the stream as **limited range (tv)**
unless the SPS says otherwise, and the Helio G36 decoder ignores the VUI range
flag entirely.

Measured on identical static frames (guest framebuffer vs phone screencap):

| Encode | Phone/source luma ratio |
|---|---|
| full-range flags on encoder | 0.87–0.94 (too dark, banding) |
| no range flags (default tv) | 0.98–1.03 (correct) |

**Applied:** no `-color_range` flags; plain `scale=1600:720:flags=bicubic`
with `format=yuv420p`. That is also the cheapest option — no extra swscale
matrix pass.

## 5. Android decode side (MediaCodec low-latency)

- AOSP added a low-latency decoder mode in Android 11 (`KEY_LOW_LATENCY`).
  Our phone runs Android 13 Go, so the key exists, but Go-edition codecs often
  ignore it.
- The real win on low-end phones is **not queueing**: feed one access unit at a
  time and render with `releaseOutputBuffer(idx, true)` immediately, which we
  already do.
- `SurfaceView` (not `TextureView`) avoids one full-frame GPU copy — already
  the case.
- Decoder must be configured with the **exact** stream size. Any mismatch makes
  the phone scale in the compositor, which is where the earlier softness came
  from.

**Applied:** after the edge-to-edge fix, SurfaceFlinger reports
`geomBufferSize=[0 0 1600 720]` and `geomLayerBounds=[0 0 1600 720]` — a true
1:1 blit, no compositor scaling.

## 6. WebRTC-based cloud-phone projects

Projects like `ScrcpyOverWebRTC` and Google's
`android-emulator-container-scripts` use WebRTC for NAT traversal and browser
playback. That matters when the client is remote; for a USB-tethered phone it
adds jitter buffering (typically 50–200 ms) for no benefit.

**Not applied.** Our raw TCP + `adb reverse` path has a measured 0–1 frame
queue and is simpler to debug.

## 7. Verification method (kept for future tuning)

Never tune by eye. The loop we use:

1. Capture a lossless reference of real gameplay (`constqp 0`).
2. Re-encode the reference with candidate settings, measure PSNR/SSIM.
3. Capture guest framebuffer (`adb -s emulator-5554 shell screencap`) and phone
   display (`adb -s <phone> shell screencap`) on the same static screen.
4. Compare `signalstats.YAVG` (colour) and `sobel` energy (sharpness).
5. Only then look at the picture.

Current end state: sharpness 44.08 (phone) vs 43.67 (source) — no measurable
softness — and luma ratio 0.98–1.03.
