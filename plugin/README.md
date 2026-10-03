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
