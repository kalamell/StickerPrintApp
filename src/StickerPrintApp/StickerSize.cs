namespace StickerPrintApp;

public sealed record StickerSize(string Name, double WidthMm, double HeightMm)
{
    public const string CustomName = "กำหนดเอง (Custom)";

    public static readonly IReadOnlyList<StickerSize> Presets =
    [
        new("40 x 30 mm", 40, 30),
        new("50 x 25 mm", 50, 25),
        new("50 x 30 mm", 50, 30),
        new("60 x 40 mm", 60, 40),
        new("70 x 50 mm", 70, 50),
        new("80 x 50 mm", 80, 50),
        new("90 x 54 mm (นามบัตร)", 90, 54),
        new("100 x 50 mm", 100, 50),
        new("100 x 75 mm", 100, 75),
        new("100 x 100 mm", 100, 100),
        new("100 x 150 mm", 100, 150),
        new(CustomName, 0, 0),
    ];

    public override string ToString() => Name;
}
