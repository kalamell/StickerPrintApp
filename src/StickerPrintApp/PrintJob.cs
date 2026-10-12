namespace StickerPrintApp;

public enum JobState
{
    Waiting,
    Queued,
    Printing,
    Printed,
    Skipped,
    Error,
}

/// <summary>One sticker to print, extracted from a Firestore document.</summary>
public sealed class PrintJob
{
    public required string DocumentId { get; init; }
    public string TicketCode { get; init; } = "";
    public string Title { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Position { get; init; } = "";
    public string Organization { get; init; } = "";
    public string Category { get; init; } = "";
    public string QrData { get; init; } = "";

    /// <summary>printerId as sent in the document ("" when missing).</summary>
    public string PrinterId { get; init; } = "";

    /// <summary>The printer slot (1–5) this job is printed on; resolved from <see cref="PrinterId"/>.</summary>
    public int ProfileId { get; set; }

    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    public JobState State { get; set; } = JobState.Queued;
    public string Message { get; set; } = "";

    /// <summary>Manual reprints bypass the Firestore claim step.</summary>
    public bool IsReprint { get; init; }

    /// <summary>Test stickers are never written back to Firestore.</summary>
    public bool IsTest => DocumentId == "TEST";

    public string DisplayName => $"{Title} {FullName}".Trim();

    /// <summary>(line 1, line 2) for a two-line name: "Title First" / "Last".</summary>
    public (string First, string Last) NameParts()
    {
        var first = FirstName;
        var last = LastName;
        if (first.Length == 0 && last.Length == 0)
        {
            var full = FullName.Trim();
            var space = full.IndexOf(' ');
            (first, last) = space > 0 ? (full[..space], full[(space + 1)..].Trim()) : (full, "");
        }
        return ($"{Title} {first}".Trim(), last);
    }

    public static PrintJob Sample(int profileId) => new()
    {
        DocumentId = "TEST",
        TicketCode = "CEA000123",
        Title = "Mr.",
        FirstName = "Somchai",
        LastName = "Jaidee-Rattanakosin",
        FullName = "Somchai Jaidee-Rattanakosin",
        Position = "Managing Director",
        Organization = "Example Co., Ltd.",
        Category = "Invitation Guests",
        QrData = "CEA000123",
        PrinterId = profileId.ToString(),
        ProfileId = profileId,
        IsReprint = true,
    };

    public PrintJob AsReprint() => new()
    {
        DocumentId = DocumentId,
        TicketCode = TicketCode,
        Title = Title,
        FirstName = FirstName,
        LastName = LastName,
        FullName = FullName,
        Position = Position,
        Organization = Organization,
        Category = Category,
        QrData = QrData,
        PrinterId = PrinterId,
        ProfileId = ProfileId,
        IsReprint = true,
    };
}
