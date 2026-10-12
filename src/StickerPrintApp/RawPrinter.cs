using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using Microsoft.Win32;

namespace StickerPrintApp;

/// <summary>Command language for printers driven directly over a COM port (no Windows driver).</summary>
public enum RawLanguage
{
    /// <summary>TSPL/TSPL2 — label printers: TSC, Xprinter XP-xxxB, Gprinter, HPRT, iDPRT, ...</summary>
    Tspl,

    /// <summary>ESC/POS raster — receipt / mobile thermal printers.</summary>
    EscPos,
}

/// <summary>
/// Prints to a printer on a (Bluetooth) serial port: the sticker is rendered to a 1-bit bitmap at the
/// printer's DPI and sent as raw TSPL or ESC/POS. This is how Bluetooth SPP label printers that have no
/// Windows driver are used.
/// </summary>
public static class RawPrinter
{
    public sealed record PortInfo(string Port, bool IsBluetooth)
    {
        public override string ToString() => IsBluetooth ? $"{Port} (Bluetooth)" : Port;
    }

    /// <summary>COM ports, flagging Bluetooth ones (their device path is \Device\BthModemN).</summary>
    public static IReadOnlyList<PortInfo> ListPorts()
    {
        var bluetooth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (name.Contains("Bth", StringComparison.OrdinalIgnoreCase) && key!.GetValue(name) is string port)
                    bluetooth.Add(port);
            }
        }
        catch
        {
            // Registry not readable: just list ports without the Bluetooth hint.
        }

        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length > 3 && int.TryParse(p.AsSpan(3), out var n) ? n : int.MaxValue)
            .Select(p => new PortInfo(p, bluetooth.Contains(p)))
            .ToList();
    }

    public static void Print(PrintJob job, PrinterProfile s, string fontFamily, int copies)
    {
        if (string.IsNullOrWhiteSpace(s.SerialPortName))
            throw new InvalidOperationException("ยังไม่ได้เลือก COM port ของเครื่องพิมพ์");

        using var bmp = RenderMonochrome(job, s, fontFamily);
        var data = s.RawLanguage == RawLanguage.EscPos
            ? BuildEscPos(bmp, Math.Max(1, copies))
            : BuildTspl(bmp, s, Math.Max(1, copies));
        Send(s, data);
    }

    /// <summary>Renders the sticker at printer resolution in its physical feed orientation (W × H as configured).</summary>
    private static Bitmap RenderMonochrome(PrintJob job, PrinterProfile s, string fontFamily)
    {
        var dotsPerMm = Math.Max(s.PrinterDpi, 100) / 25.4f;
        var content = StickerRenderer.StickerSizeMm(s);
        var bmp = new Bitmap(
            Math.Max(8, (int)Math.Round(content.Width * dotsPerMm)),
            Math.Max(8, (int)Math.Round(content.Height * dotsPerMm)),
            PixelFormat.Format24bppRgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.PageUnit = GraphicsUnit.Pixel;
            g.ScaleTransform(dotsPerMm, dotsPerMm);
            g.TranslateTransform((float)s.OffsetXMm, (float)s.OffsetYMm, MatrixOrder.Prepend);
            StickerRenderer.Draw(g, job, s, fontFamily);
        }

        if (s.Landscape)
            bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
        if (s.Rotate180)
            bmp.RotateFlip(RotateFlipType.Rotate180FlipNone);
        return bmp;
    }

    /// <summary>Packs the bitmap MSB-first, 1 bit per dot. <paramref name="blackBit"/> is the bit value for a printed dot.</summary>
    private static byte[] Pack(Bitmap bmp, out int widthBytes, bool blackBit)
    {
        widthBytes = (bmp.Width + 7) / 8;
        var packed = new byte[widthBytes * bmp.Height];
        if (!blackBit)
            Array.Fill(packed, (byte)0xFF);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var locked = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = locked.Stride;
            var row = new byte[stride];
            for (var y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * stride, row, 0, stride);
                for (var x = 0; x < bmp.Width; x++)
                {
                    int b = row[x * 3], gr = row[x * 3 + 1], r = row[x * 3 + 2];
                    if ((r * 299 + gr * 587 + b * 114) / 1000 >= 128)
                        continue; // white
                    var mask = (byte)(0x80 >> (x & 7));
                    ref var cell = ref packed[y * widthBytes + (x >> 3)];
                    cell = blackBit ? (byte)(cell | mask) : (byte)(cell & ~mask);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(locked);
        }
        return packed;
    }

    private static byte[] BuildTspl(Bitmap bmp, PrinterProfile s, int copies)
    {
        // In TSPL BITMAP data a 0 bit prints a dot.
        var bits = Pack(bmp, out var widthBytes, blackBit: false);
        var inv = CultureInfo.InvariantCulture;
        var dotsPerMm = Math.Max(s.PrinterDpi, 100) / 25.4;

        using var ms = new MemoryStream();
        void Cmd(string text) => ms.Write(Encoding.ASCII.GetBytes(text + "\r\n"));

        Cmd(string.Format(inv, "SIZE {0:0.##} mm,{1:0.##} mm", bmp.Width / dotsPerMm, bmp.Height / dotsPerMm));
        Cmd(string.Format(inv, "GAP {0:0.##} mm,0 mm", Math.Max(0, s.LabelGapMm)));
        Cmd("DIRECTION 1");
        Cmd("CLS");
        ms.Write(Encoding.ASCII.GetBytes($"BITMAP 0,0,{widthBytes},{bmp.Height},0,"));
        ms.Write(bits);
        Cmd("");
        Cmd($"PRINT 1,{copies}");
        return ms.ToArray();
    }

    private static byte[] BuildEscPos(Bitmap bmp, int copies)
    {
        var bits = Pack(bmp, out var widthBytes, blackBit: true);
        using var ms = new MemoryStream();
        ms.Write([0x1B, 0x40]); // ESC @ — initialise

        for (var c = 0; c < copies; c++)
        {
            // GS v 0 in bands, since many mobile printers have small receive buffers.
            const int band = 128;
            for (var y = 0; y < bmp.Height; y += band)
            {
                var rows = Math.Min(band, bmp.Height - y);
                ms.Write([0x1D, 0x76, 0x30, 0x00,
                    (byte)(widthBytes & 0xFF), (byte)(widthBytes >> 8),
                    (byte)(rows & 0xFF), (byte)(rows >> 8)]);
                ms.Write(bits, y * widthBytes, rows * widthBytes);
            }
            ms.Write([0x1B, 0x64, 0x02]); // ESC d 2 — feed 2 lines
        }
        return ms.ToArray();
    }

    private static void Send(PrinterProfile s, byte[] data)
    {
        using var port = new SerialPort(s.SerialPortName, s.SerialBaudRate <= 0 ? 9600 : s.SerialBaudRate)
        {
            Handshake = Handshake.None,
            WriteTimeout = 15000,
        };
        try
        {
            port.Open(); // for Bluetooth SPP this establishes the radio link and can take a few seconds
        }
        catch (Exception ex)
        {
            throw new IOException($"เปิด {s.SerialPortName} ไม่ได้ — ตรวจสอบว่าเครื่องพิมพ์เปิดอยู่และจับคู่ Bluetooth แล้ว ({ex.Message})", ex);
        }

        const int chunk = 4096;
        for (var i = 0; i < data.Length; i += chunk)
            port.Write(data, i, Math.Min(chunk, data.Length - i));

        // Let the OS drain the buffer before closing, otherwise the tail of the job can be dropped.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (port.BytesToWrite > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Thread.Sleep(300);
    }
}
