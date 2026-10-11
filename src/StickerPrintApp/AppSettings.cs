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

    /// <summary>This PC's station number; only jobs whose <see cref="StationField"/> equals it are printed.</summary>
    public string StationId { get; set; } = "1";

    /// <summary>Job field that selects the station (e.g. printerId). Empty = accept jobs for every station.</summary>
    public string StationField { get; set; } = "printerId";

    public PendingMode PendingMode { get; set; } = PendingMode.StatusPending;
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Realtime;
    public int PollIntervalSeconds { get; set; } = 3;
    public bool AutoConnectOnStart { get; set; } = true;

    /// <summary>Write printed = true / status back to the job document (also prevents double printing).</summary>
    public bool WriteBackStatus { get; set; } = true;

    // --- Field mapping (document field names; dotted paths like "guest.position" are supported) ---
    public string FieldTicketCode { get; set; } = "ticket_code";
    public string FieldTitle { get; set; } = "title";
    public string FieldFullName { get; set; } = "fullname";
    public string FieldPosition { get; set; } = "position";
    public string FieldOrganization { get; set; } = "organization";
    public string FieldCategory { get; set; } = "category";

    // --- Printer / sticker ---
    public string PrinterName { get; set; } = "";
    public string StickerSizeName { get; set; } = "90 x 54 mm (นามบัตร)";
    public double StickerWidthMm { get; set; } = 90;
    public double StickerHeightMm { get; set; } = 54;
    public bool Landscape { get; set; }
    public double OffsetXMm { get; set; }
    public double OffsetYMm { get; set; }
    public double PaddingMm { get; set; } = 2.5;
    public int Copies { get; set; } = 1;
    public bool AutoPrint { get; set; } = true;

    // --- Layout ---
    public string FontFamily { get; set; } = FontManager.DefaultFamily;
    public bool ShowTicketCode { get; set; } = true;
    public bool ShowTitle { get; set; } = true;
    public bool ShowPosition { get; set; } = true;
    public bool ShowOrganization { get; set; } = true;
    public bool ShowCategory { get; set; } = true;
    public bool CategoryInverted { get; set; } = true;
    public bool BorderFrame { get; set; }

    [JsonIgnore]
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickerPrintApp");

    [JsonIgnore]
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings file: fall back to defaults rather than refusing to start.
        }

        var s = new AppSettings();
        s.CredentialPath = FindCredentialNextToExe() ?? "";
        return s;
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
