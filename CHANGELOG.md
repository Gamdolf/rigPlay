# rigPlay 0.2.0 — unreleased

- Audio to the PC: no more dropouts on a Wi-Fi link that stalls. The second rig test still cut out
  (18 underruns and 10 skips in 100 s at 0 % loss): the buffer's target was capped at 250 ms and it threw
  the depth away above 500 ms, so every stall of 300–650 ms was a dropout, often followed by a skip. The
  jitter buffer now measures each stall when the held-back datagrams arrive and raises its target to a
  quarter more than the stall (up to 2 s for music, 1.5 s for Siri, 1 s for calls), waits for that depth
  before resuming, keeps the learned depth in the settings across SimHub restarts (the Audio section
  shows it, with a Forget button and the minimum to start from), trims excess depth by playing 1.5 %
  faster instead of skipping, and only skips a full second above the target. A pause on the phone no
  longer counts as a stall. The page shows the longest stall per stream, and the tablet logs every 10 s
  whether its Wi-Fi held the datagrams back or it produced them late.
- Other voices lower or pause the music (#58): the plugin watches the audio sessions of the programs
  listed in the Audio section ("Other voices", CrewChiefV4 by default) and, while one of them talks,
  lowers the CarPlay music to the chosen volume (25 % by default, the same ramp as for Siri) or, in
  "Pause the music" mode, toggles play/pause on the phone and toggles back when it is quiet again. The
  page shows which program is talking.
- Idle dashboard (v2): the plugin's "Idle dashboard" selector pushes a dashboard the tablet shows while
  no iPhone is connected, with a built-in offline idle screen when the PC is off and after a period of
  inactivity (#38, #39, #53).
- Data to CarPlay (v2): SimHub telemetry goes to the phone as the car's position, speed, heading,
  gear, night mode and vehicle status, at up to 10 Hz with per-field switches; the position comes from
  one of three fake-GPS strategies (fixed origin, dead reckoning, or the real circuit via the shipped
  track table and a recorded lap), chosen in the "Data to CarPlay" section and off by default
  (#40–#46). Route guidance and now-playing artwork are exposed as SimHub properties (#47).
- Microphone to the phone (v2): with the tablet's Microphone setting on "PC via SimHub", Siri and
  callers hear the PC's input device chosen in the Microphone section (#34), with an automatic boost
  (on by default, up to +20 dB, cap adjustable 0–30 dB; or a fixed boost with Automatic off) so your
  voice reaches a level Siri hears well; the level meter shows the boost in effect.
- Dashboard screen: SimHub's web dash toolbar and swipe help are hidden, so the dashboard fills
  the tablet without tapping Fullscreen (#50); the page loads as soon as SimHub names a dashboard and
  stays loaded between opens (#51); SimHub's own icon is used on the SimHub button and the CarPlay
  car icon, fetched once from the web dash server (#52).

# rigPlay 0.2.0-rc.1 — 2026-10-03

Second pre-release, for testing on a rig. Android versionCode 2, so it installs over 0.1.0-rc.1 when
both are signed with the same key.

- Opus audio compression as an option, off by default (protocol §10.4). **Opus compression** on the
  rigPlay page's Audio section makes the plugin offer `opus` before `pcm_s16le`; the tablet then
  encodes with Android's Opus encoder (20 ms packets, 96 kbit/s stereo or 48 kbit/s mono, about
  0.1 Mbit/s instead of 1.5) and falls back to PCM on a device without an encoder. The plugin decodes
  with Concentus, so `rigPlay-plugin.zip` now holds `Concentus.dll` next to `RigPlay.dll`; without it
  the plugin still loads and offers PCM only. The page shows the format of each stream and the packets
  that did not decode.
- Audio to the PC no longer cuts in and out on a tablet on Wi-Fi. The tablet holds a low-latency Wi-Fi
  lock and marks the audio datagrams for the Wi-Fi voice queue while it streams, decodes on an
  audio-priority thread, waits for a busy decoder instead of dropping its packets, and advances the
  datagram clock over audio that never reached it so the PC plays silence there instead of running its
  buffer dry. The plugin's jitter buffer starts at 80 ms, grows on every underrun (up to 250 ms, kept
  across a stream restart) and only skips ahead above 500 ms instead of 200 ms; its WASAPI buffer is
  100 ms. The rigPlay page shows the buffer target, underruns, late datagrams and skips per stream.

- The APK attached to the release, `rigPlay-0.2.0-rc.1.apk`, is built and signed locally and carries
  the same experimental accessory identity DiPlay ships, so it connects to an iPhone; see
  `docs/THIRD_PARTY_NOTICES.md` ("Experimental authentication data") and `SECURITY.md`. APKs built
  by CI carry no identity and cannot connect.

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
