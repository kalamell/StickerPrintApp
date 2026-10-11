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
    public string FullName { get; init; } = "";
    public string Position { get; init; } = "";
    public string Organization { get; init; } = "";
    public string Category { get; init; } = "";
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    public JobState State { get; set; } = JobState.Queued;
    public string Message { get; set; } = "";

    /// <summary>Manual reprints bypass the Firestore claim step.</summary>
    public bool IsReprint { get; init; }

    /// <summary>Test stickers are never written back to Firestore.</summary>
    public bool IsTest => DocumentId == "TEST";

    public string DisplayName => $"{Title} {FullName}".Trim();

    public static PrintJob Sample() => new()
    {
        DocumentId = "TEST",
        TicketCode = "CEA000123",
        Title = "Mr.",
        FullName = "สมชาย ใจดี",
        Position = "Managing Director",
        Organization = "Example Co., Ltd.",
        Category = "Invitation Guests",
        IsReprint = true,
    };

    public PrintJob AsReprint() => new()
    {
        DocumentId = DocumentId,
        TicketCode = TicketCode,
        Title = Title,
        FullName = FullName,
        Position = Position,
        Organization = Organization,
        Category = Category,
        IsReprint = true,
    };
}
