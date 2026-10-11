using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace StickerPrintApp;

/// <summary>
/// Draws a sticker. The caller must set up the Graphics so that one world unit equals one millimetre;
/// fonts are created in <see cref="GraphicsUnit.World"/> so the same code works for print and preview.
/// </summary>
/// <remarks>
/// Layout, top to bottom: ticket code (right) → name (1–2 tight lines) → position → organization,
/// with the category band pinned to the bottom. Every block gets its height budget up front so the
/// band can never be pushed out or covered, and text is drawn at explicit baselines rather than in
/// layout rectangles (GDI+ silently drops a trimmed line that is a hair taller than its rectangle).
/// </remarks>
public static class StickerRenderer
{
    // Vertical metrics as fractions of the em size. Text sits between "top" (≈ ascender/cap height
    // above the baseline) and the descender; Thai stacks vowels/tone marks above and below, so lines
    // containing Thai get extra room.
    private const float AboveBaseline = 0.80f;
    private const float BelowBaseline = 0.24f;
    private const float ThaiExtraSpacing = 0.20f;

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

        var family = s.FontFamily;
        var full = area.Height;
        var gap = full * 0.035f;
        var top = area.Top;

        // 1. Ticket code, top right.
        if (s.ShowTicketCode && !string.IsNullOrWhiteSpace(job.TicketCode))
        {
            var h = Math.Max(full * 0.10f, 2.2f);
            using var font = FitFont(g, [job.TicketCode], family, FontStyle.Regular, area.Width * 0.6f, h, 0);
            DrawLine(g, job.TicketCode, font, area.Right, top, StringAlignment.Far, Brushes.Black);
            top += h + gap;
        }

        // 2. Category band, reserved at the bottom (drawn last).
        var hasCategory = s.ShowCategory && !string.IsNullOrWhiteSpace(job.Category);
        var bandH = hasCategory ? Math.Max(full * 0.2f, 3.5f) : 0;
        var bottom = area.Bottom - (hasCategory ? bandH + gap : 0);

        // 3. Position + organization share one font size so they read as a pair.
        var details = new List<(string Text, FontStyle Style)>();
        if (s.ShowPosition && !string.IsNullOrWhiteSpace(job.Position))
            details.Add((job.Position, FontStyle.Regular));
        if (s.ShowOrganization && !string.IsNullOrWhiteSpace(job.Organization))
            details.Add((job.Organization, FontStyle.Bold));

        var detailLineH = Math.Max(full * 0.115f, 2.4f);
        var detailSpacing = 1.12f;
        var detailEm = details.Count == 0
            ? 0
            : details.Min(d => FitEm(g, [d.Text], family, d.Style, area.Width, detailLineH, 0));
        var detailsH = details.Count == 0 ? 0 : BlockHeight(detailEm, details.Count, detailSpacing, details.Select(d => d.Text));

        // 4. Name gets what is left; it is never allowed into the band's space.
        var title = s.ShowTitle ? job.Title : "";
        var nameLines = ChooseNameLines(g, title, job.FullName, family, area.Width,
            bottom - top - (detailsH > 0 ? detailsH + gap : 0), (float)s.NameLineSpacing, out var nameEm);
        // Keep very short names from dwarfing everything else.
        nameEm = Math.Min(nameEm, full * 0.26f);
        var nameH = nameLines.Length == 0 ? 0 : BlockHeight(nameEm, nameLines.Length, (float)s.NameLineSpacing, nameLines);

        // Stack name + details; optionally centre the group in the free space.
        var groupH = nameH + (nameH > 0 && detailsH > 0 ? gap : 0) + detailsH;
        var y = s.CenterVertically ? top + Math.Max(0, (bottom - top - groupH) / 2) : top;

        var state = g.Save();
        g.SetClip(new RectangleF(area.Left - pad, area.Top - pad, area.Width + 2 * pad, bottom - area.Top + pad));
        if (nameLines.Length > 0)
        {
            using var font = FontManager.Create(family, nameEm, FontStyle.Bold, GraphicsUnit.World);
            DrawBlock(g, nameLines, font, area, y, (float)s.NameLineSpacing, Brushes.Black);
            y += nameH + gap;
        }
        var detailAdvance = detailEm * (detailSpacing + (details.Any(d => ContainsThai(d.Text)) ? ThaiExtraSpacing : 0));
        foreach (var (text, style) in details)
        {
            using var font = FontManager.Create(family, detailEm, style, GraphicsUnit.World);
            DrawBlock(g, [text], font, area, y, detailSpacing, Brushes.Black);
            y += detailAdvance;
        }
        g.Restore(state);

        // 5. Category band last, so nothing can paint over it.
        if (hasCategory)
        {
            var band = new RectangleF(area.Left, area.Bottom - bandH, area.Width, bandH);
            var brush = Brushes.Black;
            if (s.CategoryInverted)
            {
                g.FillRectangle(Brushes.Black, band);
                brush = Brushes.White;
            }
            var inset = band.Height * 0.14f;
            using var font = FitFont(g, [job.Category], family, FontStyle.Bold, band.Width - 2 * inset, band.Height - 2 * inset, 0);
            var textH = BlockHeight(font.Size, 1, 1, [job.Category]);
            DrawBlock(g, [job.Category], font, band, band.Top + (band.Height - textH) / 2, 1, brush);
        }
    }

    /// <summary>
    /// "Title Fullname" on one line, or split into two balanced lines at a space — whichever gives the
    /// larger font (one line wins ties within 10 %, as it reads better).
    /// </summary>
    private static string[] ChooseNameLines(Graphics g, string title, string fullName, string family,
        float maxW, float maxH, float spacing, out float em)
    {
        var text = $"{title} {fullName}".Trim();
        em = 0;
        if (text.Length == 0 || maxH <= 1)
            return [];

        string[] best = [text];
        em = FitEm(g, best, family, FontStyle.Bold, maxW, maxH, spacing);

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[]? bestSplit = null;
        var bestSplitEm = 0f;
        for (var i = 1; i < words.Length; i++)
        {
            string[] candidate = [string.Join(' ', words[..i]), string.Join(' ', words[i..])];
            var e = FitEm(g, candidate, family, FontStyle.Bold, maxW, maxH, spacing);
            if (e > bestSplitEm)
            {
                bestSplitEm = e;
                bestSplit = candidate;
            }
        }

        if (bestSplit is not null && bestSplitEm > em * 1.1f)
        {
            em = bestSplitEm;
            return bestSplit;
        }
        return best;
    }

    /// <summary>Largest em (mm) at which every line fits <paramref name="maxW"/> and the block fits <paramref name="maxH"/>.</summary>
    private static float FitEm(Graphics g, IReadOnlyList<string> lines, string family, FontStyle style,
        float maxW, float maxH, float spacing)
    {
        // Height: block height is linear in em.
        var em = maxH / BlockHeight(1, lines.Count, spacing, lines);

        // Width: text width is (nearly) linear in em; measure at a reference size and scale.
        const float reference = 10f;
        using var font = FontManager.Create(family, reference, style, GraphicsUnit.World);
        var widest = lines.Max(l => g.MeasureString(l, font, PointF.Empty, StringFormat.GenericTypographic).Width);
        if (widest > 0)
            em = Math.Min(em, maxW / widest * reference * 0.98f);
        return Math.Max(em, 0.5f);
    }

    private static Font FitFont(Graphics g, IReadOnlyList<string> lines, string family, FontStyle style,
        float maxW, float maxH, float spacing) =>
        FontManager.Create(family, FitEm(g, lines, family, style, maxW, maxH, spacing), style, GraphicsUnit.World);

    /// <summary>Visual height of <paramref name="count"/> lines: top of first line to descender of last.</summary>
    private static float BlockHeight(float em, int count, float spacing, IEnumerable<string> lines)
    {
        var thai = lines.Any(ContainsThai);
        var lineGap = spacing + (thai ? ThaiExtraSpacing : 0);
        var above = AboveBaseline + (thai ? 0.12f : 0);
        return em * (above + BelowBaseline + (count - 1) * lineGap);
    }

    /// <summary>Draws centred lines whose visual top starts at <paramref name="top"/>.</summary>
    private static void DrawBlock(Graphics g, IReadOnlyList<string> lines, Font font, RectangleF area, float top,
        float spacing, Brush brush)
    {
        var em = font.Size;
        var thai = lines.Any(ContainsThai);
        var baseline = top + em * (AboveBaseline + (thai ? 0.12f : 0));
        var lineGap = em * (spacing + (thai ? ThaiExtraSpacing : 0));
        var cx = area.Left + area.Width / 2;
        foreach (var line in lines)
        {
            DrawAtBaseline(g, line, font, cx, baseline, StringAlignment.Center, brush);
            baseline += lineGap;
        }
    }

    private static void DrawLine(Graphics g, string text, Font font, float x, float top, StringAlignment align, Brush brush) =>
        DrawAtBaseline(g, text, font, x, top + font.Size * (AboveBaseline + (ContainsThai(text) ? 0.12f : 0)), align, brush);

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

    private static bool ContainsThai(string text) => text.Any(c => c is >= '฀' and <= '๿');

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
