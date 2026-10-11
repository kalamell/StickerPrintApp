using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StickerPrintApp;

/// <summary>
/// Makes fonts usable without installing them in Windows:
/// the bundled IBM Plex Sans Thai, plus any .ttf/.otf in a "fonts" folder next to the .exe
/// or in %AppData%\StickerPrintApp\fonts (where "เพิ่มไฟล์ฟอนต์..." copies files to).
/// </summary>
internal static class FontManager
{
    public const string DefaultFamily = "IBM Plex Sans Thai";
    private const string FallbackFamily = "Tahoma";

    private static readonly PrivateFontCollection Private = new();
    private static readonly object Gate = new();
    private static readonly HashSet<string> LoadedFiles = new(StringComparer.OrdinalIgnoreCase);

    public static string UserFontFolder => Path.Combine(AppSettings.Folder, "fonts");

    public static void Initialize()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(IsFontFile))
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                AddFontBytes(ms.ToArray());
            }
            catch
            {
                // A broken font must not stop the app; Create() falls back to Tahoma.
            }
        }

        foreach (var folder in new[] { Path.Combine(AppContext.BaseDirectory, "fonts"), UserFontFolder })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder).Where(IsFontFile))
                TryLoadFile(file, out _);
        }
    }

    /// <summary>Copies a font file into the user font folder and loads it. Returns its family name.</summary>
    public static string ImportFile(string path)
    {
        Directory.CreateDirectory(UserFontFolder);
        var target = Path.Combine(UserFontFolder, Path.GetFileName(path));
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(path, target, overwrite: true);

        if (!TryLoadFile(target, out var family))
            throw new InvalidOperationException("ไม่สามารถอ่านไฟล์ฟอนต์นี้ได้");
        return family;
    }

    /// <summary>Private (bundled/imported) families first, then fonts installed in Windows.</summary>
    public static IReadOnlyList<string> AllFamilyNames()
    {
        var names = new List<string>();
        lock (Gate)
            names.AddRange(Private.Families.Select(f => f.Name));
        using var installed = new InstalledFontCollection();
        names.AddRange(installed.Families.Select(f => f.Name).Where(n => !names.Contains(n)));
        return names;
    }

    /// <summary>
    /// Creates a font, falling back to an available style when the family lacks the requested one
    /// (e.g. only IBMPlexSansThai-Bold.ttf is present, so "Regular" text is drawn in Bold).
    /// </summary>
    public static Font Create(string family, float size, FontStyle style, GraphicsUnit unit)
    {
        var ff = FindFamily(family) ?? FindFamily(FallbackFamily) ?? FontFamily.GenericSansSerif;
        FontStyle[] candidates = [style, FontStyle.Bold, FontStyle.Regular, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic];
        var usable = candidates.FirstOrDefault(ff.IsStyleAvailable, style);
        return new Font(ff, size, usable, unit);
    }

    private static FontFamily? FindFamily(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        lock (Gate)
        {
            var priv = Private.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (priv is not null)
                return priv;
        }

        try
        {
            return new FontFamily(name);
        }
        catch (ArgumentException)
        {
            return null; // not installed
        }
    }

    private static bool TryLoadFile(string file, out string family)
    {
        family = "";
        try
        {
            lock (Gate)
            {
                if (!LoadedFiles.Add(Path.GetFullPath(file)))
                {
                    family = FamilyNameOf(file);
                    return family.Length > 0;
                }
            }
            AddFontBytes(File.ReadAllBytes(file)); // read into memory so the file is never locked
            family = FamilyNameOf(file);
            return family.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FamilyNameOf(string file)
    {
        using var probe = new PrivateFontCollection();
        probe.AddFontFile(file);
        return probe.Families.FirstOrDefault()?.Name ?? "";
    }

    private static void AddFontBytes(byte[] data)
    {
        // GDI+ keeps a pointer to this memory for the lifetime of the collection, so it is never freed.
        var ptr = Marshal.AllocCoTaskMem(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        lock (Gate)
            Private.AddMemoryFont(ptr, data.Length);

        // Also register with GDI so printer drivers that resolve fonts by name can find it.
        AddFontMemResourceEx(ptr, (uint)data.Length, IntPtr.Zero, out _);
    }

    private static bool IsFontFile(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);
}
