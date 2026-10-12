using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickerPrintApp;

/// <summary>How a not-yet-printed job is recognised.</summary>
public enum PendingMode
{
    /// <summary>status == "pending"</summary>
    StatusPending,

    /// <summary>printed == false</summary>
    PrintedFalse,
}

public enum PrinterConnection
{
    /// <summary>A printer installed in Windows (USB, network, or Bluetooth with a driver).</summary>
    Windows,

    /// <summary>Raw TSPL / ESC/POS to a COM port — Bluetooth SPP printers without a Windows driver.</summary>
    SerialPort,
}

public enum ConnectionMode
{
    /// <summary>Firestore realtime listener (gRPC streaming) — instant.</summary>
    Realtime,

    /// <summary>Query every few seconds over plain HTTPS — for networks/proxies that block gRPC.</summary>
    Polling,
}

/// <summary>All user-configurable options, persisted as JSON in %AppData%\StickerPrintApp\settings.json.</summary>
public sealed class AppSettings
{
    // --- Firebase / Firestore ---
    public string CredentialPath { get; set; } = "";
    public string ProjectId { get; set; } = "campaign-th---game";
    public string DatabaseId { get; set; } = "(default)";
    public string Collection { get; set; } = "print_jobs";

    /// <summary>Job field naming the printer slot (1–5) a job is printed on.</summary>
    public string PrinterIdField { get; set; } = "printerId";

    public const int RoundRobin = -1;

    /// <summary>
    /// Jobs without a printerId: <see cref="RoundRobin"/> = rotate over the enabled printers,
    /// 1–5 = always that printer, 0 = don't print them on this PC.
    /// </summary>
    public int UnassignedPrinterId { get; set; } = RoundRobin;

    public PendingMode PendingMode { get; set; } = PendingMode.StatusPending;
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Realtime;
    public int PollIntervalSeconds { get; set; } = 3;
    public bool AutoConnectOnStart { get; set; } = true;

    /// <summary>Write printed = true / status back to the job document (also prevents double printing).</summary>
    public bool WriteBackStatus { get; set; } = true;

    public bool AutoPrint { get; set; } = true;

    // --- Field mapping (document field names; dotted paths like "guest.position" are supported) ---
    public string FieldTicketCode { get; set; } = "ticket_code";
    public string FieldTitle { get; set; } = "title";
    public string FieldFirstName { get; set; } = "first_name";
    public string FieldLastName { get; set; } = "last_name";
    public string FieldFullName { get; set; } = "fullname";
    public string FieldPosition { get; set; } = "position";
    public string FieldOrganization { get; set; } = "organization";
    public string FieldCategory { get; set; } = "category";
    public string FieldQr { get; set; } = "ticket_code";

    public string FontFamily { get; set; } = FontManager.DefaultFamily;

    public List<PrinterProfile> Printers { get; set; } = [];

    /// <summary>Unknown properties, e.g. single-printer settings from older versions (migrated on load).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Legacy { get; set; }

    [JsonIgnore]
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickerPrintApp");

    [JsonIgnore]
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PrinterProfile? FindPrinter(int id) => Printers.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The enabled printer slot for a job's printerId value ("" = not specified), <see cref="RoundRobin"/>
    /// when an unassigned job should be rotated over the enabled printers, or 0 if this PC should not
    /// print it. Accepts "2", 2 and 2.0.
    /// </summary>
    public int ResolvePrinter(string printerId)
    {
        int id;
        if (string.IsNullOrWhiteSpace(printerId))
        {
            if (UnassignedPrinterId == RoundRobin)
                return Printers.Any(p => p.Enabled) ? RoundRobin : 0;
            id = UnassignedPrinterId;
        }
        else if (!double.TryParse(printerId, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var n) || n != Math.Floor(n))
            return 0;
        else
            id = (int)n;

        return FindPrinter(id) is { Enabled: true } ? id : 0;
    }

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            // Corrupt settings file: fall back to defaults rather than refusing to start.
        }

        if (s is null)
        {
            s = new AppSettings();
            s.CredentialPath = FindCredentialNextToExe() ?? "";
        }
        s.Normalize();
        return s;
    }

    /// <summary>Guarantees exactly printers 1..5 and migrates pre-multi-printer settings into printer 1.</summary>
    private void Normalize()
    {
        var byId = Printers.Where(p => p.Id is >= 1 and <= PrinterProfile.MaxProfiles)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var fresh = byId.Count == 0;
        Printers = Enumerable.Range(1, PrinterProfile.MaxProfiles)
            .Select(id => byId.TryGetValue(id, out var p) ? p : PrinterProfile.CreateDefault(id))
            .ToList();
        foreach (var p in Printers)
            p.Layout ??= StickerLayout.CreateDefault(p.StickerWidthMm, p.StickerHeightMm);

        if (fresh && Legacy is { Count: > 0 })
            MigrateLegacy(Printers[0]);
        Legacy = null;
    }

    private void MigrateLegacy(PrinterProfile p)
    {
        string? Str(string key) => Legacy!.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string key) => Legacy!.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        bool? Bool(string key) => Legacy!.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        if (Num("PrinterConnection") is { } conn) p.Connection = (PrinterConnection)(int)conn;
        p.PrinterName = Str("PrinterName") ?? p.PrinterName;
        p.SerialPortName = Str("SerialPortName") ?? p.SerialPortName;
        if (Num("SerialBaudRate") is { } baud) p.SerialBaudRate = (int)baud;
        if (Num("RawLanguage") is { } lang) p.RawLanguage = (RawLanguage)(int)lang;
        if (Num("PrinterDpi") is { } dpi) p.PrinterDpi = (int)dpi;
        if (Num("LabelGapMm") is { } gap) p.LabelGapMm = gap;
        if (Bool("Rotate180") is { } rot) p.Rotate180 = rot;
        p.StickerSizeName = Str("StickerSizeName") ?? p.StickerSizeName;
        if (Num("StickerWidthMm") is { } w) p.StickerWidthMm = w;
        if (Num("StickerHeightMm") is { } h) p.StickerHeightMm = h;
        if (Bool("Landscape") is { } land) p.Landscape = land;
        if (Num("OffsetXMm") is { } ox) p.OffsetXMm = ox;
        if (Num("OffsetYMm") is { } oy) p.OffsetYMm = oy;
        if (Num("Copies") is { } copies) p.Copies = (int)copies;

        var content = StickerRenderer.StickerSizeMm(p);
        p.Layout = StickerLayout.CreateDefault(content.Width, content.Height);
        p.Enabled = true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>Convenience for first run: picks up a service-account key dropped next to the .exe.</summary>
    private static string? FindCredentialNextToExe()
    {
        try
        {
            return Directory.EnumerateFiles(AppContext.BaseDirectory, "*.json")
                .FirstOrDefault(f =>
                {
                    var text = File.ReadAllText(f);
                    return text.Contains("\"service_account\"") && text.Contains("\"private_key\"");
                });
        }
        catch
        {
            return null;
        }
    }
}
