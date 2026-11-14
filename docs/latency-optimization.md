# Low-Latency Video Streaming Optimization
## PC (emulator capture) -> Android phone over USB/ADB

Target end-to-end glass-to-glass: **30-50 ms**. Current stack estimated ~80-140 ms. Below are specific, actionable tuning recommendations ordered by impact and grouped so the cheapest wins come first. Priority tags: **P0** (do now, big win), **P1** (medium), **P2** (polish).

---

## 0. Latency budget (rough, current config)

| Stage | Est. ms |
|---|---|
| ddagrab capture + hwdownload/scale | 8-16 |
| NVENC encode (p1/ull, 5 Mbps) | 3-6 |
| PC->phone TCP over adb reverse | 1-3 |
| Android reader + queue (queue cap 4) | 16-67 |
| MediaCodec decode + SurfaceView render | 8-20 |
| Display scanout / vsync | 8-17 |
| **Total** | **~45-130** |

The single biggest removable chunk is the **Android-side queue + decode scheduling** (40-80 ms). Second is **capture/scale** on PC.

---

## 1. Capture

### Current
`ddagrab` with `framerate=60`, `draw_mouse=0`, then `hwdownload,format=bgra,scale=1280:576:flags=fast_bilinear`. Captures the whole 1600x720 emulator window and downscales.

### Recommendations

- **P0 Keep ddagrab, but avoid the round-trip through system memory when possible.** `ddagrab` already uses DXGI Desktop Duplication under the hood (hardware-accelerated). The expensive part is `hwdownload,format=bgra` forcing a GPU->CPU copy before NVENC re-uploads. Replace the filter chain with a zero-copy path: capture native resolution and let NVENC handle scaling, or use `scale_npp`/`scale_cuda` if the build has CUDA filters. Expected saving **5-10 ms**.
  - If the ffmpeg build supports it: `-vf "scale_cuda=1280:576"` instead of `hwdownload,format=bgra,scale=...`.
- **P0 Drop resolution to the phone's native-ish size.** 1280x576 is fine, but if the phone is 1080p-wide, capturing at 1600x720 then downscaling adds work. Better: set emulator window to exactly 1280x576 (or the phone aspect) so no scale is needed. Remove the `-vf` entirely. Saving **3-6 ms** + CPU.
- **P1 Dirty-rect / region capture.** DXGI Desktop Duplication supports dirty rectangles; ffmpeg's `ddagrab` exposes `capture_cursor` and region via `offset_x/offset_y/video_size`. For a game, the whole frame changes anyway, so dirty-rect capture rarely helps for full-screen games. Only worth it for static UI. Skip unless profiling shows spare time.
- **P1 Cursor handling.** `draw_mouse=0` is correct for latency (the OS cursor is drawn by the compositor and would add a frame). Keep it. If you need a cursor, draw it client-side on Android instead.
- **P1 Framerate.** 60 fps is the right target for 30-50 ms budget (one frame = 16.7 ms). Do not go above 60 on a phone panel that is 60 Hz. If the panel is 120 Hz, 90/120 helps input feel but costs bandwidth. Keep 60.
- **P2 `ddagrab` adapter selection.** If multi-GPU, pin `output_idx` to the adapter driving the emulator. Wrong adapter forces a copy across GPUs.

### Expected capture latency after tuning: 3-8 ms.

---

## 2. Encoding (NVENC H.264)

### Current
`-c:v h264_nvenc -preset p1 -tune ull -rc cbr -b:v 5M -maxrate 5M -bufsize 2500k -g 30 -bf 0 -zerolatency 1`.

### Recommendations

- **P0 GOP = 1 (all-intra) is NOT recommended for 5 Mbps, but tighten GOP.** Current `-g 30` = 0.5 s keyframe interval. For low latency, use `-g 60` (1 s) or `-g 120` (2 s) to reduce keyframe bandwidth spikes, OR `-g 1` only if you want instant recovery and can afford ~2x bitrate. Keyframes cause 30-60 ms decode stalls on phone. Recommended: **`-g 60 -keyint_min 60`** with scene-cut disabled (`-sc_threshold 0`) to avoid surprise I-frames. Saving **0-10 ms** worst-case.
- **P0 Disable B-frames: already `-bf 0`. Keep.** B-frames add 1 frame latency minimum.
- **P0 CBR + small VBV buffer.** Current `-bufsize 2500k` (500 ms at 5 Mbps) is huge. For low latency set **`-bufsize 500k`** (100 ms) or even `250k`. Smaller buffer = less queuing in encoder. Saving **5-15 ms**.
- **P0 Lookahead off.** `-tune ull` already disables lookahead, but explicitly add `-rc-lookahead 0` and `-delay 0` (NVENC). Also `-no-scenecut 1`.
- **P0 NVENC zerolatency.** `-zerolatency 1` is set. Also add `-forced-idr 1`? No—avoid. Add `-async_depth 1`? NVENC option; in ffmpeg use `-delay 0`.
- **P1 Preset.** `p1` is the fastest (lowest latency) NVENC preset. Keep. `p2` adds ~1 ms for slightly better quality. Stay `p1`.
- **P1 Tune.** `ull` (ultra low latency) is correct. `ll` is slightly higher quality. Keep `ull`.
- **P1 Bitrate.** 5 Mbps for 1280x576@60 is plenty. Drop to **3 Mbps** if phone Wi-Fi/USB allows; lower bitrate reduces queueing and decode time. But NVENC CBR at 3M still fine quality. Try 3M, check artifacts.
- **P1 H.264 vs H.265.** H.264 decode is faster and more universally low-latency on Android (MediaCodec AVC path is better optimized, lower latency). H.265 gives ~30% better quality at same bitrate but decode latency is typically 1.5-2x. **Keep H.264.** Only switch to HEVC if bandwidth-constrained and phone has a good low-latency HEVC decoder.
- **P2 NVDEC not needed on phone** (we decode with MediaCodec, which uses the SoC decoder = NVDEC-equivalent). Nothing to change.

### Recommended ffmpeg args
```
-hide_banner -loglevel warning -nostdin
-fflags nobuffer -flags low_delay
-f lavfi -i ddagrab=output_idx=0:framerate=60:video_size=1280x576:offset_x=X:offset_y=Y:draw_mouse=0
-c:v h264_nvenc -preset p1 -tune ull -rc cbr
-b:v 3M -maxrate 3M -bufsize 250k
-g 60 -keyint_min 60 -sc_threshold 0 -bf 0
-rc-lookahead 0 -delay 0 -zerolatency 1
-pix_fmt yuv420p -f h264 pipe:1
```
Expected encode latency: **2-4 ms**.

---

## 3. Transport

### Current
TCP over `adb reverse tcp:27183`. PC `TcpListener` on loopback, `client.NoDelay = true`. Android `socket.setTcpNoDelay(true)`. Buffered streams 64KB/16KB. PC locks SendLock per packet and Flushes.

### Recommendations

- **P0 TCP_NODELAY already set on both sides. Good.** Also set on the TouchForwarder socket (already `NoDelay=true`). Keep.
- **P0 Reduce per-packet Flush overhead.** PC calls `stream.Flush()` after every NAL and audio packet. Flushing every 5-byte header+payload forces many small syscalls. Instead, rely on the OS socket buffer; only flush at frame boundaries or remove Flush entirely (TCP_NODELAY already disables Nagle). Removing Flush can save **1-3 ms** of syscall overhead. But ensure the reader on Android uses a large BufferedInputStream (already 64KB) so it doesn't block.
- **P0 Shrink PC send buffer / disable Nagle** already. Set socket send/receive buffers explicitly: `client.SendBufferSize = 256*1024; client.ReceiveBufferSize = 256*1024` on PC; Android `socket.setSendBufferSize`/`setReceiveBufferSize`.
- **P0 ADB reverse overhead.** `adb reverse` tunnels TCP over USB via ADB daemon. Typical additional latency 0.5-2 ms. USB 2.0 (480 Mbps) is enough for 3 Mbps + audio + touch. Ensure USB connection is USB 3 / good cable; avoid USB hubs. Use `adb reverse` (device->host) as done; do not switch to `adb forward` (would route phone->PC differently).
- **P1 UDP over ADB is not practical.** ADB only tunnels TCP. To use UDP you'd need `adb forward` of a UDP port (supported since Android 11? `adb forward` tcp only historically; UDP forwarding added later) or a direct network. Stick with TCP; with NODELAY + small buffers it's fine for 3 Mbps.
- **P1 Chunking / framing.** Current framing (4-byte length + type) is fine. Avoid sending SPS/PPS every frame; only on configure. Already done (PC only forwards type 7/8/5/1).
- **P1 Disable Android BufferedOutputStream batching delay.** `BufferedOutputStream` with 16KB buffer delays small packets. For touch/input, bypass buffering or flush. For video, the reader drains continuously. Consider unbuffered or small buffer for the output stream on Android. But PC->phone direction is input (touch) only on that socket? Actually the same socket carries video PC->phone and touch phone->PC. The Android output stream is touch/input; buffer it small (1KB) and flush. PC's input is video; large buffer fine.
- **P2 Keep-alive / ping.** Current 1 Hz ping is fine. Use it to measure RTT and log.

### Expected transport latency: **1-3 ms**.

---

## 4. Decoding (Android MediaCodec)

### Current
`MediaCodec.createDecoderByType("video/avc")`, format with `csd-0/1`, `KEY_LOW_LATENCY=1` (API 30+), `KEY_PRIORITY=0`, `KEY_OPERATING_RATE=fps`. Queue `LinkedBlockingQueue(4)`. Polls output with timeout 0. Feed uses `dequeueInputBuffer(5000)`.

### Recommendations

- **P0 Reduce queue depth from 4 to 1 (or 2).** A queue of 4 frames at 60 fps = 66 ms of added latency. Set `LinkedBlockingQueue<>(1)` and drop on overflow (already drops by clearing). Better: queue size 1 + always replace latest (drop oldest). Saving **33-50 ms**. Biggest single win.
- **P0 Frame dropping on decode.** If `dequeueOutputBuffer` returns multiple frames, release all but the last (render only latest). Current loop already drains all available output buffers; but it renders each (`releaseOutputBuffer(outIndex, true)` for every). Change: count available, render only the last, drop earlier ones (release with false/render=false). Saving **up to 16 ms**.
- **P0 PTS handling.** Current clamps `pts <= lastPtsUs` to `lastPtsUs + 16666`. This can cause decoder to hold frames. Instead, pass through the PC PTS (already monotonic) and let MediaCodec use it. Remove the clamp; if non-monotonic, drop. Also consider `queueInputBuffer(..., pts, 0)` with real PTS.
- **P0 KEY_LOW_LATENCY.** Already set on API 30+. Also set the string `"low-latency"=1` (same). Keep. On Android 12+, also try vendor options via `MediaCodec.getSupportedVendorParameters()` (Moonlight approach) for Qualcomm (`vendor.qti-ext-dec-low-latency.enable`), MediaTek (`vdec-lowlatency`), Exynos, Amlogic. Big win on those SoCs (5-15 ms).
- **P0 KEY_PRIORITY=0 (realtime)** already set. Keep. Note: on some devices this crashes if operating rate too high; Moonlight disables operating-rate on non-Qualcomm. Safer: set `KEY_PRIORITY=0` always; set `KEY_OPERATING_RATE` only on Qualcomm. Or remove operating rate entirely (default). Test.
- **P1 SurfaceView vs TextureView.** SurfaceView has lower latency and better performance (direct to SurfaceFlinger, no GPU compositing). Keep SurfaceView. TextureView adds 1 frame. Already using Surface. Good.
- **P1 Disable vsync / Choreographer waiting.** Ensure the app doesn't wait for vsync before feeding. Current decode thread polls with 0 timeout. Keep. Also disable any `View.setKeepScreenOn`? Not relevant.
- **P1 Render timing.** Use `releaseOutputBuffer(outIndex, renderTimestampNs)`? No, `releaseOutputBuffer(index, true)` renders ASAP. Keep true.
- **P2 Codec selection.** Prefer hardware decoder; `createDecoderByType` picks first. On some devices a software decoder may be chosen. Enumerate and pick hardware. Moonlight logic. Minor.

### Expected decode latency after tuning: **4-10 ms**.

---

## 5. Touch injection

### Current
Phone sends touch over the same TCP socket (inputQueue on Android, forwarded to TouchServer on emulator via adb forward 27184). TouchServer (app_process, root) injects. PC logs first 8 touches.

### Recommendations

- **P0 Batching MOVE events.** Android's `MotionEvent` for ACTION_MOVE already batches multiple historical points (`getHistoricalX/Y`). Current code sends only the latest point per ACTION_MOVE, discarding intermediate points. For smoother/low-latency feel, either send every historical point as separate events or accept latest-only (lowest latency, slightly less smooth). Recommendation: send latest only (current) for lowest latency; batch historical only if stutter. Already okay.
- **P0 Reduce input queue latency.** Android `inputQueue` (LinkedBlockingQueue 64) is fine; but `sendLoop` takes one packet at a time and flushes. Set flush only on DOWN/UP; coalesce consecutive MOVEs (drop older MOVE if a newer MOVE is queued). Saving **1-5 ms** and reducing TouchServer load.
- **P0 TouchServer injection.** `InputManager.injectInputEvent` with `INJECT_INPUT_EVENT_MODE_ASYNC` is fastest (doesn't wait). Ensure TouchServer uses ASYNC mode. If it uses WAIT_FOR_RESULT/WAIT_FOR_FINISHED, switch to ASYNC. Saving **5-15 ms**.
- **P0 Coordinate mapping.** Current maps `x = e.getX(i)/viewW` then PC multiplies by `_androidW`. Ensure `_androidW/H` match emulator resolution. Already reads via `wm size`. Good. Avoid floating point surprises: round to int on injection.
- **P1 Multi-touch edge cases.** Pointer IDs in Android can exceed 255? Usually <10. Current casts to byte. Fine. Pointer down/up changed index handling: current sends `changed` only for pointer down/up. TouchServer must map `changed` to the correct pointer index in the full pointer list. Verify TouchServer handles multi-pointer add/remove without resetting all pointers (common bug causing flicker). Test with 2-3 fingers.
- **P1 TouchServer process priority.** Run with `nice -n -10` or set thread priority to urgent. Reduces injection jitter.
- **P1 Bypass adb forward hop.** Currently phone->PC touch goes over the main socket (USB) then PC->emulator over `adb forward tcp:27184`. The PC->emulator hop is localhost adb forward (~0.5 ms). Could instead have phone send touch directly to emulator? No, phone can't reach emulator. Keep. But ensure the PC TouchForwarder socket uses TCP_NODELAY (done) and no buffering.

### Expected touch round-trip (phone touch -> emulator sees it): **10-25 ms**.

---

## 6. Audio

### Current
NAudio WASAPI loopback -> PCM 48k stereo 16-bit, 20 ms frames, over same TCP socket. Android AudioTrack MODE_STREAM, buffer max(minBuffer, 2 frames).

### Recommendations

- **P0 Switch PCM to Opus.** PCM 48k stereo = 1.5 Mbps. Opus at 48-64 kbps gives near-transparent quality and reduces transport queueing. Concentus.dll is already in out2 (Opus encoder available). Use it. Saving bandwidth **~1.4 Mbps** and reduces head-of-line blocking on the shared TCP socket. Latency: Opus frame 20 ms + 5 ms encode = ~25 ms. Acceptable. Or keep 10 ms frames for lower latency (Opus supports 2.5/5/10/20/40/60 ms). Use 10 ms frames for lower latency. But 10 ms increases packet rate. Compromise: **20 ms frames, Opus 64 kbps**.
- **P0 Reduce AudioTrack buffer.** Current `Math.max(minBuffer, PACKET_BYTES*2)` = at least 40 ms. minBuffer on Android is often 20-40 ms. Set buffer to `minBuffer` only (not *2) and write in 20 ms chunks. Reduces audio latency from ~60 ms to ~30 ms. Saving **20-30 ms**.
- **P0 AudioTrack performance mode.** Set `AudioTrack.Builder.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)` (API 26+). Also `setTransferMode(MODE_STREAM)`. Saving **5-10 ms**.
- **P1 Sync with video.** Use a common clock: stamp audio packets with PTS (PC capture time) and have Android schedule playback. Simpler: keep audio free-running (current) with small jitter buffer (2 frames). Add a 2-frame jitter buffer on Android to absorb TCP jitter; drop if >3 frames queued. Current queue cap 16 is too large (320 ms). Set to **4**. Saving perceived sync.
- **P1 WASAPI loopback latency.** `WasapiLoopbackCapture` default buffer ~100 ms. Set `AudioClientStreamFlags`? NAudio: use `WasapiLoopbackCapture` with smaller buffer? NAudio doesn't easily expose. Alternative: use `WasapiCapture` with `AudioClientShareMode.Shared` and a small buffer. Or capture via ffmpeg's `dshow`/`wasapi` input with `-audio_buffer_size 20`. Recommend moving audio capture into the same ffmpeg process or a separate low-latency capture. Harder. Keep NAudio but reduce `BufferedWaveProvider.BufferDuration` from 300 ms to **100 ms**.
- **P2 Consider separate socket for audio** to avoid head-of-line blocking behind a large video frame. Or send audio on the same socket but with smaller framing; TCP ordering means a dropped video frame delays audio. For 3 Mbps this is rare. P2.

### Expected audio latency after tuning: **30-50 ms** (was ~80-120 ms).

---

## 7. Measurement (glass-to-glass)

### Methods

1. **High-speed camera (ground truth).** Place a millisecond counter (e.g., a phone stopwatch app or an on-screen millisecond clock on PC) in front of the emulator window region being captured, and film both the PC screen and the Android phone screen with a 240/480 fps camera (or a phone's 240 fps slo-mo). Count frames between the digit change on PC and the same digit appearing on phone. Resolution: 1/240 s = 4.2 ms. This is the only true glass-to-glass number.
   - Practical: use a web page with `performance.now()` ms counter on PC inside the captured region; film with iPhone 240fps.
2. **Software timestamps (approximate).** Stamp each video frame on PC with capture time `T_cap` (embed as overlay text via ffmpeg `drawtext` or in NAL metadata). On Android, record `System.nanoTime()` when `releaseOutputBuffer` renders. RTT/offset between clocks via the existing ping/pong (PC sends nanoTime, phone echoes; compute offset). End-to-end = `T_render_phone - T_cap_pc - clock_offset`. Accuracy ~5-10 ms. Good for A/B tuning.
3. **Input latency.** Touch phone -> emulator sees it: instrument TouchServer to log injection time; phone logs send time; correlate via the main socket ping. Or simpler: tap a button that changes a visible pixel on emulator; measure with high-speed camera phone->PC screen.
4. **Per-stage profiling.** Add timestamps in PC OnNal (encode done), Android readLoop (received), feed (decode input), decodeLoop (output). Log deltas. This isolates which stage is slow.

### Recommended target metrics
- Capture+encode (PC): < 8 ms
- Transport (one-way): < 3 ms
- Decode+render (Android): < 10 ms
- Display scanout: < 17 ms (60 Hz)
- **Total video: < 40 ms**
- Touch round-trip: < 25 ms
- Audio: < 50 ms

---

## Prioritized quick-win list (no rearchitecture)

1. Android queue depth 4 -> 1 + render-only-last-frame. **-40 to -60 ms.** (VideoClient.java)
2. AudioTrack buffer *2 -> *1 + PERFORMANCE_MODE_LOW_LATENCY. **-20 to -30 ms.** (AudioPlayer.java)
3. NVENC `-bufsize 2500k` -> `250k`, `-g 30` -> `60`, add `-sc_threshold 0 -rc-lookahead 0 -delay 0`. **-5 to -15 ms.** (FfmpegCapture.cs)
4. Remove PC `stream.Flush()` per packet (or flush per frame). **-1 to -3 ms.** (Program.cs Send)
5. PCM -> Opus 64 kbps, 20 ms frames; reduce NAudio buffer 300->100 ms. **-1.4 Mbps + -20 ms audio.** (AudioCapture.cs)
6. Android decode: set `"low-latency"=1` + `KEY_PRIORITY=0`; add vendor options for the SoC. **-5 to -15 ms.** (VideoClient.java tryConfigure)
7. Coalesce touch MOVEs in Android sendLoop + ensure TouchServer uses ASYNC injection. **-5 to -15 ms touch.** (VideoClient.java / TouchServer.java)
8. Capture at native 1280x576 (no scale filter). **-3 to -6 ms.** (FfmpegCapture.cs + emulator window)

Cumulative expected: video glass-to-glass **~30-45 ms**, touch **~15-25 ms**.

---

## Notes on what NOT to do

- Do not switch to H.265 unless bandwidth-constrained; decode latency higher on most phones.
- Do not enable B-frames.
- Do not increase queue depth "for smoothness" — it directly adds latency.
- Do not use TextureView over SurfaceView.
- Do not use UDP over ADB (not supported; TCP with NODELAY is fine).
- Dirty-rect capture rarely helps for full-screen games.

---

## Files to touch

- `pc/CLGame2Phone.Agent/FfmpegCapture.cs` — encoder args, capture size.
- `pc/CLGame2Phone.Agent/Program.cs` — Send flush, buffer sizes.
- `pc/CLGame2Phone.Agent/AudioCapture.cs` — Opus, buffer duration.
- `android/.../VideoClient.java` — queue depth, frame dropping, PTS, low-latency options.
- `android/.../AudioPlayer.java` — buffer, performance mode, queue cap.
- `touchserver/src/.../TouchServer.java` — ASYNC injection, priority.
