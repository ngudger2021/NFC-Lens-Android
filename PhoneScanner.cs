using Android.Nfc;
using Android.Nfc.Tech;
using System.Security.Cryptography;
using AndroidNdef = Android.Nfc.Tech.Ndef;

namespace NfcLens.Mobile;

// Adapts the phone NFC APIs; never sends reader-specific FF commands over ISO-DEP.
sealed class PhoneScanner
{
    readonly object gate = new();
    ITagTechnology? active;
    bool cancelled;
    void Open(ITagTechnology tech)
    {
        lock (gate)
        {
            active = tech;
            if (cancelled) throw new OperationCanceledException();
        }
        tech.Connect();
        lock (gate) { if (cancelled) throw new OperationCanceledException(); }
    }
    public void Cancel()
    {
        ITagTechnology? tech;
        lock (gate) { cancelled = true; tech = active; }
        try { tech?.Close(); } catch (Java.IO.IOException) { }
    }
    public void Read(Tag tag, bool emvOnly, byte[] key, bool keyB, InspectionSession session)
    {
        session.Add(new("Identifier", Convert.ToHexString(tag.GetId() ?? []), true));
        session.Add(new("Android technologies", string.Join(", ", tag.GetTechList() ?? [])));
        try
        {
            var iso = IsoDep.Get(tag);
            if (iso != null)
            {
                session.Log("ISO-DEP detected. Connecting to the contactless application interface…");
                Open(iso); iso.Timeout = 5000;
                session.Log("ISO-DEP connected. Starting EMV application discovery.");
                byte[] Exchange(byte[] command)
                {
                    if (command[0] == 0xFF) return [0x6A, 0x81];
                    try { return iso.Transceive(command) ?? throw new IOException("No ISO-DEP response."); }
                    catch (Java.IO.IOException ex)
                    {
                        session.Log("ISO-DEP exchange failed (" + ex.GetType().Name + "). No response status was received.");
                        throw;
                    }
                }
                var scan = new CardScan(Exchange, session.Log);
                try { scan.Read([], emvOnly, key, keyB); }
                finally
                {
                    session.AddEmv(scan.Emv);
                    foreach (var field in scan.Nfc) session.Add(field);
                }
                return;
            }
            if (emvOnly)
            {
                session.Log("No ISO-DEP technology was exposed by Android; no EMV commands were sent.");
                session.Add(new("Read status", "Android did not expose ISO-DEP. Ensure this is a contactless payment card, remove it, and retry holding it steady after pressing Scan."));
                return;
            }
            var classic = MifareClassic.Get(tag);
            if (classic != null)
            {
                Open(classic);
                byte[] loadedKey = new byte[6];
                byte[] Exchange(byte[] command)
                {
                    switch (command[1])
                    {
                        case 0xCA: return [.. (tag.GetId() ?? []), 0x90, 0];
                        case 0x82: command.AsSpan(5, 6).CopyTo(loadedKey); return [0x90, 0];
                        case 0x86:
                            int sector = classic.BlockToSector(command[7]);
                            bool ok = command[8] == 0x61 ? classic.AuthenticateSectorWithKeyB(sector, loadedKey) : classic.AuthenticateSectorWithKeyA(sector, loadedKey);
                            return ok ? [0x90, 0] : [0x63, 0];
                        case 0xB0: return [.. (classic.ReadBlock(command[3]) ?? []), 0x90, 0];
                        default: throw new NotSupportedException("Unsupported Classic operation.");
                    }
                }
                session.Log("Classic calls below describe reader operations mapped to Android APIs, not literal over-the-air APDUs.");
                var scan = new ClassicScanner(Exchange, session.Log);
                try { scan.Read(classic.SectorCount, key, keyB); }
                finally { CryptographicOperations.ZeroMemory(loadedKey); foreach (var field in scan.Fields) session.Add(field); }
                return;
            }
            var ndef = AndroidNdef.Get(tag);
            if (ndef != null)
            {
                Open(ndef);
                session.Log("Android NDEF read started (platform API; raw RF commands are not exposed).");
                var bytes = ndef.NdefMessage?.ToByteArray() ?? [];
                session.Log("< " + Convert.ToHexString(bytes) + " | Android NDEF read complete");
                session.Add(new("Tag format", ndef.Type ?? "NDEF"));
                session.Add(new("Raw NDEF message", Convert.ToHexString(bytes), true));
                if (bytes.Length == 0) session.Add(new("NDEF", "No message stored."));
                else foreach (var field in NfcLens.Ndef.Decode(bytes)) session.Add(field);
                return;
            }
            session.Add(new("Read status", "No supported readable technology. MIFARE Classic is optional on Android phones; protected or unformatted tags may expose only an identifier."));
        }
        finally
        {
            ITagTechnology? tech;
            lock (gate) { tech = active; active = null; }
            try { tech?.Close(); } catch (Java.IO.IOException) { }
            finally { tech?.Dispose(); }
        }
    }
}
