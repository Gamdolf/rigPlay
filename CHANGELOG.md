# rigPlay 0.1.0 — unreleased

- Audio to the PC no longer cuts in and out on a tablet on Wi-Fi. The tablet holds a low-latency Wi-Fi
  lock and marks the audio datagrams for the Wi-Fi voice queue while it streams, decodes on an
  audio-priority thread, waits for a busy decoder instead of dropping its packets, and advances the
  datagram clock over audio that never reached it so the PC plays silence there instead of running its
  buffer dry. The plugin's jitter buffer starts at 80 ms, grows on every underrun (up to 250 ms, kept
  across a stream restart) and only skips ahead above 500 ms instead of 200 ms; its WASAPI buffer is
  100 ms. The rigPlay page shows the buffer target, underruns, late datagrams and skips per stream.

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

- The APK attached to the release, `rigPlay-0.1.0-rc.1.apk`, is built and signed locally and carries
  the same experimental accessory identity DiPlay ships, so it connects to an iPhone. It is not an
  Apple-issued identity; see `docs/THIRD_PARTY_NOTICES.md` ("Experimental authentication data") and
  `SECURITY.md`. APKs built by CI carry no identity and cannot connect; to build your own, see
  `docs/BUILD.md` ("Accessory identity").
- Wireless CarPlay on a tablet that is also on home Wi-Fi is not verified yet (#33); wired USB is
  the fallback.
- The PC microphone is not routed to the phone (#34).

# Inherited history

rigPlay is a fork of [DiPlay](https://github.com/shihabal3amri/DiPlay) 0.2.10 by shihabal3amri,
itself based on xcertplay by shilapi. The history of the inherited CarPlay receiver up to the fork
is in the [DiPlay changelog](https://github.com/shihabal3amri/DiPlay/blob/main/CHANGELOG.md).
