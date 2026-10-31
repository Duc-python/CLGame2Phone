# CLGame2Phone

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
  H.264 1280x576@60, TCP server on `27183`, `adb reverse`, touch forwarding,
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

## Build

### PC agent
```powershell
dotnet build pc/CLGame2Phone.Agent/CLGame2Phone.Agent.csproj -c Release
```

### Android client
```powershell
$env:JAVA_HOME="D:\CLGame2Phone\tools\jdk17\jdk-17.0.20.1+1"
$env:ANDROID_HOME="C:\Android\Sdk"
gradle -p android assembleDebug
```

### TouchServer
```powershell
javac -cp $env:ANDROID_HOME\platforms\android-34\android.jar -d touchserver/out touchserver/src/com/clgame2phone/TouchServer.java
d8 --output touchserver/dex touchserver/out/com/clgame2phone/TouchServer.class
# package classes.dex into touchserver/touchserver.jar
```

## Run

1. Start LDPlayer, set resolution to 1600x720.
2. Connect the phone via USB, enable ADB debugging.
3. Run the PC agent: `pc/CLGame2Phone.Agent/bin/Release/net10.0/CLGame2Phone.Agent.exe`
4. Install & launch the Android client APK on the phone.

## Notes

- Touch uses direct multi-touch injection (no on-screen buttons/gamepad).
- The emulator window is resized to 1600x720 to match the phone screen 1:1.
- Free Fire / Liên Quân emulator-detection bypass is applied via `setprop` at agent startup.
- Audio is PCM 48 kHz stereo (NAudio WASAPI loopback → AudioTrack).

## License

MIT