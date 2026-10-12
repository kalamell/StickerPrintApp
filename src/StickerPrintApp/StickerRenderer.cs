using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using QRCoder;

namespace StickerPrintApp;

/// <summary>Where an element landed on the sticker (mm), for click-and-drag in the preview.</summary>
public sealed record ElementHit(string Key, RectangleF Bounds);

/// <summary>
/// Draws a sticker from a <see cref="StickerLayout"/>: every element sits at its own X/Y in mm
/// from the sticker's top-left corner. The caller sets up the Graphics so one world unit = 1 mm;
/// fonts use <see cref="GraphicsUnit.World"/> so the same code serves print and preview.
/// </summary>
public static class StickerRenderer
{
    public const string KeyName = "name";
    public const string KeyPosition = "position";
    public const string KeyOrganization = "organization";
    public const string KeyTicket = "ticket";
    public const string KeyCategory = "category";
    public const string KeyQr = "qr";

    private const float MmPerPt = 25.4f / 72f;

    // Vertical metrics as fractions of the em size: Y is the top of capitals, the baseline sits
    // AboveBaseline below it. Thai stacks marks above and below, so Thai lines get extra room.
    private const float AboveBaseline = 0.72f;
    private const float BelowBaseline = 0.24f;
    private const float ThaiAbove = 0.12f;
    private const float ThaiExtraSpacing = 0.20f;

    public static SizeF StickerSizeMm(PrinterProfile p) =>
        p.Landscape
            ? new SizeF((float)p.StickerHeightMm, (float)p.StickerWidthMm)
            : new SizeF((float)p.StickerWidthMm, (float)p.StickerHeightMm);

    public static void Draw(Graphics g, PrintJob job, PrinterProfile p, string family, List<ElementHit>? hits = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var size = StickerSizeMm(p);
        var l = p.Layout;

        if (l.Border)
        {
            using var pen = new Pen(Color.Black, 0.3f);
            g.DrawRectangle(pen, 0.6f, 0.6f, size.Width - 1.2f, size.Height - 1.2f);
        }

        var title = l.ShowTitle ? job.Title : "";
        string[] nameLines;
        if (l.NameTwoLines)
        {
            var (first, last) = job.NameParts();
            if (!l.ShowTitle && job.Title.Length > 0 && first.StartsWith(job.Title, StringComparison.Ordinal))
                first = first[job.Title.Length..].Trim();
            nameLines = new[] { first, last }.Where(x => x.Length > 0).ToArray();
        }
        else
        {
            nameLines = [$"{title} {job.FullName}".Trim()];
        }

        DrawText(g, KeyName, l.Name, nameLines, family, size, hits);
        DrawText(g, KeyPosition, l.Position, [job.Position], family, size, hits);
        DrawText(g, KeyOrganization, l.Organization, [job.Organization], family, size, hits);
        DrawText(g, KeyTicket, l.TicketCode, [job.TicketCode], family, size, hits);
        DrawText(g, KeyCategory, l.Category, [job.Category], family, size, hits);
        DrawQr(g, l.Qr, job.QrData, hits);
    }

    private static void DrawText(Graphics g, string key, TextElement e, string[] lines, string family, SizeF sticker,
        List<ElementHit>? hits)
    {
        lines = lines.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => e.Uppercase ? x.ToUpperInvariant() : x)
            .ToArray();
        if (!e.Visible || lines.Length == 0)
            return;

        var x = (float)e.X;
        var style = e.Bold ? FontStyle.Bold : FontStyle.Regular;
        var em = (float)Math.Max(e.FontSizePt, 1) * MmPerPt;

        // Shrink long text to the allowed width (explicit max width, or up to the sticker edge).
        var room = e.Align switch
        {
            TextAlign.Center => 2 * Math.Min(x, sticker.Width - x),
            TextAlign.Right => x,
            _ => sticker.Width - x,
        } - 1f;
        var maxW = e.MaxWidthMm > 0 ? Math.Min((float)e.MaxWidthMm, room) : room;
        var widest = MeasureWidest(g, lines, family, style, em);
        if (maxW > 1 && widest > maxW)
        {
            em *= maxW / widest;
            widest = maxW;
        }

        using var font = FontManager.Create(family, em, style, GraphicsUnit.World);
        var thai = lines.Any(ContainsThai);
        var above = em * (AboveBaseline + (thai ? ThaiAbove : 0));
        var lineGap = em * ((float)e.LineSpacing + (thai ? ThaiExtraSpacing : 0));
        var align = e.Align switch
        {
            TextAlign.Center => StringAlignment.Center,
            TextAlign.Right => StringAlignment.Far,
            _ => StringAlignment.Near,
        };

        var baseline = (float)e.Y + above;
        foreach (var line in lines)
        {
            DrawAtBaseline(g, line, font, x, baseline, align, Brushes.Black);
            baseline += lineGap;
        }

        if (hits is not null)
        {
            var left = align switch
            {
                StringAlignment.Center => x - widest / 2,
                StringAlignment.Far => x - widest,
                _ => x,
            };
            var height = above + (lines.Length - 1) * lineGap + em * BelowBaseline;
            hits.Add(new ElementHit(key, new RectangleF(left, (float)e.Y, Math.Max(widest, 2), height)));
        }
    }

    private static float MeasureWidest(Graphics g, string[] lines, string family, FontStyle style, float em)
    {
        // Width is linear in size; measure at a reference size for stable results.
        const float reference = 10f;
        using var font = FontManager.Create(family, reference, style, GraphicsUnit.World);
        var widest = lines.Max(l => g.MeasureString(l, font, PointF.Empty, StringFormat.GenericTypographic).Width);
        return widest * em / reference;
    }

    private static void DrawAtBaseline(Graphics g, string text, Font font, float x, float baseline,
        StringAlignment align, Brush brush)
    {
        var ff = font.FontFamily;
        var ascent = font.Size * ff.GetCellAscent(font.Style) / ff.GetEmHeight(font.Style);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = align, // relative to x: Near = starts at x, Center = centred on x, Far = ends at x
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        g.DrawString(text, font, brush, new PointF(x, baseline - ascent), format);
    }

    private static void DrawQr(Graphics g, QrElement e, string data, List<ElementHit>? hits)
    {
        if (!e.Visible || string.IsNullOrWhiteSpace(data) || e.SizeMm <= 1)
            return;

        using var generator = new QRCodeGenerator();
        using var qr = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        var matrix = qr.ModuleMatrix;
        const int quiet = 4; // QRCoder includes a 4-module quiet zone; the white sticker provides it
        var modules = matrix.Count - 2 * quiet;
        if (modules <= 0)
            return;

        var cell = (float)e.SizeMm / modules;
        var x0 = (float)e.X;
        var y0 = (float)e.Y;

        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None; // crisp module edges
        for (var r = 0; r < modules; r++)
        {
            var row = matrix[r + quiet];
            for (var c = 0; c < modules; c++)
            {
                if (!row[c + quiet])
                    continue;
                var run = 1; // merge horizontal runs into one rectangle
                while (c + run < modules && row[c + run + quiet])
                    run++;
                // Slight overlap avoids hairline gaps between modules.
                g.FillRectangle(Brushes.Black, x0 + c * cell, y0 + r * cell, run * cell + 0.02f, cell + 0.02f);
                c += run - 1;
            }
        }
        g.SmoothingMode = smoothing;

        hits?.Add(new ElementHit(KeyQr, new RectangleF(x0, y0, (float)e.SizeMm, (float)e.SizeMm)));
    }

    private static bool ContainsThai(string text) => text.Any(c => c is >= '฀' and <= '๿');

    /// <summary>Renders a preview bitmap at the given pixels-per-millimetre.</summary>
    public static Bitmap RenderPreview(PrintJob job, PrinterProfile p, string family, float pxPerMm, List<ElementHit>? hits = null)
    {
        var size = StickerSizeMm(p);
        var bmp = new Bitmap(Math.Max(1, (int)(size.Width * pxPerMm)), Math.Max(1, (int)(size.Height * pxPerMm)));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.PageUnit = GraphicsUnit.Pixel;
        g.ScaleTransform(pxPerMm, pxPerMm);
        Draw(g, job, p, family, hits);
        return bmp;
    }
}
