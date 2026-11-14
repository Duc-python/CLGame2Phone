# CLGame2Phone

[![Build](https://github.com/Duc-python/CLGame2Phone/actions/workflows/build.yml/badge.svg)](https://github.com/Duc-python/CLGame2Phone/actions/workflows/build.yml)
[![Release](https://github.com/Duc-python/CLGame2Phone/actions/workflows/release.yml/badge.svg)](https://github.com/Duc-python/CLGame2Phone/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Stream a PC emulator (LDPlayer) to an Android phone over USB/ADB with low-latency
touch control. The PC captures the emulator window, encodes H.264 via NVENC, and
streams it to a native Android client that decodes with MediaCodec. Touch events
are forwarded back and injected into the emulator with real multi-touch support.

## Architecture

```
Phone (Android)  <--USB/ADB-->  PC Agent (.NET 10)  -->  LDPlayer (emulator)
   MediaCodec decode              ffmpeg ddagrab + NVENC
   SurfaceView + touch            TouchServer (app_process, root)
   AudioTrack (PCM)               NAudio WASAPI loopback
```

- **PC agent** (`pc/CLGame2Phone.Agent`): window discovery, content-rect capture,
  H.264 1600x720@60 (NVENC `p1/ll`, VBR CQ20), TCP server on `27183`, `adb reverse`, touch forwarding,
  audio streaming, window tracking/restart.
- **Android client** (`android/app`): MediaCodec decode to SurfaceView, auto-reconnect,
  multi-touch packet builder, status overlay, AudioTrack playback.
- **TouchServer** (`touchserver`): runs inside the emulator via `app_process`,
  uses reflection on `InputManager.injectInputEvent` to inject real `MotionEvent`s.

## Requirements

- Windows 10/11 with an NVIDIA GPU (NVENC) — tested on RTX A2000.
- LDPlayer 9 (or BlueStacks 5) with ADB enabled.
- Android phone connected via USB with ADB debugging.
- .NET 10 SDK, JDK 17, Android SDK 34, Gradle 8.10.

## Environment variables

Set these before building/running (paths are examples):

```powershell
$env:JAVA_HOME  = "C:\Program Files\Eclipse Adoptium\jdk-17..."
$env:ANDROID_HOME = "C:\Android\Sdk"   # or ANDROID_SDK_ROOT
$env:PATH = "$env:ANDROID_HOME\platform-tools;$env:PATH"
```

The agent resolves `adb.exe` from `ANDROID_HOME`/`ANDROID_SDK_ROOT`, then falls
back to `C:\Android\Sdk` and PATH.

## Build from source

Build order matters: TouchServer jar first, then agent.

### 1. TouchServer

```powershell
mkdir touchserver\out, touchserver\dex -Force
javac -cp "$env:ANDROID_HOME\platforms\android-34\android.jar" `
  -d touchserver\out touchserver\src\com\clgame2phone\TouchServer.java
d8 --output touchserver\dex touchserver\out\com\clgame2phone\TouchServer.class
# package classes.dex into touchserver\touchserver.jar (zip with classes.dex at root)
cd touchserver\dex; jar cf ..\touchserver.jar classes.dex; cd ..\..
```

### 2. PC agent

```powershell
dotnet build pc/CLGame2Phone.Agent/CLGame2Phone.Agent.csproj -c Release
```

### 3. Android client

```powershell
gradle -p android assembleDebug
# APK: android/app/build/outputs/apk/debug/app-debug.apk
```

## Run

1. Start LDPlayer, set resolution to 1600x720.
2. Connect the phone via USB, enable ADB debugging.
3. Run the PC agent: `pc\CLGame2Phone.Agent\bin\Release\net10.0\CLGame2Phone.Agent.exe`
4. Install & launch the Android client APK on the phone.

## Releases

Tagged releases (`v*`) are built by GitHub Actions and publish:
`CLGame2Phone.Agent.zip`, the debug/release APK, and `touchserver.jar`.
See `.github/workflows/release.yml`.

## Troubleshooting

- **`adb.exe` not found**: install Android platform-tools and set `ANDROID_HOME`,
  or add `platform-tools` to PATH.
- **NVENC init fails**: ensure an NVIDIA GPU with up-to-date drivers; fall back
  to software encode by editing `FfmpegCapture.cs`.
- **Black screen on phone**: emulator resolution must be 1600x720; agent resizes
  the window at startup.
- **Touch not injected**: TouchServer must run as root inside the emulator
  (`app_process`); check agent logs in `logs/`.
- **Audio choppy**: WASAPI loopback device selection; check default playback
  device.

## Notes

- Touch uses direct multi-touch injection (no on-screen buttons/gamepad).
- The emulator window is resized to 1600x720 to match the phone screen 1:1.
- Free Fire / Liên Quân emulator-detection bypass is applied via `setprop` at agent startup.
- Audio is PCM 48 kHz stereo (NAudio WASAPI loopback → AudioTrack).

## License

MIT (see [LICENSE](LICENSE)).