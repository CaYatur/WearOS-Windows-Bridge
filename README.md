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
- `src/Bridge.Windows` — Windows tray host, media/session adapter, RFCOMM server, LAN server and companion feature providers.
- `tests/Bridge.Protocol.Tests` — protocol/security tests.
- `android/` — Android companion using Media3 MediaSessionService with Bluetooth-first RFCOMM and automatic LAN fallback.

## Security model

Pairing creates a random 256-bit secret. Messages contain a timestamp, nonce and HMAC-SHA256 signature. Receivers reject expired or invalid messages. Pairing secrets are never committed. LAN fallback is intended only for the local network; Windows Firewall should be scoped to Private networks.

## Development

Windows requires .NET 10 SDK. Android requires Android Studio/JDK 21 and Android SDK. Run Windows tests with:

```text
dotnet test WearOSWindowsBridge.slnx
```

The Android project is intentionally a normal Gradle Android app and can be opened directly in Android Studio.

## Build and pairing

1. Start the Windows tray application. Double-click its tray icon and open **Pairing info**.
2. Pair the Android phone with the PC in Windows/Android Bluetooth settings.
3. In Android, enter the PC's LAN IP, Bluetooth MAC address (optional but required for Bluetooth-first operation), and the displayed pairing key.
4. Enable only the modules you want and tap **Save & start bridge**. Bluetooth is preferred; LAN is used automatically while Bluetooth is unavailable.

Windows release build: `dotnet publish src/Bridge.Windows/Bridge.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`

Android debug APK: run `gradlew assembleDebug` inside `android/` (or use Android Studio).

## Validation status

Windows Release build, self-contained publish, Android debug APK build, and protocol/security unit tests are verified in the development workspace. The remaining validation boundary is physical-device interoperability: a paired Android phone/Wear OS watch was not connected to ADB in the coding workspace, so real Bluetooth radio behavior and the watch vendor's media UI still require a device smoke test before calling a release production-verified.

## License

MIT
