# NFC Lens for Android

Native .NET 10 Android app using the phone's built-in NFC reader. This is a separate UI over the same EMV scanner, TLV decoder, NDEF decoder and learning content used by the Windows app. Android 8.0 (API 26) or later is required.

## Current build status

A standalone debug APK has been built successfully and its APK v2/v3 signatures verified. The convenient installation copy is `apk/NFC-Lens-Android.apk`; the original is under `bin/Debug/net10.0-android/`. Shared core tests and Windows builds also pass. Physical phone behaviour has not yet been device-tested.

## Build an installable APK

### Visual Studio installation

Installing `dotnet workload install android` alone does not add Visual Studio's Android project tooling or install the Java/Android SDKs. In **Visual Studio Installer → Modify**, select **.NET Multi-platform App UI development** and ensure **Android SDK setup** and **OpenJDK (Microsoft distribution)** are selected. Alternatively, import `.vsconfig` from the Installer's **More → Import configuration** action. Close Visual Studio before installation, then reopen `NfcLens.Android.sln` afterward.

If the Error List shows missing `Android`, `Java`, `Activity` and `Resource` types alongside `NETSDK1147`, resolve workload/project loading first. Those messages can cascade from missing Android references. A fresh command-line build is the best way to identify the first active error. `XA5300` specifically means the Android SDK is missing or its directory is undiscovered; use the helper below to install it or provide its path.

Install the .NET 10 SDK and Android workload, either with Visual Studio's Android/.NET tooling or:

```powershell
dotnet workload install android
```

Install the Android SDK and JDK 21. Microsoft's dependency target can provision compatible components into absolute paths of your choice:

The simplest route from the repository root is:

```powershell
.\build.ps1 -InstallDependencies
```

This installs missing components under `.toolchain/` unless existing SDK/JDK environment paths are available, accepts the Android SDK licences and builds a standalone debug APK. Internet access is required for downloads. It prints the signed APK path on success. For an existing custom installation:

```powershell
.\build.ps1 -AndroidSdkDirectory 'C:/your/android-sdk' -JavaSdkDirectory 'C:/your/jdk-21'
```

Equivalent manual commands:

```powershell
dotnet build NfcLens.Android.csproj -t:InstallAndroidDependencies -p:AndroidSdkDirectory=C:/Android/sdk -p:JavaSdkDirectory=C:/Android/jdk -p:AcceptAndroidSdkLicenses=True
dotnet build NfcLens.Android.csproj -c Debug -p:EmbedAssembliesIntoApk=True -p:AndroidSdkDirectory=C:/Android/sdk -p:JavaSdkDirectory=C:/Android/jdk
```

The installable debug package is `bin/Debug/net10.0-android/app.nfclens.mobile-Signed.apk`. If Android tooling inserts an API version into that output directory, find it with `Get-ChildItem bin -Recurse -Filter '*-Signed.apk'`. A debug APK is for local testing, not a production release. Keep a stable, private signing key for future distribution.

With a connected phone and USB debugging enabled:

```powershell
adb install -r bin/Debug/net10.0-android/app.nfclens.mobile-Signed.apk
```

Open `NfcLens.Android.sln` here in Visual Studio, or this folder in VS Code. The layout is editable in `Resources/layout/activity_main.xml`; logic is in `MainActivity.cs`, and NFC adapters are in `PhoneScanner.cs`. The Windows solution remains independent so its builds do not require Android tooling.

## Use

Version 1.0.1 enables reader mode when **Scan** is pressed, avoiding the earlier case where a card presented before Scan could be detected and ignored. **EMV only** polls NFC-A/B with Android's NDEF precheck disabled; Auto retains NDEF discovery for ordinary tags. If a payment card fails, try EMV only, remove the card, press Scan and present it again. Live log now distinguishes tag discovery, ISO-DEP connection, APDU exchange failure and missing ISO-DEP. This is a compatibility change, not confirmation of a particular phone/card failure cause; device testing is still required.

1. Enable NFC on the phone, open the app and tap **Scan**.
2. Hold the card still against the phone's NFC antenna. Tap **Stop** to cancel. Scans stop when the app leaves the foreground.
3. Use **Auto (EMV first)** or **EMV only**. The EMV-only path sends no Classic or reader-specific `FF` commands over ISO-DEP.
4. View **Details**, **Live log**, or enable **Learning mode**. Choose a lesson in the history; decoded EMV fields also get lessons.
5. **Demo** works without NFC hardware. It uses only fictional data.
6. **Reveal data** controls card values, response bodies and exported reports. **Export** asks Android's file picker where to save the current report; the app does not upload it. Scan data and Classic settings stay in memory and clear when the process exits. The app requests no Internet permission.

## Supported phone paths

- Contactless EMV uses Android `IsoDep` and the original EMV APDU scanner.
- Standard NDEF tags use Android's `Ndef` API and the shared Text/URI decoder. Android performs the underlying tag exchanges; these events are explicitly labelled as platform reads rather than a raw RF capture. Type 4 tags on ISO-DEP can use the shared NDEF file reader after EMV discovery.
- MIFARE Classic uses Android's optional `MifareClassic` API with a selected known Key A or B. The default is `FFFFFFFFFFFF`, Key A. Failed sectors are reported and trailers are skipped. Phone hardware support is required; not every NFC phone supports Classic.
- Contact-only chip cards, USB PC/SC readers such as ACR122U, DESFire authentication, card writing, and full payment-terminal processing are not included.

The phone sees the card's **contactless** application, so it may return different fields from a contact chip reader.

## Verification

```powershell
dotnet run --project CoreTests/NfcLens.Core.Tests.csproj
dotnet build NfcLens.sln
```

These verify the portable protocol core and Windows regression path; they do not validate Android bindings, UI or radio behaviour. Before using real card data, build/install the APK and check Demo, masking, learning, export, foreground/background cancellation and tag removal. Then test an owned EMV card and supported NDEF/Classic tags. Do not commit real scan logs or signing keys.

References: [Android reader mode](https://developer.android.com/reference/nfc/NfcAdapter), [Android Classic support](https://learn.microsoft.com/en-us/dotnet/api/android.nfc.tech.mifareclassic), [Microsoft Android dependencies](https://learn.microsoft.com/en-us/dotnet/getting-started/installation/dependencies).
