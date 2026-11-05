# NFC Lens for Android

Read the contactless (NFC) payment cards and tags your phone can see, and explain what each field means. NFC Lens shows a card-style summary of a payment card, lets you flip it to see the dates and application details, and lists every EMV field with a plain-English explanation.

It's an inspection tool, not a payment app. It never authorises a payment, never writes to a card, and never sends data anywhere. The app requests no Internet permission.

This guide covers building the APK from source, signing a release build, and sharing it. The APK is too large to attach to a GitHub release, so share it through a cloud link instead (see [Publishing a release](#publishing-a-release)).

## Requirements

- Windows with PowerShell.
- The **.NET 10 SDK**.
- Android 8.0 (API 26) or later on the phone, with NFC.
- An Internet connection for the first build, which downloads the Android SDK and a JDK.

## 1. Get the source

Clone with the shared core, which is a git submodule:

```powershell
git clone --recurse-submodules https://github.com/ngudger2021/NFC-Lens-Android.git
cd NFC-Lens-Android
```

If you cloned without `--recurse-submodules`, run this from the repository folder:

```powershell
git submodule update --init --recursive
```

## 2. Install the Android SDK and JDK

The first time, let the build script download the Android SDK and a JDK into `.toolchain\`. It accepts the Android SDK licences automatically:

```powershell
.\build.ps1 -InstallDependencies
```

This needs several gigabytes of free disk space. Later builds don't need the flag.

If you already have the Android SDK and a JDK 21, point the script at them instead:

```powershell
.\build.ps1 -AndroidSdkDirectory 'C:\path\to\android-sdk' -JavaSdkDirectory 'C:\path\to\jdk-21'
```

## 3. Build a debug APK (for your own phone)

```powershell
.\build.ps1
```

The script prints the APK path when it finishes:

```
bin\Debug\net10.0-android\app.nfclens.mobile-Signed.apk
```

A debug APK is signed with Android's debug key. It's for testing on your own phones only, and it can't be used to update a release build.

## 4. Build a signed release APK

A release APK must be signed with your own private key. The build script doesn't do release builds, so use `dotnet build` directly.

### 4a. Create a signing key (once)

Create the key outside the repository, for example in a `Keys` folder in your Documents. The `keytool` that comes with the JDK can do it. If you used `.toolchain`, it's at `.toolchain\jdk\bin\keytool.exe`:

```powershell
keytool -genkeypair -v -keystore C:\Users\nick\Documents\Keys\nfclens-release.keystore -alias nfclens -keyalg RSA -keysize 2048 -validity 10000
```

`keytool` asks for a keystore password, a key password and some name details. Write the passwords down and keep them somewhere safe.

> **Keep this key safe.** Anyone with the keystore and its passwords can sign apps as you. If you lose it, you can't publish updates that existing installs will accept, and users will have to uninstall first. Back it up somewhere private. Never copy it into the repository. The `.gitignore` excludes `*.keystore` and `*.jks`, but check `git status` before each commit.

### 4b. Build and sign

Store the passwords in environment variables for the current PowerShell session, so they don't end up in your command history as literal text:

```powershell
$env:NFCLENS_KEYSTORE_PASSWORD = 'your-keystore-password'
$env:NFCLENS_KEY_PASSWORD = 'your-key-password'

dotnet build NfcLens.Android.csproj -c Release `
  -p:AndroidSdkDirectory="C:\path\to\android-sdk" `
  -p:JavaSdkDirectory="C:\path\to\jdk-21" `
  -p:AndroidKeyStore=true `
  -p:AndroidSigningKeyStore="C:\Users\nick\Documents\Keys\nfclens-release.keystore" `
  -p:AndroidSigningKeyAlias=nfclens `
  -p:AndroidSigningKeyPass=$env:NFCLENS_KEY_PASSWORD `
  -p:AndroidSigningStorePass=$env:NFCLENS_KEYSTORE_PASSWORD

Remove-Item Env:NFCLENS_KEYSTORE_PASSWORD, Env:NFCLENS_KEY_PASSWORD
```

If `.\build.ps1` already found your SDK and JDK, use the same paths here. The SDK is usually at `.toolchain\android-sdk`, and the JDK at `.toolchain\jdk`.

The signed release APK is at:

```
bin\Release\net10.0-android\app.nfclens.mobile-Signed.apk
```

It's currently about 49 MB.

### 4c. Check the signature

Use `apksigner` from the Android build tools. Replace `36.0.0` with the version folder you have:

```powershell
& 'C:\path\to\android-sdk\build-tools\36.0.0\apksigner.bat' verify --verbose 'bin\Release\net10.0-android\app.nfclens.mobile-Signed.apk'
```

The output should end with `Verifies` and show `Verified using v2 scheme (APK Signature Scheme v2): true`. The warnings about restricted methods from Java are harmless.

## 5. Bump the version

Before each release, edit `NfcLens.Android.csproj`:

- `ApplicationDisplayVersion` is the version shown to people, such as `1.0.2`.
- `ApplicationVersion` is a whole number that must go up by at least one each release, such as `3`. Android uses it to decide whether an update is newer.

Commit the change before you build the release.

## Publishing a release

### 1. Tag the commit

```powershell
git tag -a v1.0.2 -m "NFC Lens Android 1.0.2"
git push origin v1.0.2
```

Use the same version as `ApplicationDisplayVersion`.

### 2. Get a checksum

People can use this to confirm the file they downloaded is the one you built:

```powershell
Get-FileHash 'bin\Release\net10.0-android\app.nfclens.mobile-Signed.apk' -Algorithm SHA256
```

### 3. Upload the APK to cloud storage

GitHub doesn't accept release files over 25 MB, so host the APK elsewhere:

1. Upload the APK to Google Drive, OneDrive or Dropbox.
2. Set the link to **anyone with the link can view**.
3. Copy the link.

### 4. Create the GitHub release

1. Open `https://github.com/ngudger2021/NFC-Lens-Android/releases/new`.
2. Choose the tag you pushed (`v1.0.2`).
3. Enter a title such as `NFC Lens Android 1.0.2`.
4. In the description, add:
   - the download link from step 3,
   - the SHA-256 checksum from step 2,
   - what changed in this version.
5. Click **Publish release**.

Don't attach the APK to the release. The page will show the cloud link instead.

## Installing on a phone

### With a cable (recommended for testing)

1. On the phone, turn on **Developer options** and **USB debugging**. (Settings → About phone → tap *Build number* seven times.)
2. Connect the phone by USB and accept the debugging prompt on the phone.
3. Install the APK:

```powershell
adb install -r 'bin\Release\net10.0-android\app.nfclens.mobile-Signed.apk'
```

`adb` is in `.toolchain\android-sdk\platform-tools\`.

### From a download link

1. Open the link on the phone and download the APK.
2. Open the file. Android asks you to allow installs from that app. Allow it, then tap **Install**.

If an older version signed with a *different* key is installed, Android refuses the update. Uninstall NFC Lens first, then install again. Updates signed with the same key install over the top and keep the app's data.

## Using the app

1. Tap **Demo** to see fictional card data, which needs no NFC hardware.
2. Enable NFC in Android settings.
3. Tap **Scan card**, then hold a card still against the back of the phone until the read finishes. A short vibration means the read succeeded.
4. Tap the card picture to flip it and see the back.
5. Tap any field to see what its tag means.

- **Scan mode:** **Auto (EMV first)** is the default. If a payment card fails, switch to **EMV only**, remove the card, press Scan and present it again.
- **Reveal:** shows card numbers, names, expiry dates and other sensitive values. They stay hidden until you turn it on.
- **Export:** saves a text report through Android's file picker. Nothing is uploaded.
- **Log:** the live command log. Response bodies are hidden unless Reveal is on.
- **Key:** enter a six-byte key for MIFARE Classic tags. The default is the factory key `FFFFFFFFFFFF`.

Scan results stay in memory and are cleared when the app closes.

## Troubleshooting

- **The build can't find the Android SDK:** run `.\build.ps1 -InstallDependencies`, or pass `-AndroidSdkDirectory` and `-JavaSdkDirectory` as in step 2.
- **Signing errors on a release build:** check that the keystore path is right, that the alias is `nfclens`, and that both passwords match what you set in `keytool`.
- **`INSTALL_FAILED_UPDATE_INCOMPATIBLE`:** the installed version was signed with a different key. Uninstall NFC Lens, then install again.
- **`adb` is not recognised:** use `.toolchain\android-sdk\platform-tools\adb.exe`.
- **The card doesn't read:** try **EMV only** mode, keep the card still, and check that NFC is on. The **Log** tab shows where a read stopped.

## Updating the shared core

The protocol code is in its own repository, linked here as the `Core` folder. To update to its latest version:

```powershell
cd Core
git pull origin main
cd ..
git add Core
git commit -m "Update Core"
```

## Safety notes

- Use only cards you are authorised to inspect.
- Don't commit scan logs, exported reports or signing keys. Check `git status` before each commit.
- Use the fictional Demo data for screenshots and sharing.

## References

- [Android NFC reader mode](https://developer.android.com/reference/android/nfc/NfcAdapter)
- [Android MIFARE Classic support](https://developer.android.com/reference/android/nfc/tech/MifareClassic)
- [EMV specifications](https://www.emvco.com/emv-technologies/)
