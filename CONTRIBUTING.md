# Contributing to CLGame2Phone

Thanks for your interest. This project has three components; please keep
changes scoped to one component per PR when possible.

## Components

- `pc/CLGame2Phone.Agent` — .NET 10 Windows agent
- `android/` — Android client (Gradle, minSdk 26)
- `touchserver/` — Java TouchServer (built with `javac` + `d8`)

## Prerequisites

- Windows 10/11, .NET 10 SDK, JDK 17, Android SDK 34, Gradle 8.10
- Set `ANDROID_HOME` (or `ANDROID_SDK_ROOT`) and `JAVA_HOME`. The agent
  also honors `ANDROID_HOME` instead of the hardcoded `C:\Android\Sdk`.

## Build

```powershell
# TouchServer (must be built first; produces touchserver/touchserver.jar)
javac -cp "$env:ANDROID_HOME\platforms\android-34\android.jar" `
  -d touchserver/out touchserver/src/com/clgame2phone/TouchServer.java
# package classes.dex -> touchserver/touchserver.jar (see README)

# PC agent
dotnet build pc/CLGame2Phone.Agent/CLGame2Phone.Agent.csproj -c Release

# Android client
gradle -p android assembleDebug
```

## Development workflow

1. Fork and create a feature branch: `git checkout -b feat/my-change`.
2. Keep commits focused; use conventional-ish messages (`feat:`, `fix:`, `docs:`).
3. Run a local build of the affected component before submitting.
4. Open a PR against `main` using the PR template.

## Code style

- C#: follow existing style (nullable enabled, implicit usings).
- Java/Kotlin: 4-space indent, match existing files.
- Gradle: keep `compileSdk`/`targetSdk` in sync with README.

## Reporting bugs

Use the bug report issue template and include: OS, GPU, LDPlayer/BlueStacks
version, phone model, logs from `logs/`.

## Security

Do not commit secrets, keystores, or `local.properties`. See SECURITY notes
in README. Report vulnerabilities privately (see CODE_OF_CONDUCT / security
policy) rather than via public issues.