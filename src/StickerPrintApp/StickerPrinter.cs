using System.Drawing;
using System.Drawing.Printing;

namespace StickerPrintApp;

public static class StickerPrinter
{
    public static IReadOnlyList<string> InstalledPrinters() =>
        PrinterSettings.InstalledPrinters.Cast<string>().ToList();

    /// <summary>Prints <paramref name="copies"/> stickers synchronously (call from a background thread).</summary>
    public static void Print(PrintJob job, AppSettings s, int copies)
    {
        if (s.PrinterConnection == PrinterConnection.SerialPort)
        {
            RawPrinter.Print(job, s, copies);
            return;
        }

        using var doc = new PrintDocument();
        doc.PrintController = new StandardPrintController(); // no "Printing..." dialog
        doc.DocumentName = $"Sticker {job.DocumentId}";

        if (!string.IsNullOrWhiteSpace(s.PrinterName))
            doc.PrinterSettings.PrinterName = s.PrinterName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException($"ไม่พบเครื่องพิมพ์ \"{s.PrinterName}\"");

        // PaperSize is in hundredths of an inch.
        var w = MmToHundredthInch(s.StickerWidthMm);
        var h = MmToHundredthInch(s.StickerHeightMm);
        var paper = FindDriverPaper(doc.PrinterSettings, w, h) ?? new PaperSize("Sticker", w, h);

        doc.DefaultPageSettings.PaperSize = paper;
        doc.DefaultPageSettings.Landscape = s.Landscape;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;

        var remaining = Math.Max(1, copies);
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Millimeter;
            // Graphics origin sits at the printer's hard margin; shift back to the physical paper corner,
            // then apply the user's calibration offset.
            g.TranslateTransform(
                (float)(s.OffsetXMm - e.PageSettings.HardMarginX * 0.254),
                (float)(s.OffsetYMm - e.PageSettings.HardMarginY * 0.254));

            StickerRenderer.Draw(g, job, s);

            remaining--;
            e.HasMorePages = remaining > 0;
        };

        doc.Print();
    }

    public static string Describe(AppSettings s) => s.PrinterConnection == PrinterConnection.SerialPort
        ? $"{s.SerialPortName} ({(s.RawLanguage == RawLanguage.EscPos ? "ESC/POS" : "TSPL")})"
        : string.IsNullOrWhiteSpace(s.PrinterName) ? "เครื่องพิมพ์ค่าเริ่มต้นของ Windows" : s.PrinterName;

    private static int MmToHundredthInch(double mm) => (int)Math.Round(mm / 25.4 * 100);

    /// <summary>Some label drivers (Zebra, Godex, TSC...) only honour their own stock definitions, so prefer a matching one.</summary>
    private static PaperSize? FindDriverPaper(PrinterSettings ps, int w, int h)
    {
        const int tolerance = 4; // ≈1 mm
        foreach (PaperSize p in ps.PaperSizes)
        {
            if (Math.Abs(p.Width - w) <= tolerance && Math.Abs(p.Height - h) <= tolerance)
                return p;
        }
        return null;
    }
}
