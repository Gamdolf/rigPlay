# Building rigPlay

The repository holds two builds: the Android app (Gradle, at the repository root) and the SimHub plugin
(.NET, in `plugin/`). CI runs both on every pull request (`.github/workflows/ci.yml`).

## Android app

Requirements:

- JDK 25 (what CI uses). JDK 21 also works for local builds.
- Android SDK with platform 37 (`platforms;android-37.0`) and build-tools 36.0.0. Point Gradle at it with
  `ANDROID_HOME` or `sdk.dir` in `local.properties`.
- NDK 28.2.13676358 for the native code in `shared/`. Gradle downloads it on the first build if the SDK
  licences are accepted.
- The included Gradle wrapper (`./gradlew`); no separate Gradle install.

The same commands CI runs:

```sh
./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug
```

Output: `mobile/build/outputs/apk/debug/mobile-debug.apk`.

This APK contains no accessory identity. It installs and runs, but it **cannot connect to an iPhone**.
Tests generate synthetic identities at runtime; no key files are tracked. To build an APK that connects,
see [Accessory identity](#accessory-identity-required-to-connect-to-an-iphone).

## SimHub plugin

Requirements: the .NET 8 SDK. No Windows, Visual Studio or SimHub install is needed; the SimHub
assemblies the plugin compiles against are in `plugin/lib/`.

```sh
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release
```

Output: `plugin/RigPlay/bin/Release/net48/RigPlay.dll`. Install it as described in
[plugin/INSTALL.md](../plugin/INSTALL.md). Details of the plugin's layout are in
[plugin/README.md](../plugin/README.md).

## Version

The root `VERSION` file holds the version (`0.1.0`). `mobile/build.gradle.kts` reads it as the APK's
`versionName`, `plugin/Directory.Build.props` as the plugin's assembly version, and the release
workflow refuses a tag other than `v<VERSION>`. The Android `versionCode` lives in
`mobile/build.gradle.kts` and is bumped by hand for every release.

## Releases from CI

Pushing a tag `v<VERSION>` runs `.github/workflows/release.yml`, which publishes the APK and
`rigPlay-plugin.zip` on a GitHub release. The CI APK is identity-less, so it cannot connect to an
iPhone either. It is signed only when the repository's keystore secrets are set.
