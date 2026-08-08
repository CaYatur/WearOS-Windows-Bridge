# WearOS Windows Bridge

Open-source Windows ↔ Android/Wear OS companion bridge. It mirrors Windows media sessions to an Android MediaSession so Wear OS can display/control PC playback, and provides opt-in Windows companion modules.

## Transport

1. Bluetooth RFCOMM is preferred.
2. If Bluetooth is unavailable, the Android companion can fall back to the paired PC over the same local network.
3. Every application frame is authenticated with a pairing secret. LAN never opens an Internet-facing relay or requires router port forwarding.

## Modules

- Media metadata/control (default on)
- Windows master volume/mute (opt-in)
- Clipboard text sync (opt-in)
- PC status (opt-in)

Each module is independently switchable in Android settings and represented in the shared protocol. Clipboard is deliberately off by default because clipboard contents may be sensitive.

## Repository layout

- `src/Bridge.Protocol` — transport-independent JSON protocol, feature flags and HMAC authentication.
- `src/Bridge.Windows` — Windows host, media/session adapter and LAN server.
- `tests/Bridge.Protocol.Tests` — protocol/security tests.
- `android/` — Android companion skeleton using Media3 MediaSessionService, Bluetooth RFCOMM and LAN fallback.

## Security model

Pairing creates a random 256-bit secret. Messages contain a timestamp, nonce and HMAC-SHA256 signature. Receivers reject expired or invalid messages. Pairing secrets are never committed. LAN fallback is intended only for the local network; Windows Firewall should be scoped to Private networks.

## Development

Windows requires .NET 10 SDK. Android requires Android Studio/JDK 21 and Android SDK. Run Windows tests with:

```text
dotnet test WearOSWindowsBridge.slnx
```

The Android project is intentionally a normal Gradle Android app and can be opened directly in Android Studio.

## Status

This repository is an actively developed MVP. Protocol/authentication and the Windows LAN host are executable/testable. Bluetooth and Android MediaSession integration require real-device validation because neither a paired Wear OS device nor Android emulator is exposed to the coding workspace.

## License

MIT
