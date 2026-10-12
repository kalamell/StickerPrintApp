using System.Text.Json;

namespace StickerPrintApp;

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>A text element on the sticker. X/Y are in mm from the sticker's top-left corner.</summary>
public sealed class TextElement
{
    public bool Visible { get; set; } = true;

    /// <summary>Anchor X (mm): left edge, centre or right edge depending on <see cref="Align"/>.</summary>
    public double X { get; set; }

    /// <summary>Top of the text (mm), i.e. the top of capital letters.</summary>
    public double Y { get; set; }

    public double FontSizePt { get; set; } = 10;
    public bool Bold { get; set; }
    public bool Uppercase { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;

    /// <summary>Longer text is shrunk to this width (mm). 0 = up to the sticker edge.</summary>
    public double MaxWidthMm { get; set; }

    /// <summary>Distance between lines in em (multi-line elements only).</summary>
    public double LineSpacing { get; set; } = 1.2;
}

public sealed class QrElement
{
    public bool Visible { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double SizeMm { get; set; } = 15;
}

/// <summary>Where everything goes on one printer's sticker.</summary>
public sealed class StickerLayout
{
    /// <summary>"Title First" on line 1 and "Last" on line 2 (otherwise one line).</summary>
    public bool NameTwoLines { get; set; } = true;
    public bool ShowTitle { get; set; } = true;

    public TextElement Name { get; set; } = new();
    public TextElement Position { get; set; } = new();
    public TextElement Organization { get; set; } = new();
    public TextElement TicketCode { get; set; } = new();
    public TextElement Category { get; set; } = new();
    public QrElement Qr { get; set; } = new();

    public bool Border { get; set; }

    /// <summary>The reference design (90 × 54 mm), scaled to the given sticker size.</summary>
    public static StickerLayout CreateDefault(double widthMm, double heightMm)
    {
        var l = new StickerLayout
        {
            Name = new TextElement { X = 7, Y = 7.3, FontSizePt = 16, Bold = true, Uppercase = true, LineSpacing = 1.22, MaxWidthMm = 76 },
            Position = new TextElement { X = 7, Y = 22.6, FontSizePt = 8.5 },
            Organization = new TextElement { X = 7, Y = 26.5, FontSizePt = 8.5 },
            Qr = new QrElement { X = 7, Y = 35, SizeMm = 15.5 },
            TicketCode = new TextElement { Visible = false, X = 83, Y = 4, FontSizePt = 7, Align = TextAlign.Right },
            Category = new TextElement { Visible = false, X = 83, Y = 46, FontSizePt = 9, Bold = true, Align = TextAlign.Right },
        };
        l.Scale(widthMm / 90.0, heightMm / 54.0);
        return l;
    }

    /// <summary>Scales positions by (sx, sy) and sizes by the smaller factor, e.g. when the paper size changes.</summary>
    public void Scale(double sx, double sy)
    {
        if (sx <= 0 || sy <= 0 || (Math.Abs(sx - 1) < 1e-6 && Math.Abs(sy - 1) < 1e-6))
            return;

        var s = Math.Min(sx, sy);
        foreach (var t in TextElements())
        {
            t.X = Math.Round(t.X * sx, 1);
            t.Y = Math.Round(t.Y * sy, 1);
            t.FontSizePt = Math.Round(t.FontSizePt * s * 2, MidpointRounding.AwayFromZero) / 2;
            t.MaxWidthMm = Math.Round(t.MaxWidthMm * sx, 1);
        }
        Qr.X = Math.Round(Qr.X * sx, 1);
        Qr.Y = Math.Round(Qr.Y * sy, 1);
        Qr.SizeMm = Math.Round(Qr.SizeMm * s, 1);
    }

    public IEnumerable<TextElement> TextElements() => [Name, Position, Organization, TicketCode, Category];
}

/// <summary>One printer slot. Jobs whose printerId equals <see cref="Id"/> are printed here.</summary>
public sealed class PrinterProfile
{
    public const int MaxProfiles = 5;

    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Label { get; set; } = "";

    // Output
    public PrinterConnection Connection { get; set; } = PrinterConnection.Windows;
    public string PrinterName { get; set; } = "";
    public string SerialPortName { get; set; } = "";
    public int SerialBaudRate { get; set; } = 9600;
    public RawLanguage RawLanguage { get; set; } = RawLanguage.Tspl;
    public int PrinterDpi { get; set; } = 203;
    public double LabelGapMm { get; set; } = 2;
    public bool Rotate180 { get; set; }

    // Paper
    public string StickerSizeName { get; set; } = "90 x 54 mm (นามบัตร)";
    public double StickerWidthMm { get; set; } = 90;
    public double StickerHeightMm { get; set; } = 54;
    public bool Landscape { get; set; }
    public double OffsetXMm { get; set; }
    public double OffsetYMm { get; set; }
    public int Copies { get; set; } = 1;

    public StickerLayout Layout { get; set; } = StickerLayout.CreateDefault(90, 54);

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"Printer {Id}" : $"Printer {Id} — {Label}";

    public static PrinterProfile CreateDefault(int id) => new() { Id = id, Enabled = id == 1 };

    public PrinterProfile Clone() =>
        JsonSerializer.Deserialize<PrinterProfile>(JsonSerializer.Serialize(this))!;
}
