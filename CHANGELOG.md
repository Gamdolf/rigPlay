# rigPlay 0.1.0 — unreleased

- Dashboard screen: SimHub's web dash toolbar and swipe help are hidden, so the dashboard fills
  the tablet without tapping Fullscreen (#50).
- Dashboard screen: the page loads as soon as SimHub names a dashboard and stays loaded between
  opens, so the SimHub button and the CarPlay car icon show it at once instead of reloading it (#51).

# rigPlay 0.1.0-rc.1 — 2026-10-03

First rigPlay pre-release: an Android tablet on a sim-racing rig runs CarPlay from your iPhone and
works with SimHub on the PC. This build is for testing on a rig; the end-to-end checklist in
`docs/TESTING.md` has not been run on the target tablet yet.

- Project setup: the SimHub plugin lives beside the Android app in `plugin/`, CI builds and tests
  both, and the PC ↔ tablet protocol is written down with shared fixtures (#1).
- Rebrand from DiPlay: application ID `io.xorob.rigplay`, the BYD head-unit features and the
  automotive module removed, an English-only interface worded for a tablet on a rig (#2).
- SimHub plugin: a rigPlay page in SimHub, tablet discovery and PIN pairing, the dashboard chosen
  in SimHub pushed to the tablet, music-control properties and actions, and the tablet's CarPlay
  audio played on a PC output device (#3). Install from `rigPlay-plugin.zip`; see
  `plugin/INSTALL.md`.
- Tablet app: SimHub link and first-run pairing, a home screen for the rig, a SimHub button that
  shows the chosen dashboard, CarPlay audio streamed to the PC, media commands from wheel buttons,
  and the phone released when SimHub goes away (#4).
- Versioning restarts at 0.1.0 (Android versionCode 1); the app, the plugin and the release tag
  all read the root `VERSION` file.

Known limits of this pre-release:

- The APK attached to the release is built by CI: it is unsigned and carries no accessory identity,
  so it cannot connect to an iPhone. Build it yourself with the identity as described in
  `docs/BUILD.md` ("Accessory identity").
- Wireless CarPlay on a tablet that is also on home Wi-Fi is not verified yet (#33); wired USB is
  the fallback.
- The PC microphone is not routed to the phone (#34).

# Inherited history

rigPlay is a fork of [DiPlay](https://github.com/shihabal3amri/DiPlay) 0.2.10 by shihabal3amri,
itself based on xcertplay by shilapi. The history of the inherited CarPlay receiver up to the fork
is in the [DiPlay changelog](https://github.com/shihabal3amri/DiPlay/blob/main/CHANGELOG.md).
