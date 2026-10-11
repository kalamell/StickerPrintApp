namespace StickerPrintApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance per machine: two instances would race for the same printer.
        using var mutex = new Mutex(true, @"Local\StickerPrintApp.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("โปรแกรม Sticker Print เปิดอยู่แล้ว", "Sticker Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        FontManager.Initialize();
        Application.Run(new MainForm());
    }
}
