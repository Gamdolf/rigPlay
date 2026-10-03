// SPDX-License-Identifier: GPL-3.0-only
// ProtocolDefaults.cs: the default ports of the rigPlay protocol between the plugin and a tablet.
// Placeholders until docs/protocol.md exists (#12), which will own these numbers; keep the two in step.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
namespace RigPlayPlugin
{
    public static class ProtocolDefaults
    {
        /// <summary>TCP, plugin listens: pairing and control messages from tablets.</summary>
        public const int ControlPort = 18877;

        /// <summary>UDP, plugin listens: tablets broadcast to find the PC running SimHub.</summary>
        public const int DiscoveryPort = 18878;

        /// <summary>The plugin receives the tablet's CarPlay audio here.</summary>
        public const int AudioPort = 18879;

        /// <summary>Lowest port a user may configure; below this are the privileged ports.</summary>
        public const int MinPort = 1024;

        public const int MaxPort = 65535;
    }
}
