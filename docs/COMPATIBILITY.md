# Compatibility

rigPlay is pre-release. It is an independent CarPlay receiver, not an Apple-certified accessory. The
experimental accessory identity it needs is extractable from any APK that bundles it, and iPhones may stop
accepting it after an iOS update (see [Credits and licences](THIRD_PARTY_NOTICES.md)).

| Area | Scope |
| --- | --- |
| Tablet | Android 10 or later, with Wi-Fi Direct and Bluetooth. The APK installs on Android 9 (minimum SDK 28), but wireless needs Android 10. |
| Tablet network | The tablet stays on the home Wi-Fi for the PC link and hosts a Wi-Fi Direct group for the phone at the same time. |
| Wireless CarPlay on tablets | **Being validated** ([#33](https://github.com/xorob0/rigPlay/issues/33)). Not confirmed on any tablet yet. |
| Wired CarPlay | USB with a data cable. The fallback when wireless does not work on a tablet. |
| Phone | A standard iPhone with CarPlay enabled. Compatibility varies by model and iOS version. |
| SimHub | 9.12.6 or later. The plugin is compiled against and tested with 9.12.6. |
| PC | Windows, on the same local network as the tablet. |
| Video | Default H.264 at 30 fps. 60 fps and HEVC demand more from the tablet's decoder. |

## Wireless on tablets

Wireless CarPlay needs the tablet to own the network the phone joins. On a car head unit that is a
Wi-Fi Direct group or the unit's hotspot. On a rig, the tablet must also stay connected to the home Wi-Fi
to reach the PC. Whether a given tablet can host a Wi-Fi Direct group while connected to Wi-Fi as a
client, and keep both stable with music playing, depends on its Wi-Fi chip and firmware. This is being
tested in [#33](https://github.com/xorob0/rigPlay/issues/33); this page will list the tablets and the
configuration that work.

Until then:

- If wireless does not connect or keeps dropping, use **Connect with USB**.
- Both networks share one radio. A 2.4 GHz group may stutter; 5 GHz is preferred where the tablet allows it.
- A radio that can join a 5 GHz network may still refuse to host a 5 GHz Wi-Fi Direct group.

## Known limitations

- The microphone for Siri and calls is the tablet's in v1, not the PC's.
- The control channel and audio between tablet and PC are not encrypted (see [Privacy](PRIVACY.md)).
- Some iPhone and tablet combinations ignore the CarPlay size setting. Reconnecting is implemented; it does
  not guarantee the iPhone uses the requested layout.
- Auto-start on boot depends on the tablet's firmware and battery settings.
- USB needs a data port and a data cable.
- Calls, Siri, long sessions and future iOS releases need more testing.

Reports record requested and actual Wi-Fi frequencies, association state and fallback failures. Wi-Fi
credentials and protocol payloads are excluded. See [Wireless diagnostics](WIRELESS_DIAGNOSTICS.md).

Android references: [SupplicantState](https://developer.android.com/reference/android/net/wifi/SupplicantState),
[explicit P2P operating frequency](https://developer.android.com/reference/android/net/wifi/p2p/WifiP2pConfig.Builder#setGroupOperatingFrequency(int)).
