using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace StickerPrintApp;

/// <summary>
/// Draws a sticker. The caller must set up the Graphics so that one world unit equals one millimetre;
/// fonts are created in <see cref="GraphicsUnit.World"/> so the same code works for print and preview.
/// </summary>
public static class StickerRenderer
{
    public static SizeF StickerSizeMm(AppSettings s) =>
        s.Landscape
            ? new SizeF((float)s.StickerHeightMm, (float)s.StickerWidthMm)
            : new SizeF((float)s.StickerWidthMm, (float)s.StickerHeightMm);

    public static void Draw(Graphics g, PrintJob job, AppSettings s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var size = StickerSizeMm(s);
        var pad = (float)Math.Max(0, s.PaddingMm);
        var area = new RectangleF(pad, pad, size.Width - 2 * pad, size.Height - 2 * pad);
        if (area.Width <= 1 || area.Height <= 1)
            return;

        if (s.BorderFrame)
        {
            using var pen = new Pen(Color.Black, 0.3f);
            g.DrawRectangle(pen, 0.6f, 0.6f, size.Width - 1.2f, size.Height - 1.2f);
        }

        var gap = area.Height * 0.03f;
        var fullHeight = area.Height;

        // Top: ticket code (small, right-aligned).
        if (s.ShowTicketCode && !string.IsNullOrWhiteSpace(job.TicketCode))
        {
            var h = Math.Max(fullHeight * 0.11f, 2.5f);
            DrawFitted(g, job.TicketCode, s.FontFamily, FontStyle.Regular,
                new RectangleF(area.Left, area.Top, area.Width, h), Brushes.Black, StringAlignment.Far);
            area.Y += h + gap;
            area.Height -= h + gap;
        }

        // Bottom-up: category band, organization, position; the remainder is the name block.
        if (s.ShowCategory && !string.IsNullOrWhiteSpace(job.Category))
        {
            var bandH = Math.Max(fullHeight * 0.2f, 3.5f);
            var band = new RectangleF(area.Left, area.Bottom - bandH, area.Width, bandH);
            if (s.CategoryInverted)
            {
                g.FillRectangle(Brushes.Black, band);
                DrawFitted(g, job.Category, s.FontFamily, FontStyle.Bold, Inset(band, band.Height * 0.12f), Brushes.White);
            }
            else
            {
                DrawFitted(g, job.Category, s.FontFamily, FontStyle.Bold, band, Brushes.Black);
            }
            area.Height -= bandH + gap;
        }

        if (s.ShowOrganization && !string.IsNullOrWhiteSpace(job.Organization))
            area.Height -= DrawBottomLine(g, job.Organization, s.FontFamily, FontStyle.Bold, area, fullHeight * 0.13f) + gap;

        if (s.ShowPosition && !string.IsNullOrWhiteSpace(job.Position))
            area.Height -= DrawBottomLine(g, job.Position, s.FontFamily, FontStyle.Regular, area, fullHeight * 0.12f) + gap;

        DrawName(g, s.ShowTitle ? job.Title : "", job.FullName, s.FontFamily, area);
    }

    /// <summary>Draws one line at the bottom of <paramref name="area"/>; returns the height used.</summary>
    private static float DrawBottomLine(Graphics g, string text, string family, FontStyle style, RectangleF area, float height)
    {
        var h = Math.Max(height, 2.5f);
        DrawFitted(g, text, family, style, new RectangleF(area.Left, area.Bottom - h, area.Width, h), Brushes.Black);
        return h;
    }

    /// <summary>"Title Fullname": uses one line or two (split after the first name), whichever allows the larger font.</summary>
    private static void DrawName(Graphics g, string title, string fullName, string family, RectangleF area)
    {
        var full = $"{title} {fullName}".Trim();
        if (full.Length == 0 || area.Height <= 1)
            return;

        var oneLine = FitSize(g, full, family, FontStyle.Bold, area.Size);

        var split = fullName.Trim().IndexOf(' ');
        if (split > 0)
        {
            var first = $"{title} {fullName[..split]}".Trim();
            var rest = fullName[(split + 1)..].Trim();
            var half = new SizeF(area.Width, area.Height / 2);
            var twoLines = Math.Min(
                FitSize(g, first, family, FontStyle.Bold, half),
                FitSize(g, rest, family, FontStyle.Bold, half));

            if (twoLines > oneLine * 1.15f)
            {
                using var font = new Font(family, twoLines, FontStyle.Bold, GraphicsUnit.World);
                var top = new RectangleF(area.Left, area.Top, area.Width, area.Height / 2);
                var bottom = new RectangleF(area.Left, area.Top + area.Height / 2, area.Width, area.Height / 2);
                DrawText(g, first, font, top, Brushes.Black);
                DrawText(g, rest, font, bottom, Brushes.Black);
                return;
            }
        }

        using (var font = new Font(family, oneLine, FontStyle.Bold, GraphicsUnit.World))
            DrawText(g, full, font, area, Brushes.Black);
    }

    private static void DrawFitted(Graphics g, string text, string family, FontStyle style, RectangleF box, Brush brush,
        StringAlignment align = StringAlignment.Center)
    {
        var size = FitSize(g, text, family, style, box.Size);
        using var font = new Font(family, size, style, GraphicsUnit.World);
        DrawText(g, text, font, box, brush, align);
    }

    private static void DrawText(Graphics g, string text, Font font, RectangleF box, Brush brush,
        StringAlignment align = StringAlignment.Center)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = align,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        g.DrawString(text, font, brush, box, format);
    }

    /// <summary>Largest font size (world units = mm) at which <paramref name="text"/> fits in <paramref name="box"/>.</summary>
    private static float FitSize(Graphics g, string text, string family, FontStyle style, SizeF box)
    {
        // Line height ≈ 1.2 × em for most fonts; Thai needs headroom for upper/lower vowel marks.
        var size = box.Height / 1.35f;
        for (var i = 0; i < 4 && size > 0.5f; i++)
        {
            using var font = new Font(family, size, style, GraphicsUnit.World);
            var measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            var lineH = font.GetHeight(g);
            var scale = Math.Min(box.Width / Math.Max(measured.Width, 0.01f), box.Height / Math.Max(lineH, 0.01f));
            if (scale >= 0.98f)
                break;
            size *= scale * 0.98f;
        }
        return Math.Max(size, 0.5f);
    }

    private static RectangleF Inset(RectangleF r, float d) => new(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);

    /// <summary>Renders a preview bitmap at the given pixels-per-millimetre.</summary>
    public static Bitmap RenderPreview(PrintJob job, AppSettings s, float pxPerMm)
    {
        var size = StickerSizeMm(s);
        var bmp = new Bitmap(Math.Max(1, (int)(size.Width * pxPerMm)), Math.Max(1, (int)(size.Height * pxPerMm)));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.PageUnit = GraphicsUnit.Pixel;
        g.ScaleTransform(pxPerMm, pxPerMm);
        Draw(g, job, s);
        return bmp;
    }
}
