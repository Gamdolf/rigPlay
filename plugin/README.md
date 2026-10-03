# rigPlay SimHub plugin

The PC side of rigPlay: a SimHub plugin with its own page in SimHub's left menu. It will discover and pair
rigPlay tablets, push the SimHub dashboard they show, and play their CarPlay audio on this PC (epic #3).
This is the skeleton: the page, settings persistence and logging.

```
plugin/
  RigPlay/          the plugin (net48), builds RigPlay.dll
  RigPlay.Tests/    xunit tests (net8.0) for the plugin's pure logic
  lib/              SimHub 9.12.6 assemblies the plugin compiles against (see lib/README.md)
  scripts/          make-icon.py, which draws RigPlay/Resources/icon.png
```

## Build and test

Needs the .NET 8 SDK; no Windows, Visual Studio or SimHub install.

```bash
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release   # -> plugin/RigPlay/bin/Release/net48/RigPlay.dll
```

The plugin targets .NET Framework 4.8 through the `Microsoft.NETFramework.ReferenceAssemblies` package and
references WPF as plain assemblies, without `UseWPF` or XAML: the page is built in C# and picks up SimHub's own
controls and styles at runtime. That keeps the build cross-platform. The files the tests compile
(`ProtocolDefaults.cs`, `RigPlaySettings.cs`, `Theme.cs`) must stay free of SimHub and WPF types.

## Install

Copy `RigPlay.dll` into SimHub's install folder (`C:\Program Files (x86)\SimHub\`), restart SimHub and accept
the "new plugin found" prompt. **rigPlay** then appears in the left menu. Settings are stored in
`PluginsData\Common\RigPlay.RigPlaySettings.json`; log lines are prefixed `[rigPlay]` in SimHub's log.

## Audio receiver (#24)

`RigPlay/Audio/` receives the tablet's audio datagrams (docs/protocol.md §10) on the audio UDP port and plays
them through NAudio's `WasapiOut` (shared mode) on the device picked on the page:

- `AudioHeader.cs` is the 12-byte header codec, tested against `protocol/fixtures/audio-header.json`.
- `JitterBuffer.cs` holds one stream: placed by timestamp, 80 ms target, 200 ms maximum, silence for gaps and
  underruns, late and duplicate datagrams dropped, reset on the start flag.
- `AudioReceiver.cs` runs the socket and the stream lifecycle. The control server calls
  `RigPlay.Audio.OnAudioStart(stream, format, sampleRate, channels)`, `OnAudioStop(stream)` and `OnLinkLost()`;
  until it does, a datagram with the start flag starts its stream (fallback). A stream leaves the mix after
  2 s without datagrams.
- `AudioOutput.cs` mixes the streams at 48 kHz stereo float (resampling where needed), ducks media by 12 dB
  while Siri or a call plays, applies volume and mute live, and follows device removal back to the Windows
  default. Without any output device it keeps pulling the mix in real time so the stats stay live, and logs
  that once.
- `AudioMath.cs`, `AudioStats.cs` and the receiver are pure and unit-tested; `AudioOutput.cs` and
  `AudioSection.cs` (the page section) need Windows and are not.

`tools/AudioSender/` streams a WAV file or a tone as spec datagrams, for testing without a tablet:

```bash
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 music.wav --loss 5
dotnet run --project plugin/tools/AudioSender -- 127.0.0.1 23712 --tone 440 --seconds 10
```
