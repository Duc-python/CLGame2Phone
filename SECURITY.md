# Security Policy

## Supported versions

Only the latest tagged release receives security fixes.

## Reporting a vulnerability

Please do **not** open a public issue. Use GitHub's "Report a vulnerability"
(private security advisory) on this repository, or contact the maintainer
privately. Include reproduction steps and affected version.

## Secrets and local config

- Never commit `android/local.properties`, keystores, or `*.jks`.
- The agent reads `ANDROID_HOME`/`ANDROID_SDK_ROOT`; do not hardcode SDK paths.
- Run a secret scanner (e.g. `gitleaks`) before publishing; the CI can add a
  `gitleaks/gitleaks-action` step if desired.