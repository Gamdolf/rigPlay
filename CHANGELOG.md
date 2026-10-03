# rigPlay 0.1.0 — unreleased

First rigPlay release, in progress: an Android tablet on a sim-racing rig runs CarPlay from your
iPhone and works with SimHub on the PC.

- Project setup: the SimHub plugin lives beside the Android app in `plugin/`, CI builds and tests
  both, and the PC ↔ tablet protocol is written down with shared fixtures (#1).
- Rebrand from DiPlay: application ID `io.xorob.rigplay`, the BYD head-unit features and the
  automotive module removed, an English-only interface worded for a tablet on a rig (#2).
- SimHub plugin (in progress): a rigPlay page in SimHub, tablet discovery and PIN pairing, the
  dashboard chosen in SimHub pushed to the tablet, music-control properties and actions, and the
  tablet's CarPlay audio played on a PC output device (#3).
- Tablet app (in progress): SimHub link and first-run pairing, a home screen for the rig, a SimHub
  button that shows the chosen dashboard, CarPlay audio streamed to the PC, and media commands from
  wheel buttons (#4).
- Release (planned): a documented local build, an end-to-end rig test checklist, and a tagged
  release with the APK and the plugin zip (#5).
- Versioning restarts at 0.1.0 (Android versionCode 1); the app, the plugin and the release tag
  all read the root `VERSION` file.

# Inherited history

rigPlay is a fork of [DiPlay](https://github.com/shihabal3amri/DiPlay) 0.2.10 by shihabal3amri,
itself based on xcertplay by shilapi. The history of the inherited CarPlay receiver up to the fork
is in the [DiPlay changelog](https://github.com/shihabal3amri/DiPlay/blob/main/CHANGELOG.md).
