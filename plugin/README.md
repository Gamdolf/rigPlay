# rigPlay SimHub plugin

The PC side of rigPlay: a SimHub plugin with its own page in SimHub's left menu. It discovers and pairs
rigPlay tablets, pushes the SimHub dashboard they show, and plays their CarPlay audio on this PC (epic #3).

```
plugin/
  RigPlay/          the plugin (net48), builds RigPlay.dll
  RigPlay.Tests/    xunit tests (net8.0) for the plugin's pure logic
  lib/              SimHub 9.12.6 assemblies the plugin compiles against (see lib/README.md)
  scripts/          package-plugin.sh (the release zip), make-icon.py (RigPlay/Resources/icon.png)
  tools/            AudioSender, a tablet-less audio source for testing
  INSTALL.md        install and troubleshooting for users; shipped in the zip
```

## Build and test

Needs the .NET 8 SDK; no Windows, Visual Studio or SimHub install.

```bash
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release   # -> plugin/RigPlay/bin/Release/net48/RigPlay.dll
bash plugin/scripts/package-plugin.sh    # builds, then -> build/rigPlay-plugin.zip (RigPlay.dll + INSTALL.md)
```

The release workflow publishes the same zip (`package-plugin.sh --no-build --out dist/rigPlay-plugin.zip`).
It holds no SimHub assemblies and no `.pdb`: SimHub ships everything the plugin references.

The plugin targets .NET Framework 4.8 through the `Microsoft.NETFramework.ReferenceAssemblies` package and
references WPF as plain assemblies, without `UseWPF` or XAML: the page is built in C# and picks up SimHub's own
controls and styles at runtime. That keeps the build cross-platform. The files the tests compile
(`ProtocolDefaults.cs`, `RigPlaySettings.cs`, `Theme.cs` and everything under `Core/`, `Protocol/` and `Net/`)
must stay free of SimHub and WPF types; SimHub-facing glue lives in `RigPlay.cs`, `PluginBridge.cs` and the page.

## Tablet server

The plugin implements [`docs/protocol.md`](../docs/protocol.md); `RigPlay.Tests/ProtocolFixturesTests.cs` checks the
codec against every file in [`protocol/fixtures/`](../protocol/fixtures/).

| Part | File | What it does |
|---|---|---|
| Messages | `Protocol/Messages.cs` | Typed messages and `MessageCodec` (decode, validate, encode). |
| Audio header | `Protocol/AudioHeader.cs` | The 12-byte datagram header codec (§10.2). |
| Beacon | `Net/DiscoveryBeacon.cs` | UDP 23710 every second, to each interface's directed broadcast and 255.255.255.255. |
| Control server | `Net/ControlServer.cs`, `Net/ClientSession.cs` | TCP 23711 (configurable), newline JSON, hello/welcome, heartbeats, 5 s watchdog, one session per tablet, `shutdown` on exit, LAN peers only. |
| Pairing | `Pairing/PairingService.cs` | PIN shown on the page (6 digits, 120 s, single use, 3 attempts, 5 starts/min), token issue and resume. Only the token's SHA-256 is stored (`PairedTablets[].TokenHash`). |
| Dashboards | `Dashboards/DashboardCatalog.cs`, `Dashboards/WebDashProbe.cs` | Lists `<SimHub>\DashTemplates\*` (title from `<name>.djson.metadata`), builds `http://<ip>:<port>/Dash#<name>` (spec §11; `/Dash#<name>` is what SimHub's own dashboard list links to, `/dashboard/<name>` is a 404 on SimHub 9.12.6) with the address the tablet reached the PC on, probes `http://127.0.0.1:<port>/` every 10 s. The port is the page's override, else SimHub's `SimHubWebPort` setting, else 8888. |
| Host | `Core/RigPlayHost.cs` | Runs the above inside SimHub without depending on it; the page and the SimHub glue read it. |

## SimHub surface

What dashboards and input mappings can use (docs/protocol.md §16). Properties describe the *primary tablet*: the
paired tablet whose iPhone connected most recently, else the one that paired most recently (§12). Actions send a
`command` to it; with no primary tablet they do nothing (logged at debug level). The logic is in
`Core/SimHubSurface.cs` (`PrimaryTabletSelector`, `PlaybackClock`, `SurfaceActions`); `PluginBridge.cs` registers it.

| Name | Kind | Type | Value / effect | Without data |
|---|---|---|---|---|
| `RigPlay.TabletConnected` | property | bool | At least one paired tablet is connected (any tablet, not only the primary). | `false` |
| `RigPlay.PhoneConnected` | property | bool | `status.phoneConnected` | `false` |
| `RigPlay.Screen` | property | string | `carplay`, `dashboard`, `idle` or `off` (`status.screen`) | `off` |
| `RigPlay.NowPlaying.Title` | property | string | `status.nowPlaying.title` | `""` |
| `RigPlay.NowPlaying.Artist` | property | string | `status.nowPlaying.artist` | `""` |
| `RigPlay.NowPlaying.Album` | property | string | `status.nowPlaying.album` | `""` |
| `RigPlay.NowPlaying.App` | property | string | `status.nowPlaying.app` | `""` |
| `RigPlay.NowPlaying.Playing` | property | bool | `status.nowPlaying.playing` | `false` |
| `RigPlay.NowPlaying.Position` | property | double, s | Last `position` plus the time since that status arrived while playing, clamped to the duration. | `0` |
| `RigPlay.NowPlaying.Duration` | property | double, s | `status.nowPlaying.duration` (`0` for live streams) | `0` |
| `RigPlay.PlayPause` | action | | `command media playPause` | |
| `RigPlay.NextTrack` | action | | `command media next` | |
| `RigPlay.PreviousTrack` | action | | `command media previous` | |
| `RigPlay.Siri` | action | | `command media siri` | |
| `RigPlay.ShowDashboard` | action | | `command showDashboard` | |
| `RigPlay.ShowCarPlay` | action | | `command showCarPlay` | |
| `RigPlay.ToggleScreen` | action | | `showCarPlay` when the last `status.screen` is `dashboard`, otherwise `showDashboard` | |

Bind an action in SimHub under Controls and events → the rigPlay entries; use a property in a dashboard as
`[RigPlay.NowPlaying.Title]`.

## Notes

A port that cannot be bound is shown in the page's Status section and logged; the plugin keeps running. To poke the
server by hand: `nc <pc> 23711`, then type
`{"type":"hello","tabletId":"nc","name":"nc","appVersion":"0","protocol":1}` and Enter; the plugin answers `welcome`.

## Install

[INSTALL.md](INSTALL.md) has the user steps. In short: copy `RigPlay.dll` next to `SimHubWPF.exe`
(`C:\Program Files (x86)\SimHub\`), `Unblock-File` it, start SimHub and accept the "new plugin found"
prompt. **rigPlay** then appears in the left menu. Settings are stored in
`PluginsData\Common\RigPlay.RigPlaySettings.json`; log lines are prefixed `[rigPlay]` in SimHub's log.

## Audio receiver (#24)

`RigPlay/Audio/` receives the tablet's audio datagrams (docs/protocol.md §10) on the audio UDP port and plays
them through NAudio's `WasapiOut` (shared mode) on the device picked on the page:

- `AudioHeader.cs` is the 12-byte header codec, tested against `protocol/fixtures/audio-header.json`; `OpusToc`
  in it reads an Opus packet's TOC byte so an opus datagram is checked and placed without a decoder.
- `OpusSupport.cs` decodes the optional Opus format (§10.4) with Concentus, the managed libopus, which ships as
  `Concentus.dll` next to `RigPlay.dll` (the one assembly SimHub does not have). Everything that touches it is
  behind `NoInlining`, so a plugin installed without the DLL still loads and offers PCM only.
- `JitterBuffer.cs` holds one stream: placed by timestamp, 80 ms target, 200 ms maximum, silence for gaps and
  underruns, late and duplicate datagrams dropped, reset on the start flag.
- `AudioReceiver.cs` runs the socket and the stream lifecycle. A stream starts with the `audioStart` of a paired
  tablet and belongs to that tablet's IP; datagrams from any other source are dropped and counted as rejected.
  It stops on that tablet's `audioStop`, when its session closes, or (from the mix) after 2 s without datagrams.
  `OpusEnabled` (the **Opus compression** setting, off by default) puts `opus` before `pcm_s16le` in
  `state.audio.formats` and lets `audioStart` name it; an opus stream decodes each packet on the receive thread
  into the same jitter buffer.
- `AudioGlue.cs` connects the receiver to the control server: `audioStart`/`audioStop`/session loss, the
  paired-IP source filter, and `state.audio` (`enabled` while the audio port is bound, even with no output
  device; the tablet then streams and the page shows that nothing plays). Changing the audio port on the page
  rebinds the receiver and pushes the new `state.audio.port`.
- `AudioOutput.cs` mixes the streams at 48 kHz stereo float (resampling where needed), ducks media by 12 dB
  while Siri or a call plays, applies volume and mute live, and follows device removal back to the Windows
  default. Without any output device it keeps pulling the mix in real time so the stats stay live, and logs
  that once.
- `AudioMath.cs`, `AudioStats.cs`, the receiver and the glue are pure and unit-tested; `AudioOutput.cs` and
  `AudioSection.cs` (the page section) need Windows and are not.

`tools/AudioSender/` streams a WAV file or a tone as spec datagrams, PCM or Opus, for testing without a tablet:

```bash
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 music.wav --loss 5
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 --tone 440 --seconds 10
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 music.wav --opus 96
```

The plugin plays these only after a tablet on the sender's address has paired and sent `audioStart` for the
stream (for example a scripted fake tablet on the same PC, then the sender to 127.0.0.1); anything else shows
up as "rejected" in the Audio section's datagram counter.
