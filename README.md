# WearOS Windows Bridge

Open-source Windows ↔ Android/Wear OS companion bridge. It mirrors Windows media sessions to an Android MediaSession so Wear OS can display/control PC playback, and provides opt-in Windows companion modules.

## Transport

1. Wi-Fi (LAN) is the dependable path and needs no bonding: the client finds the PC by UDP broadcast, so no IP has to be typed in.
2. Bluetooth RFCOMM is preferred **when it is available**, and takes over automatically when it connects. It requires the PC to be bonded with the device running the app. From a phone that is routine; from a watch it depends on whether the Wear OS build lets you bond the watch itself with a PC, and many do not. If nothing bonded answers, the bridge stays on Wi-Fi — that is the normal path, not a failure.
3. Both transports run at once. Wi-Fi stays warm underneath Bluetooth, so losing the radio costs no reconnect.
4. Every application frame is authenticated with a pairing secret. LAN never opens an Internet-facing relay or requires router port forwarding.

### Protocol version 2

**Update the Windows app and the Android app together.** A v2 peer refuses a v1 peer with `VersionMismatch`, so a new APK against an old tray app — or the reverse — rejects every frame and never connects. Existing pairing keys keep working; the key format did not change, so there is nothing to re-pair.

The signature covers the payload **exactly as transmitted**. Version 1 hashed a re-serialization of the parsed payload on each side, which is not a shared value: `System.Text.Json` writes a `[Flags]` enum as `"media, volume"` where `org.json` writes `15`, and escapes non-ASCII and `+` where `org.json` writes them literally. Every frame failed its signature check in both directions, so the PC never answered and the watch reconnected forever. A v1 client now fails with a clean version mismatch instead.

Both runtimes are pinned against the same byte-exact wire vectors — `GoldenVectorTests` in `tests/Bridge.Protocol.Tests/ProtocolTests.cs` and `BridgeProtocolGoldenTest.kt` in `android/app/src/test/`. They check fixed strings, never each other: a round-trip test passes even when the two sides disagree, which is how the v1 bug survived. Change a vector in one file and you must change it in the other in the same commit.

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

1. Start the Windows tray application.
2. In the Android app, leave every field blank and tap **Save & start bridge**. It finds the PC by broadcast and requests pairing; approve the prompt on the PC and the key is stored automatically. **Pairing info** in the tray menu shows the key if you would rather type it in.
3. Enable only the modules you want, then tap **Save & start bridge** again to apply.
4. Optional, for Bluetooth: bond the PC with the device running the app in Windows/Android Bluetooth settings. No MAC address is needed — every bonded device is tried and the one that answers is remembered.

Windows release build: `dotnet publish src/Bridge.Windows/Bridge.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`

Android debug APK: run `gradlew assembleDebug` inside `android/` (or use Android Studio).

## Troubleshooting

The tray menu has **Open log**. It writes to `%LOCALAPPDATA%\WearOSWindowsBridge\bridge.log` and names the reason a frame was refused — `VersionMismatch` (stale APK), `BadSignature` (wrong pairing key), `StaleTimestamp` (device clocks more than five minutes apart), `ReplayedNonce`. A link that refuses every frame used to be indistinguishable from an idle one. On the device side the same reasons appear in `adb logcat` under the `WearBridge*` tags.

If the watch cannot find the PC: confirm both are on the same Wi-Fi network, that the network is marked **Private** in Windows, and use **Repair firewall** in the tray menu.

## Validation status

Verified on this machine:

- Protocol/security unit tests, both sides: 12 xUnit tests and 11 Kotlin tests, including the shared golden vectors. The Kotlin side produces exactly the signatures the C# side accepts, and accepts the exact bytes C# produces, for a payload carrying Turkish characters and a `+` inside base64 — the two cases v1 corrupted.
- Windows Release build, self-contained single-file publish, and Android debug APK build.
- A live end-to-end run against the real `BridgeHost` using a client that writes frames in the Android wire format: real media metadata read from the running Windows session, a media command answered with fresh state in 31 ms, malformed and replayed frames rejected without dropping the link, and artwork sent once per track (one 37 KB frame followed by 0.6 KB frames carrying only the artwork id).

Not verified, and still needing a device smoke test:

- Anything involving a real radio: RFCOMM bonding, connect/disconnect behaviour, and throughput. The Windows RFCOMM listener starts and registers its SDP record, but no watch or phone was bonded to this PC.
- The Wear OS media UI itself — how the vendor surface renders the session, artwork and transport controls.
- The Android foreground-service lifecycle over hours, including the boot restart path, which Android 12+ may refuse (that refusal is logged, not thrown).

## License

MIT
