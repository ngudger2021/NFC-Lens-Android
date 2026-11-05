using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Nfc;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using System.Security.Cryptography;

namespace NfcLens.Mobile;

[Activity(Label = "NFC Lens", MainLauncher = true, Exported = true,
    Theme = "@style/AppTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity, NfcAdapter.IReaderCallback
{
    // Carries the scan across a light/dark recreate; cleared once restored. Not persisted.
    static InspectionSession? retainedSession;
    NfcAdapter? adapter;
    PhoneScanner? reader;
    InspectionSession session = new();
    TextView status = null!, liveLog = null!;
    CheckBox reveal = null!;
    Spinner mode = null!;
    ProgressBar progress = null!;
    Button scan = null!, demo = null!, export = null!, keySettings = null!, cardTab = null!, logTab = null!;
    ScrollView cardPage = null!, logPage = null!;
    LinearLayout tileContainer = null!;
    CardSummary? shownSummary;
    bool cardShowsBack;
    List<CardTile>? shownTiles;
    bool rendered;
    byte[] key = Convert.FromHexString("FFFFFFFFFFFF");
    bool keyB, armed, foreground;
    int busy;
    Handler handler = null!;
    Java.Lang.Runnable? ticker;
    string? pendingExport;

    T Control<T>(int id) where T : View => FindViewById<T>(id) ?? throw new InvalidOperationException("Missing Android layout control.");
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Session data intentionally remains in memory; it is not restored after process death.
        SetContentView(Resource.Layout.activity_main);
        if (retainedSession != null) { session = retainedSession; retainedSession = null; }
        var content = FindViewById<View>(Android.Resource.Id.Content);
        content?.SetOnApplyWindowInsetsListener(new SystemInsets());
        status = Control<TextView>(Resource.Id.status);
        liveLog = Control<TextView>(Resource.Id.live_log);
        progress = Control<ProgressBar>(Resource.Id.progress);
        reveal = Control<CheckBox>(Resource.Id.reveal);
        mode = Control<Spinner>(Resource.Id.scan_mode);
        scan = Control<Button>(Resource.Id.scan);
        demo = Control<Button>(Resource.Id.demo);
        export = Control<Button>(Resource.Id.export);
        keySettings = Control<Button>(Resource.Id.classic_key);
        cardTab = Control<Button>(Resource.Id.card_tab);
        logTab = Control<Button>(Resource.Id.log_tab);
        cardPage = Control<ScrollView>(Resource.Id.card_page);
        logPage = Control<ScrollView>(Resource.Id.log_page);
        tileContainer = Control<LinearLayout>(Resource.Id.tile_container);
        mode.Adapter = Choices(["Auto (EMV first)", "EMV only"]);
        scan.Click += (_, _) =>
        {
            if (Volatile.Read(ref busy) != 0) { reader?.Cancel(); status.Text = "Stopping scan\u2026"; return; }
            if (adapter == null) { status.Text = "No NFC hardware. Try Demo."; return; }
            if (!adapter.IsEnabled) { status.Text = "Enable NFC in Android settings, then return to NFC Lens."; return; }
            armed = !armed;
            scan.Text = armed ? "Cancel" : "Scan card";
            status.Text = armed ? "Hold a card steady against the phone's NFC antenna." : "Ready.";
            if (armed)
            {
                session = new InspectionSession();
                session.Log("Waiting for a card. Mode: " + (mode.SelectedItemPosition == 1 ? "EMV only; NDEF probing disabled." : "Auto; Android NDEF discovery enabled."));
                ShowPage(0);
            }
            ConfigureReaderMode();
        };
        mode.ItemSelected += (_, _) => { if (foreground && armed && Volatile.Read(ref busy) == 0) ConfigureReaderMode(); };
        demo.Click += (_, _) => { session = InspectionSession.Demo(); RefreshPages(); ShowPage(0); status.Text = "Fictional demo; no card scanned."; };
        export.Click += (_, _) => ExportReport();
        keySettings.Click += (_, _) => EditKey();
        cardTab.Click += (_, _) => ShowPage(0);
        logTab.Click += (_, _) => ShowPage(1);
        reveal.CheckedChange += (_, _) => RefreshPages();
        adapter = NfcAdapter.GetDefaultAdapter(this);
        handler = new Handler(Looper.MainLooper!);
        ticker = new Java.Lang.Runnable(() => { if (!foreground) return; RefreshPages(); handler.PostDelayed(ticker!, 150); });
        ShowPage(0);
        RefreshPages();
    }
    ArrayAdapter<string> Choices(string[] values)
    {
        var result = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, values);
        result.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        return result;
    }
    protected override void OnResume()
    {
        base.OnResume(); foreground = true;
        scan.Text = "Scan card";
        status.Text = adapter == null ? "No NFC hardware; try Demo." : adapter.IsEnabled ? "Tap Scan, then hold a card against the phone." : "NFC is disabled in Android settings.";
        handler.Post(ticker!);
    }
    void ConfigureReaderMode()
    {
        if (adapter == null) return;
        adapter.DisableReaderMode(this);
        if (!foreground || !armed || !adapter.IsEnabled) return;
        var flags = NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.NoPlatformSounds;
        if (mode.SelectedItemPosition == 1) flags |= NfcReaderFlags.SkipNdefCheck;
        else flags |= NfcReaderFlags.NfcF | NfcReaderFlags.NfcV;
        var extras = new Bundle();
        extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 250);
        adapter.EnableReaderMode(this, this, flags, extras);
        session.Log("Reader mode enabled. Present the card now, or keep it against the NFC antenna.");
    }
    protected override void OnPause()
    {
        foreground = false; armed = false;
        reader?.Cancel();
        adapter?.DisableReaderMode(this);
        handler.RemoveCallbacks(ticker!);
        base.OnPause();
    }
    protected override void OnDestroy()
    {
        reader?.Cancel();
        if (IsChangingConfigurations) retainedSession = session;
        CryptographicOperations.ZeroMemory(key);
        handler.RemoveCallbacksAndMessages(null);
        base.OnDestroy();
    }
    public void OnTagDiscovered(Tag? tag)
    {
        if (tag != null) RunOnUiThread(() => BeginScan(tag));
    }
    void BeginScan(Tag tag)
    {
        if (!foreground || !armed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        armed = false;
        session.Log("Tag detected. Technologies: " + string.Join(", ", tag.GetTechList() ?? []));
        session.Log("Phone NFC scan started.");
        var current = session;
        var currentReader = new PhoneScanner(); reader = currentReader;
        var scanKey = (byte[])key.Clone(); bool scanKeyB = keyB, emvOnly = mode.SelectedItemPosition == 1;
        scan.Text = "Stop";
        demo.Enabled = export.Enabled = keySettings.Enabled = mode.Enabled = false;
        status.Text = "Reading\u2026 keep the card steady.";
        progress.Visibility = ViewStates.Visible;
        ShowPage(1);
        _ = Task.Run(() =>
        {
            string outcome;
            bool succeeded = false;
            try
            {
                currentReader.Read(tag, emvOnly, scanKey, scanKeyB, current);
                succeeded = true;
                outcome = "Scan finished. Tap a tile for what it means.";
            }
            catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.IllegalStateException or Android.Nfc.FormatException or IOException or System.FormatException or System.OperationCanceledException or ArgumentException)
            {
                // Do not echo exception messages from native authentication APIs, which may include inputs.
                outcome = ex switch
                {
                    System.OperationCanceledException => "Scan cancelled.",
                    TagLostException => "Card connection lost. Hold the card steady at the phone's NFC antenna and scan again.",
                    Java.IO.IOException => "NFC communication failed. Check the Log tab and retry using EMV only mode.",
                    System.FormatException or Android.Nfc.FormatException => "The card returned an unsupported or malformed data format. See the Log tab.",
                    _ => "Read stopped: NFC operation unavailable. See the Log tab for the failure type."
                };
                current.Log("Failure type: " + ex.GetType().Name);
                current.Add(new("Read status", outcome)); current.Log(outcome);
            }
            finally { CryptographicOperations.ZeroMemory(scanKey); }
            current.Log("Scan attempt finished.");
            RunOnUiThread(() =>
            {
                Interlocked.Exchange(ref busy, 0);
                if (IsFinishing || IsDestroyed) return;
                if (foreground) adapter?.DisableReaderMode(this);
                scan.Text = "Scan card";
                demo.Enabled = export.Enabled = keySettings.Enabled = mode.Enabled = true;
                progress.Visibility = ViewStates.Gone;
                status.Text = outcome;
                if (succeeded) Haptic();
                RefreshPages();
                ShowPage(0);
            });
        });
    }
    void RefreshPages()
    {
        string logText = session.Transcript(reveal.Checked);
        if (liveLog.Text != logText)
        {
            liveLog.Text = logText;
            logPage.Post(() => logPage.FullScroll(FocusSearchDirection.Down));
        }
        var summary = session.Summary(reveal.Checked);
        var tiles = session.Tiles(reveal.Checked);
        if (!rendered || summary != shownSummary || !tiles.SequenceEqual(shownTiles!))
        {
            rendered = true;
            shownSummary = summary;
            shownTiles = tiles;
            Render(summary, tiles);
        }
    }
    void Render(CardSummary? summary, List<CardTile> tiles)
    {
        tileContainer.RemoveAllViews();
        if (summary != null)
        {
            tileContainer.AddView(BuildCard(summary));
            var hint = MakeText("Tap the card to flip it", 12, Resource.Color.text_secondary);
            hint.Gravity = GravityFlags.Center;
            hint.SetPadding(0, 0, 0, Dp(12));
            tileContainer.AddView(hint);
        }
        if (tiles.Count == 0)
        {
            var empty = new TextView(this)
            {
                Text = "No card data yet.\n\nTap Scan, then hold a card or tag against the phone's NFC antenna. Demo shows fictional data.",
                TextSize = 15
            };
            empty.SetTextColor(Tint(Resource.Color.text_secondary));
            empty.SetPadding(Dp(8), Dp(24), Dp(8), Dp(8));
            tileContainer.AddView(empty);
            return;
        }
        string? group = null;
        foreach (var tile in tiles)
        {
            if (tile.Group != group) { group = tile.Group; tileContainer.AddView(SectionHeader(group)); }
            tileContainer.AddView(TileView(tile));
        }
    }
    // Card-shaped summary with a front (network, number, name) and a back (dates and application details).
    // Tap to flip. Masked values stay masked on both sides unless Reveal is on.
    View BuildCard(CardSummary summary)
    {
        var frame = new FrameLayout(this);
        float density = Resources!.DisplayMetrics!.Density;
        int cardWidthDp = (int)(Resources.DisplayMetrics!.WidthPixels / density) - 32;
        frame.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp((int)(cardWidthDp / 1.586)))
        {
            TopMargin = Dp(4),
            BottomMargin = Dp(6)
        };
        frame.SetCameraDistance(12000 * density);
        frame.AddView(BuildFront(summary));
        frame.AddView(BuildBack(summary));
        ApplyCardSide(frame);
        frame.Clickable = true;
        frame.Focusable = true;
        frame.Click += (_, _) => FlipCard(frame);
        return frame;
    }
    void ApplyCardSide(FrameLayout frame)
    {
        frame.GetChildAt(0)!.Visibility = cardShowsBack ? ViewStates.Gone : ViewStates.Visible;
        frame.GetChildAt(1)!.Visibility = cardShowsBack ? ViewStates.Visible : ViewStates.Gone;
    }
    void FlipCard(FrameLayout frame)
    {
        frame.Animate()!.RotationY(90).SetDuration(160).WithEndAction(new Java.Lang.Runnable(() =>
        {
            cardShowsBack = !cardShowsBack;
            ApplyCardSide(frame);
            frame.RotationY = -90;
            frame.Animate()!.RotationY(0).SetDuration(160).Start();
        })).Start();
    }
    View BuildFront(CardSummary summary)
    {
        var front = new LinearLayout(this) { Orientation = Orientation.Vertical };
        front.SetPadding(Dp(20), Dp(16), Dp(20), Dp(16));
        front.Background = Gradient(C(Resource.Color.face_start), C(Resource.Color.face_end), Dp(16));
        front.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        var spacer = new View(this) { LayoutParameters = new LinearLayout.LayoutParams(0, 0, 1f) };
        top.AddView(spacer);
        top.AddView(BrandMark(summary.Brand));
        front.AddView(top);

        // Chip sits on the left below the network mark, as on a physical card.
        var chip = new View(this) { Background = Gradient(Color.ParseColor("#F3D98B"), Color.ParseColor("#B98B2E"), Dp(6)) };
        chip.LayoutParameters = new LinearLayout.LayoutParams(Dp(42), Dp(32)) { TopMargin = Dp(12) };
        front.AddView(chip);

        var number = MakeText(summary.PanDisplay, 20, Resource.Color.face_text);
        number.Typeface = Typeface.Monospace;
        number.SetPadding(0, Dp(26), 0, 0);
        front.AddView(number);

        var nameBlock = new LinearLayout(this) { Orientation = Orientation.Vertical };
        nameBlock.SetPadding(0, Dp(12), 0, 0);
        nameBlock.AddView(MakeText("Cardholder", 9, Resource.Color.face_text_muted));
        var name = MakeText(summary.NameDisplay, 16, Resource.Color.face_text, bold: true);
        nameBlock.AddView(name);
        front.AddView(nameBlock);
        return front;
    }
    View BuildBack(CardSummary summary)
    {
        var back = new LinearLayout(this) { Orientation = Orientation.Vertical };
        back.Background = Gradient(C(Resource.Color.face_start), C(Resource.Color.face_end), Dp(16));
        back.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        var stripe = new View(this) { Background = Gradient(Color.ParseColor("#0B0F1A"), Color.ParseColor("#0B0F1A"), 0) };
        stripe.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(28)) { TopMargin = Dp(18) };
        back.AddView(stripe);

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
        body.AddView(BackPair(("Expires", summary.ExpiryDisplay), ("Valid from", summary.EffectiveDisplay)));
        body.AddView(BackPair(("Card sequence", summary.SequenceDisplay), ("Issued in", summary.IssuerCountry ?? "Not read")));
        body.AddView(BackPair(("Application", summary.ApplicationLabel ?? "Not read"), null));
        body.AddView(BackPair(("Application ID", summary.Aid ?? "Not read"), null));
        back.AddView(body);
        return back;
    }
    // Two fields side by side; a null right-hand field leaves the row half empty.
    View BackPair((string Label, string Value) left, (string Label, string Value)? right)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.AddView(BackCell(left.Label, left.Value));
        row.AddView(BackCell(right?.Label ?? "", right?.Value ?? ""));
        return row;
    }
    View BackCell(string label, string value)
    {
        var cell = new LinearLayout(this) { Orientation = Orientation.Vertical };
        cell.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { BottomMargin = Dp(8) };
        if (label.Length == 0) return cell;
        cell.AddView(MakeText(label.ToUpperInvariant(), 8, Resource.Color.face_text_muted));
        cell.AddView(MakeText(value, 13, Resource.Color.face_text, bold: true));
        return cell;
    }
    // Network mark: uses brand_visa / brand_mastercard drawables when supplied, otherwise a plain text label.
    View BrandMark(string? brand)
    {
        string logoName = brand switch { "Visa" => "brand_visa", "Mastercard" => "brand_mastercard", _ => "" };
        int logo = logoName.Length == 0 ? 0 : Resources!.GetIdentifier(logoName, "drawable", PackageName);
        if (logo != 0)
        {
            var image = new ImageView(this);
            image.SetImageResource(logo);
            image.SetScaleType(ImageView.ScaleType.FitEnd);
            image.LayoutParameters = new LinearLayout.LayoutParams(Dp(64), Dp(36));
            return image;
        }
        return MakeText((brand ?? "Payment card").ToUpperInvariant(), 17, Resource.Color.face_text, bold: true);
    }
    TextView MakeText(string text, float size, int colorResource, bool bold = false)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(Tint(colorResource));
        if (bold) view.Typeface = Typeface.DefaultBold;
        return view;
    }
    View SectionHeader(string group)
    {
        var header = new TextView(this) { Text = GroupLabel(group).ToUpperInvariant(), TextSize = 12, LetterSpacing = 0.08f };
        header.SetTextColor(Tint(Resource.Color.text_secondary));
        header.Typeface = Typeface.DefaultBold;
        header.SetPadding(Dp(4), Dp(14), 0, Dp(8));
        return header;
    }
    static string GroupLabel(string group) => group switch
    {
        "Card" => "Card data",
        "Application" => "Application",
        "Processing" => "Processing",
        _ => "Device"
    };
    View TileView(CardTile tile)
    {
        bool hidden = tile.Value == "[hidden]";
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14));
        card.Background = Gradient(C(Resource.Color.surface), C(Resource.Color.surface), Dp(14));
        card.LayoutParameters = FullWidth(0, Dp(10));
        card.Clickable = true;
        card.Focusable = true;

        var title = new TextView(this) { Text = tile.Title + (tile.Tag == null ? "" : "  \u00B7  " + tile.Tag), TextSize = 12 };
        title.SetTextColor(Tint(Resource.Color.text_secondary));
        var value = new TextView(this) { Text = tile.Value, TextSize = 17 };
        value.SetTextColor(Tint(hidden ? Resource.Color.text_hidden : Resource.Color.text_primary));
        value.Typeface = Typeface.DefaultBold;
        value.SetTextIsSelectable(true);
        value.SetPadding(0, Dp(4), 0, 0);

        card.AddView(title);
        card.AddView(value);
        card.Click += (_, _) => ShowExplanation(tile);
        return card;
    }
    void ShowExplanation(CardTile tile)
    {
        string heading = tile.Tag == null ? "Non-EMV field" : "Tag " + tile.Tag;
        string note = tile.Sensitive ? "\n\nThis value is masked unless Reveal is on." : "";
        new AlertDialog.Builder(this)
            .SetTitle(tile.Title)
            .SetMessage(heading + "\n\n" + tile.Explanation + note)
            .SetPositiveButton("Got it", (_, _) => { })
            .Show();
    }
    void ShowPage(int page)
    {
        cardPage.Visibility = page == 0 ? ViewStates.Visible : ViewStates.Gone;
        logPage.Visibility = page == 1 ? ViewStates.Visible : ViewStates.Gone;
        StyleTab(cardTab, page == 0);
        StyleTab(logTab, page == 1);
    }
    void StyleTab(Button tab, bool selected)
    {
        tab.SetTextColor(Tint(selected ? Resource.Color.tab_selected : Resource.Color.tab_idle));
        tab.Typeface = selected ? Typeface.DefaultBold : Typeface.Default;
    }
    // Feedback only: a refused or unavailable vibrator must never break the scan result.
    void Haptic()
    {
        try
        {
            if (GetSystemService(VibratorService) is not Vibrator vibrator || !vibrator.HasVibrator) return;
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) vibrator.Vibrate(VibrationEffect.CreateOneShot(60, VibrationEffect.DefaultAmplitude));
            else vibrator.Vibrate(60);
        }
        catch (Java.Lang.SecurityException) { }
    }
    int C(int colorResource) => Resources!.GetColor(colorResource, Theme);
    // Text colours come straight from the colour resource so day/night values resolve correctly.
    Android.Content.Res.ColorStateList Tint(int colorResource) => Resources!.GetColorStateList(colorResource, Theme)!;
    int Dp(int value) => (int)Math.Round(value * Resources!.DisplayMetrics!.Density);
    LinearLayout.LayoutParams FullWidth(int topMargin, int bottomMargin) =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = topMargin, BottomMargin = bottomMargin };
    GradientDrawable Gradient(int startColor, int endColor, int radius)
    {
        var drawable = new GradientDrawable(GradientDrawable.Orientation.TlBr, [startColor, endColor]);
        drawable.SetCornerRadius(radius);
        return drawable;
    }
    void EditKey()
    {
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 16, 32, 16);
        var note = new TextView(this) { Text = "One known 6-byte key per scan. Factory default: FFFFFFFFFFFF. Does not affect EMV." };
        var input = new EditText(this) { Text = Convert.ToHexString(key), InputType = InputTypes.ClassText | InputTypes.TextVariationPassword };
        input.SetSingleLine(true);
        var type = new Spinner(this) { Adapter = Choices(["Key A", "Key B"]) }; type.SetSelection(keyB ? 1 : 0);
        layout.AddView(note); layout.AddView(input); layout.AddView(type);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Classic key");
        builder.SetView(layout);
        builder.SetNegativeButton("Cancel", (_, _) => { });
        builder.SetPositiveButton("Apply", (_, _) => { });
        var dialog = builder.Create() ?? throw new InvalidOperationException("Could not create key dialog.");
        dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            string value = input.Text ?? "";
            if (value.Length != 12 || !value.All(Uri.IsHexDigit)) { input.Error = "Enter exactly 12 hexadecimal characters."; return; }
            CryptographicOperations.ZeroMemory(key); key = Convert.FromHexString(value); keyB = type.SelectedItemPosition == 1;
            input.Text = ""; dialog.Dismiss();
        };
    }
    void ExportReport()
    {
        pendingExport = session.Report(reveal.Checked);
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable); intent.SetType("text/plain"); intent.PutExtra(Intent.ExtraTitle, "NFC-Lens-report.txt");
        StartActivityForResult(intent, 100);
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != 100) return;
        string? report = pendingExport; pendingExport = null;
        if (resultCode != Result.Ok || data?.Data == null || report == null) return;
        try
        {
            using var stream = ContentResolver!.OpenOutputStream(data.Data, "wt") ?? throw new IOException();
            using var writer = new StreamWriter(stream); writer.Write(report);
            status.Text = "Report saved to your selected location.";
        }
        catch (Exception ex) when (ex is IOException or Java.IO.IOException or Java.Lang.SecurityException) { status.Text = "Could not save report."; }
    }
    sealed class SystemInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View? view, WindowInsets? insets)
        {
            if (insets == null) throw new ArgumentNullException(nameof(insets));
            view?.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
            return insets;
        }
    }
}



