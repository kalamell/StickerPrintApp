using System.Drawing;
using System.Text.Json;

namespace StickerPrintApp;

public sealed class MainForm : Form
{
    private const string DefaultPrinterLabel = "(เครื่องพิมพ์ค่าเริ่มต้นของ Windows)";
    private const int MaxRows = 1000;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly JobProcessor _processor;
    private readonly CancellationTokenSource _appCts = new();
    private readonly Dictionary<PrintJob, DataGridViewRow> _rows = [];
    private readonly Dictionary<string, PrintJob> _waiting = []; // jobs held while Auto print is off

    private FirestoreJobSource? _source;
    private CancellationTokenSource? _listenCts;
    private AppSettings? _connectedWith;
    private bool _loading;
    private int _printedCount;
    private int _errorCount;

    // Top bar
    private readonly Label _lblStatus = new() { AutoSize = true, Font = new Font("Tahoma", 10f, FontStyle.Bold), Padding = new Padding(0, 6, 12, 0) };
    private readonly Button _btnConnect = new() { Text = "เชื่อมต่อ", AutoSize = true };
    private readonly CheckBox _chkAutoPrint = new() { Text = "พิมพ์อัตโนมัติ (Auto print)", AutoSize = true, Padding = new Padding(12, 4, 0, 0) };
    private readonly Button _btnTest = new() { Text = "พิมพ์ทดสอบ", AutoSize = true };
    private readonly Label _lblCounts = new() { AutoSize = true, Padding = new Padding(12, 6, 0, 0) };

    // Jobs tab
    private readonly DataGridView _grid = new();
    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };

    // Preview panel
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.FromArgb(230, 230, 230) };
    private readonly Label _lblPreviewInfo = new() { Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _btnPrintSelected = new() { Text = "พิมพ์ / พิมพ์ซ้ำ รายการที่เลือก", Dock = DockStyle.Bottom, Height = 40 };

    // Settings: Firebase
    private readonly TextBox _txtCred = new() { Width = 360 };
    private readonly Button _btnBrowse = new() { Text = "เลือกไฟล์...", AutoSize = true };
    private readonly Label _lblAccount = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _txtProject = new() { Width = 240 };
    private readonly TextBox _txtDatabase = new() { Width = 240 };
    private readonly TextBox _txtCollection = new() { Width = 240 };
    private readonly ComboBox _cboStationId = new() { Width = 120 };
    private readonly TextBox _txtStationField = new() { Width = 240 };
    private readonly CheckBox _chkAcceptUnassigned = new() { Text = "รับงานที่ไม่ได้ระบุเครื่องด้วย (Station ที่ว่างก่อนได้งาน ไม่พิมพ์ซ้ำ)", AutoSize = true };
    private readonly ComboBox _cboPending = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _cboConnMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _numPoll = new() { Minimum = 1, Maximum = 60, Width = 80 };
    private readonly CheckBox _chkWriteBack = new() { Text = "หลังพิมพ์ mark printed = true / status กลับไปที่ Firestore", AutoSize = true };
    private readonly CheckBox _chkAutoConnect = new() { Text = "เชื่อมต่ออัตโนมัติเมื่อเปิดโปรแกรม", AutoSize = true };

    // Settings: field mapping
    private readonly TextBox _txtFTicket = new() { Width = 240 };
    private readonly TextBox _txtFTitle = new() { Width = 240 };
    private readonly TextBox _txtFFullName = new() { Width = 240 };
    private readonly TextBox _txtFPosition = new() { Width = 240 };
    private readonly TextBox _txtFOrganization = new() { Width = 240 };
    private readonly TextBox _txtFCategory = new() { Width = 240 };

    // Settings: printer & sticker
    private readonly ComboBox _cboPrinter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Button _btnRefreshPrinters = new() { Text = "รีเฟรช", AutoSize = true };
    private readonly ComboBox _cboRawLang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _cboDpi = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly NumericUpDown _numGap = Num(0, 20, 0.5m);
    private readonly ComboBox _cboBaud = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly CheckBox _chkRotate180 = new() { Text = "กลับหัว 180°", AutoSize = true };
    private readonly ComboBox _cboSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly NumericUpDown _numW = Num(10, 300, 1);
    private readonly NumericUpDown _numH = Num(10, 300, 1);
    private readonly CheckBox _chkLandscape = new() { Text = "แนวนอน (Landscape / หมุน 90°)", AutoSize = true };
    private readonly NumericUpDown _numOffX = Num(-20, 20, 0.5m);
    private readonly NumericUpDown _numOffY = Num(-20, 20, 0.5m);
    private readonly NumericUpDown _numPad = Num(0, 20, 0.5m);
    private readonly NumericUpDown _numCopies = new() { Minimum = 1, Maximum = 20, Width = 80 };

    // Settings: layout
    private readonly ComboBox _cboFont = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button _btnAddFont = new() { Text = "เพิ่มไฟล์ฟอนต์...", AutoSize = true };
    private readonly CheckBox _chkShowTicket = new() { Text = "แสดง Ticket code", AutoSize = true };
    private readonly CheckBox _chkShowTitle = new() { Text = "แสดงคำนำหน้า (Title) หน้าชื่อ", AutoSize = true };
    private readonly CheckBox _chkShowPosition = new() { Text = "แสดงตำแหน่ง (Position)", AutoSize = true };
    private readonly CheckBox _chkShowOrganization = new() { Text = "แสดงองค์กร (Organization)", AutoSize = true };
    private readonly CheckBox _chkShowCategory = new() { Text = "แสดง Category", AutoSize = true };
    private readonly CheckBox _chkInverted = new() { Text = "Category แบบแถบดำ ตัวอักษรขาว", AutoSize = true };
    private readonly CheckBox _chkBorder = new() { Text = "มีกรอบรอบสติกเกอร์", AutoSize = true };
    private readonly Button _btnSave = new() { Text = "บันทึกการตั้งค่า", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };

    public MainForm()
    {
        Text = "Sticker Print — Firebase Auto Print";
        Size = new Size(1280, 800);
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9f);

        _processor = new JobProcessor(() => _settings, () => _source);
        _processor.JobChanged += job => Ui(() => OnJobChanged(job));
        _processor.Log += LogLine;

        BuildLayout();
        LoadSettingsIntoControls();
        WireEvents();
        SetConnected(false, "ไม่ได้เชื่อมต่อ");
        UpdateTitle();
        UpdateCounts();
        RefreshPreview();

        _ = Task.Run(() => _processor.RunAsync(_appCts.Token));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_settings.AutoConnectOnStart && File.Exists(_settings.CredentialPath))
            Connect();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ReadControlsIntoSettings();
        try { _settings.Save(); } catch { /* ignore on exit */ }
        _listenCts?.Cancel();
        _appCts.Cancel();
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        top.Controls.AddRange([_lblStatus, _btnConnect, _chkAutoPrint, _btnTest, _lblCounts]);

        // Right: preview
        var previewPanel = new Panel { Dock = DockStyle.Right, Width = 380, Padding = new Padding(8) };
        var previewTitle = new Label { Text = "ตัวอย่างสติกเกอร์", Dock = DockStyle.Top, Height = 24, Font = new Font("Tahoma", 10f, FontStyle.Bold) };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(_lblPreviewInfo);
        previewPanel.Controls.Add(previewTitle);
        previewPanel.Controls.Add(_btnPrintSelected);

        // Tabs
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildJobsTab());
        tabs.TabPages.Add(BuildSettingsTab());

        Controls.Add(tabs);
        Controls.Add(previewPanel);
        Controls.Add(top);
    }

    private TabPage BuildJobsTab()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.Columns.Add("time", "เวลา");
        _grid.Columns.Add("doc", "Document ID");
        _grid.Columns.Add("ticket", "Ticket code");
        _grid.Columns.Add("name", "ชื่อ-นามสกุล");
        _grid.Columns.Add("position", "ตำแหน่ง");
        _grid.Columns.Add("org", "องค์กร");
        _grid.Columns.Add("category", "Category");
        _grid.Columns.Add("state", "สถานะ");
        _grid.Columns.Add("msg", "รายละเอียด");
        _grid.Columns["time"]!.FillWeight = 50;
        _grid.Columns["state"]!.FillWeight = 60;
        _grid.Columns["msg"]!.FillWeight = 160;

        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 160, Padding = new Padding(0, 4, 0, 0) };
        logPanel.Controls.Add(_txtLog);

        var page = new TabPage("งานพิมพ์");
        page.Controls.Add(_grid);
        page.Controls.Add(new Splitter { Dock = DockStyle.Bottom });
        page.Controls.Add(logPanel);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8),
        };

        var credRow = Row(_txtCred, _btnBrowse);
        flow.Controls.Add(Group("Firebase / Firestore",
            ("Service account (.json)", credRow),
            ("", _lblAccount),
            ("Project ID", _txtProject),
            ("Database", _txtDatabase),
            ("Collection", _txtCollection),
            ("Printer Station # (เครื่องนี้)", _cboStationId),
            ("ฟิลด์ที่ใช้กรอง Station", _txtStationField),
            ("", _chkAcceptUnassigned),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "รับเฉพาะ job ที่ฟิลด์นี้ = เลข Station (รองรับทั้งตัวเลข 1 และข้อความ \"1\"); เว้นว่าง = รับทุก job" }),
            ("งานที่ยังไม่พิมพ์คือ", _cboPending),
            ("การเชื่อมต่อ", _cboConnMode),
            ("Polling ทุก (วินาที)", _numPoll),
            ("", _chkWriteBack),
            ("", _chkAutoConnect)));

        flow.Controls.Add(Group("ชื่อฟิลด์ในเอกสาร (Field mapping)",
            ("Ticket code", _txtFTicket),
            ("Title (คำนำหน้า)", _txtFTitle),
            ("Full name", _txtFFullName),
            ("Position", _txtFPosition),
            ("Organization", _txtFOrganization),
            ("Category", _txtFCategory),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "ชื่อฟิลด์ใน Firestore เช่น category หรือ guest_role; ถ้า fullname ว่าง จะใช้ first_name + last_name" })));

        flow.Controls.Add(Group("เครื่องพิมพ์และขนาดสติกเกอร์",
            ("เครื่องพิมพ์", Row(_cboPrinter, _btnRefreshPrinters)),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Bluetooth ที่ไม่มีไดรเวอร์: จับคู่ใน Windows แล้วเลือกรายการ \"Bluetooth / COM\" (ดู README)" }),
            ("ภาษาเครื่องพิมพ์ (COM)", _cboRawLang),
            ("ความละเอียด / ระยะห่างฉลาก", Row(_cboDpi, new Label { Text = "dpi   gap (mm)", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _numGap)),
            ("Baud rate (COM)", Row(_cboBaud, _chkRotate180)),
            ("ขนาดสติกเกอร์", _cboSize),
            ("กว้าง × สูง (mm)", Row(_numW, new Label { Text = "×", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _numH)),
            ("", _chkLandscape),
            ("ขอบใน (mm)", _numPad),
            ("ชดเชยตำแหน่ง X / Y (mm)", Row(_numOffX, _numOffY)),
            ("จำนวนต่อคน", _numCopies)));

        flow.Controls.Add(Group("รูปแบบ",
            ("ฟอนต์", Row(_cboFont, _btnAddFont)),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "มี IBM Plex Sans Thai ในตัวแล้ว ไม่ต้องติดตั้ง; ฟอนต์อื่นเลือกไฟล์ .ttf/.otf หรือวางในโฟลเดอร์ fonts ข้าง .exe" }),
            ("", _chkShowTicket),
            ("", _chkShowTitle),
            ("", _chkShowPosition),
            ("", _chkShowOrganization),
            ("", _chkShowCategory),
            ("", _chkInverted),
            ("", _chkBorder)));

        flow.Controls.Add(_btnSave);

        var page = new TabPage("ตั้งค่า");
        page.Controls.Add(flow);
        return page;
    }

    private static GroupBox Group(string title, params (string Label, Control Control)[] rows)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = new Point(8, 22), Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, control) in rows)
        {
            table.RowCount++;
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
            table.Controls.Add(control);
        }

        var box = new GroupBox { Text = title, AutoSize = true, MinimumSize = new Size(720, 0), Padding = new Padding(8) };
        box.Controls.Add(table);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        p.Controls.AddRange(controls);
        return p;
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal step) =>
        new() { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = 1, Width = 80 };

    // ------------------------------------------------------------------ settings <-> controls

    private void LoadSettingsIntoControls()
    {
        _loading = true;
        try
        {
            var s = _settings;
            _txtCred.Text = s.CredentialPath;
            _txtProject.Text = s.ProjectId;
            _txtDatabase.Text = s.DatabaseId;
            _txtCollection.Text = s.Collection;
            _cboStationId.Items.Clear();
            _cboStationId.Items.AddRange(["1", "2", "3"]);
            _cboStationId.Text = s.StationId;
            _txtStationField.Text = s.StationField;
            _chkAcceptUnassigned.Checked = s.AcceptUnassigned;
            _cboPending.Items.Clear();
            _cboPending.Items.AddRange(["status == \"pending\"", "printed == false"]);
            _cboPending.SelectedIndex = s.PendingMode == PendingMode.PrintedFalse ? 1 : 0;
            _cboConnMode.Items.Clear();
            _cboConnMode.Items.AddRange(["Realtime (แนะนำ — ทันที)", "Polling (สำหรับเครือข่ายที่บล็อก gRPC)"]);
            _cboConnMode.SelectedIndex = s.ConnectionMode == ConnectionMode.Polling ? 1 : 0;
            _numPoll.Value = Math.Clamp(s.PollIntervalSeconds, 1, 60);
            _chkWriteBack.Checked = s.WriteBackStatus;
            _chkAutoConnect.Checked = s.AutoConnectOnStart;
            _chkAutoPrint.Checked = s.AutoPrint;

            _txtFTicket.Text = s.FieldTicketCode;
            _txtFTitle.Text = s.FieldTitle;
            _txtFFullName.Text = s.FieldFullName;
            _txtFPosition.Text = s.FieldPosition;
            _txtFOrganization.Text = s.FieldOrganization;
            _txtFCategory.Text = s.FieldCategory;

            _cboRawLang.Items.Clear();
            _cboRawLang.Items.AddRange(["TSPL — เครื่องพิมพ์ฉลาก (TSC, Xprinter, Gprinter, HPRT)", "ESC/POS — เครื่องพิมพ์ใบเสร็จ/พกพา"]);
            _cboRawLang.SelectedIndex = s.RawLanguage == RawLanguage.EscPos ? 1 : 0;
            _cboDpi.Items.Clear();
            _cboDpi.Items.AddRange(["203", "300"]);
            _cboDpi.SelectedItem = s.PrinterDpi >= 300 ? "300" : "203";
            _numGap.Value = Clamp(_numGap, s.LabelGapMm);
            _cboBaud.Items.Clear();
            _cboBaud.Items.AddRange(["9600", "19200", "38400", "57600", "115200"]);
            _cboBaud.SelectedItem = s.SerialBaudRate.ToString();
            if (_cboBaud.SelectedIndex < 0) _cboBaud.SelectedIndex = 0;
            _chkRotate180.Checked = s.Rotate180;

            LoadPrinters();

            _cboSize.Items.Clear();
            foreach (var p in StickerSize.Presets)
                _cboSize.Items.Add(p);
            _cboSize.SelectedItem = StickerSize.Presets.FirstOrDefault(p => p.Name == s.StickerSizeName)
                                    ?? StickerSize.Presets.First(p => p.Name == StickerSize.CustomName);
            _numW.Value = Clamp(_numW, s.StickerWidthMm);
            _numH.Value = Clamp(_numH, s.StickerHeightMm);
            UpdateSizeEditable();
            _chkLandscape.Checked = s.Landscape;
            _numPad.Value = Clamp(_numPad, s.PaddingMm);
            _numOffX.Value = Clamp(_numOffX, s.OffsetXMm);
            _numOffY.Value = Clamp(_numOffY, s.OffsetYMm);
            _numCopies.Value = Clamp(_numCopies, s.Copies);

            LoadFonts(s.FontFamily);

            _chkShowTicket.Checked = s.ShowTicketCode;
            _chkShowTitle.Checked = s.ShowTitle;
            _chkShowPosition.Checked = s.ShowPosition;
            _chkShowOrganization.Checked = s.ShowOrganization;
            _chkShowCategory.Checked = s.ShowCategory;
            _chkInverted.Checked = s.CategoryInverted;
            _chkBorder.Checked = s.BorderFrame;

            ShowAccountInfo();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>An entry in the printer list: a Windows printer or a COM port.</summary>
    private sealed record PrinterChoice(PrinterConnection Kind, string Name, string Label)
    {
        public override string ToString() => Label;
    }

    private void LoadPrinters()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _cboPrinter.Items.Clear();
            _cboPrinter.Items.Add(new PrinterChoice(PrinterConnection.Windows, "", DefaultPrinterLabel));
            try
            {
                foreach (var p in StickerPrinter.InstalledPrinters())
                    _cboPrinter.Items.Add(new PrinterChoice(PrinterConnection.Windows, p, p));
            }
            catch (Exception ex)
            {
                LogLine($"อ่านรายชื่อเครื่องพิมพ์ไม่สำเร็จ: {ex.Message}");
            }

            try
            {
                foreach (var port in RawPrinter.ListPorts())
                    _cboPrinter.Items.Add(new PrinterChoice(PrinterConnection.SerialPort, port.Port, $"Bluetooth / COM: {port}"));
            }
            catch (Exception ex)
            {
                LogLine($"อ่านรายชื่อ COM port ไม่สำเร็จ: {ex.Message}");
            }

            var s = _settings;
            var wanted = s.PrinterConnection == PrinterConnection.SerialPort
                ? new PrinterChoice(PrinterConnection.SerialPort, s.SerialPortName, $"Bluetooth / COM: {s.SerialPortName} (ไม่พบตอนนี้)")
                : new PrinterChoice(PrinterConnection.Windows, s.PrinterName, $"{s.PrinterName} (ไม่พบตอนนี้)");
            var match = _cboPrinter.Items.Cast<PrinterChoice>().FirstOrDefault(c => c.Kind == wanted.Kind && c.Name == wanted.Name);
            if (match is null && wanted.Name.Length > 0)
                _cboPrinter.Items.Add(match = wanted); // keep the saved choice visible even if offline right now
            _cboPrinter.SelectedItem = match ?? _cboPrinter.Items[0];
        }
        finally
        {
            _loading = wasLoading;
        }
        UpdatePrinterControls();
    }

    private void UpdatePrinterControls()
    {
        var serial = (_cboPrinter.SelectedItem as PrinterChoice)?.Kind == PrinterConnection.SerialPort;
        foreach (var c in new Control[] { _cboRawLang, _cboDpi, _numGap, _cboBaud, _chkRotate180 })
            c.Enabled = serial;
        _numGap.Enabled = serial && _cboRawLang.SelectedIndex != 1;
    }

    private void LoadFonts(string selected)
    {
        _cboFont.Items.Clear();
        foreach (var name in FontManager.AllFamilyNames())
            _cboFont.Items.Add(name);
        if (!_cboFont.Items.Contains(selected))
            _cboFont.Items.Insert(0, selected); // keep the saved choice visible even if missing on this PC
        _cboFont.SelectedItem = selected;
    }

    private void AddFontFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "เลือกไฟล์ฟอนต์",
            Filter = "Font (*.ttf;*.otf)|*.ttf;*.otf",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        var last = "";
        foreach (var file in dlg.FileNames)
        {
            try
            {
                last = FontManager.ImportFile(file);
                LogLine($"เพิ่มฟอนต์ {last} ({Path.GetFileName(file)})");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{Path.GetFileName(file)}: {ex.Message}", "เพิ่มฟอนต์ไม่สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        if (last.Length > 0)
            LoadFonts(last); // SelectedIndexChanged → live preview
    }

    private void ReadControlsIntoSettings()
    {
        var s = _settings;
        s.CredentialPath = _txtCred.Text.Trim();
        s.ProjectId = _txtProject.Text.Trim();
        s.DatabaseId = _txtDatabase.Text.Trim();
        s.Collection = _txtCollection.Text.Trim();
        s.StationId = _cboStationId.Text.Trim();
        s.StationField = _txtStationField.Text.Trim();
        s.AcceptUnassigned = _chkAcceptUnassigned.Checked;
        s.PendingMode = _cboPending.SelectedIndex == 1 ? PendingMode.PrintedFalse : PendingMode.StatusPending;
        s.ConnectionMode = _cboConnMode.SelectedIndex == 1 ? ConnectionMode.Polling : ConnectionMode.Realtime;
        s.PollIntervalSeconds = (int)_numPoll.Value;
        s.WriteBackStatus = _chkWriteBack.Checked;
        s.AutoConnectOnStart = _chkAutoConnect.Checked;
        s.AutoPrint = _chkAutoPrint.Checked;

        s.FieldTicketCode = _txtFTicket.Text.Trim();
        s.FieldTitle = _txtFTitle.Text.Trim();
        s.FieldFullName = _txtFFullName.Text.Trim();
        s.FieldPosition = _txtFPosition.Text.Trim();
        s.FieldOrganization = _txtFOrganization.Text.Trim();
        s.FieldCategory = _txtFCategory.Text.Trim();

        if (_cboPrinter.SelectedItem is PrinterChoice printer)
        {
            s.PrinterConnection = printer.Kind;
            if (printer.Kind == PrinterConnection.SerialPort)
                s.SerialPortName = printer.Name;
            else
                s.PrinterName = printer.Name;
        }
        s.RawLanguage = _cboRawLang.SelectedIndex == 1 ? RawLanguage.EscPos : RawLanguage.Tspl;
        s.PrinterDpi = int.TryParse(_cboDpi.SelectedItem as string, out var dpi) ? dpi : 203;
        s.LabelGapMm = (double)_numGap.Value;
        s.SerialBaudRate = int.TryParse(_cboBaud.SelectedItem as string, out var baud) ? baud : 9600;
        s.Rotate180 = _chkRotate180.Checked;
        s.StickerSizeName = (_cboSize.SelectedItem as StickerSize)?.Name ?? StickerSize.CustomName;
        s.StickerWidthMm = (double)_numW.Value;
        s.StickerHeightMm = (double)_numH.Value;
        s.Landscape = _chkLandscape.Checked;
        s.PaddingMm = (double)_numPad.Value;
        s.OffsetXMm = (double)_numOffX.Value;
        s.OffsetYMm = (double)_numOffY.Value;
        s.Copies = (int)_numCopies.Value;

        s.FontFamily = _cboFont.SelectedItem as string ?? FontManager.DefaultFamily;
        s.ShowTicketCode = _chkShowTicket.Checked;
        s.ShowTitle = _chkShowTitle.Checked;
        s.ShowPosition = _chkShowPosition.Checked;
        s.ShowOrganization = _chkShowOrganization.Checked;
        s.ShowCategory = _chkShowCategory.Checked;
        s.CategoryInverted = _chkInverted.Checked;
        s.BorderFrame = _chkBorder.Checked;
    }

    private static decimal Clamp(NumericUpDown n, double v) => Math.Clamp((decimal)v, n.Minimum, n.Maximum);

    private void UpdateSizeEditable()
    {
        var custom = (_cboSize.SelectedItem as StickerSize)?.Name == StickerSize.CustomName;
        _numW.Enabled = custom;
        _numH.Enabled = custom;
    }

    private void ShowAccountInfo()
    {
        try
        {
            if (!File.Exists(_txtCred.Text))
            {
                _lblAccount.Text = "ยังไม่ได้เลือกไฟล์ Service account";
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_txtCred.Text));
            var email = doc.RootElement.TryGetProperty("client_email", out var e) ? e.GetString() : "?";
            var project = doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : "?";
            _lblAccount.Text = $"{email}  (project: {project})";
        }
        catch (Exception ex)
        {
            _lblAccount.Text = $"อ่านไฟล์ไม่ได้: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ events

    private void WireEvents()
    {
        _btnConnect.Click += (_, _) =>
        {
            if (_listenCts is null) Connect();
            else Disconnect();
        };

        _btnTest.Click += (_, _) =>
        {
            ReadControlsIntoSettings();
            _processor.Enqueue(PrintJob.Sample());
        };

        _chkAutoPrint.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.AutoPrint = _chkAutoPrint.Checked;
            if (_settings.AutoPrint)
                FlushWaiting();
        };

        _btnPrintSelected.Click += (_, _) => PrintSelected();
        _grid.SelectionChanged += (_, _) => RefreshPreview();

        _btnBrowse.Click += (_, _) => BrowseCredential();
        _txtCred.TextChanged += (_, _) => ShowAccountInfo();
        _btnRefreshPrinters.Click += (_, _) => LoadPrinters();
        _btnAddFont.Click += (_, _) => AddFontFile();

        _cboSize.SelectedIndexChanged += (_, _) =>
        {
            if (_cboSize.SelectedItem is StickerSize { WidthMm: > 0 } p)
            {
                _numW.Value = Clamp(_numW, p.WidthMm);
                _numH.Value = Clamp(_numH, p.HeightMm);
            }
            UpdateSizeEditable();
        };

        _btnSave.Click += (_, _) => SaveSettings();

        // Any visual setting → live preview.
        Control[] live =
        [
            _cboPrinter, _cboSize, _numW, _numH, _chkLandscape, _numPad, _numOffX, _numOffY, _numCopies,
            _cboRawLang, _cboDpi, _numGap, _cboBaud, _chkRotate180,
            _cboFont, _chkShowTicket, _chkShowTitle, _chkShowPosition, _chkShowOrganization, _chkShowCategory,
            _chkInverted, _chkBorder,
        ];
        foreach (var c in live)
        {
            switch (c)
            {
                case ComboBox cb: cb.SelectedIndexChanged += (_, _) => OnLiveSettingChanged(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => OnLiveSettingChanged(); break;
                case CheckBox ck: ck.CheckedChanged += (_, _) => OnLiveSettingChanged(); break;
            }
        }
        _preview.Resize += (_, _) => RefreshPreview();
    }

    private void OnLiveSettingChanged()
    {
        if (_loading) return;
        UpdatePrinterControls();
        ReadControlsIntoSettings();
        RefreshPreview();
    }

    private void BrowseCredential()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "เลือกไฟล์ Service account (.json)",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        _txtCred.Text = dlg.FileName;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            if (doc.RootElement.TryGetProperty("project_id", out var p) && p.GetString() is { Length: > 0 } project)
                _txtProject.Text = project;
        }
        catch
        {
            // ShowAccountInfo already reports unreadable files.
        }
    }

    private void SaveSettings()
    {
        ReadControlsIntoSettings();
        try
        {
            _settings.Save();
            LogLine("บันทึกการตั้งค่าแล้ว");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "บันทึกไม่สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_connectedWith is not null && ConnectionChanged(_connectedWith, _settings))
        {
            LogLine("ค่าการเชื่อมต่อเปลี่ยน — กำลังเชื่อมต่อใหม่");
            Disconnect();
            Connect();
        }
        RefreshPreview();
    }

    private static bool ConnectionChanged(AppSettings a, AppSettings b) =>
        a.CredentialPath != b.CredentialPath || a.ProjectId != b.ProjectId || a.DatabaseId != b.DatabaseId ||
        a.Collection != b.Collection || a.StationId != b.StationId || a.StationField != b.StationField || a.AcceptUnassigned != b.AcceptUnassigned ||
        a.PendingMode != b.PendingMode || a.ConnectionMode != b.ConnectionMode || a.PollIntervalSeconds != b.PollIntervalSeconds ||
        a.FieldTicketCode != b.FieldTicketCode || a.FieldTitle != b.FieldTitle || a.FieldFullName != b.FieldFullName ||
        a.FieldPosition != b.FieldPosition || a.FieldOrganization != b.FieldOrganization || a.FieldCategory != b.FieldCategory;

    // ------------------------------------------------------------------ connection

    private void Connect()
    {
        ReadControlsIntoSettings();
        if (!File.Exists(_settings.CredentialPath))
        {
            MessageBox.Show(this, "กรุณาเลือกไฟล์ Service account (.json) ในแท็บ ตั้งค่า", "ยังไม่ได้ตั้งค่า",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _connectedWith = _settings.Clone();
        var source = new FirestoreJobSource(_connectedWith);
        source.JobArrived += job => Ui(() => OnJobArrived(job));
        source.Log += LogLine;
        source.ConnectionChanged += (ok, msg) => Ui(() => SetConnected(ok, msg));

        _source = source;
        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        _ = Task.Run(() => source.RunAsync(token));

        _btnConnect.Text = "ยกเลิกการเชื่อมต่อ";
        SetConnected(false, "กำลังเชื่อมต่อ...");
        UpdateTitle();
        LogLine($"กำลังเชื่อมต่อ {source.Description}");
    }

    private void Disconnect()
    {
        _listenCts?.Cancel();
        _listenCts = null;
        _source = null;
        _connectedWith = null;
        _btnConnect.Text = "เชื่อมต่อ";
        SetConnected(false, "ไม่ได้เชื่อมต่อ");
    }

    private void SetConnected(bool ok, string message)
    {
        _lblStatus.Text = (ok ? "● " : "○ ") + message;
        _lblStatus.ForeColor = ok ? Color.ForestGreen : (_listenCts is null ? Color.DimGray : Color.DarkOrange);
        if (!ok && _listenCts is not null)
            LogLine(message);
    }

    // ------------------------------------------------------------------ jobs

    private void OnJobArrived(PrintJob job)
    {
        LogLine($"รับงานใหม่: {job.DisplayName} [{job.TicketCode}] ({job.DocumentId})");
        if (_settings.AutoPrint)
        {
            if (!_processor.Enqueue(job))
                LogLine($"ข้าม {job.DocumentId}: กำลังพิมพ์อยู่หรือพิมพ์ไปแล้ว");
            return;
        }

        if (_waiting.ContainsKey(job.DocumentId))
            return;
        job.State = JobState.Waiting;
        job.Message = "รอสั่งพิมพ์ (ปิดพิมพ์อัตโนมัติอยู่)";
        _waiting[job.DocumentId] = job;
        UpsertRow(job);
    }

    private void FlushWaiting()
    {
        foreach (var job in _waiting.Values.ToList())
        {
            _waiting.Remove(job.DocumentId);
            _processor.Enqueue(job);
        }
    }

    private void PrintSelected()
    {
        ReadControlsIntoSettings();
        var jobs = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PrintJob>().Reverse().ToList();
        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "กรุณาเลือกรายการในตาราง", "Sticker Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var job in jobs)
        {
            switch (job.State)
            {
                case JobState.Waiting:
                    _waiting.Remove(job.DocumentId);
                    _processor.Enqueue(job);
                    break;
                case JobState.Printed or JobState.Error or JobState.Skipped:
                    _processor.Enqueue(job.AsReprint());
                    break;
                // Queued / Printing: already on its way.
            }
        }
    }

    private void OnJobChanged(PrintJob job)
    {
        if (job.State == JobState.Printed) _printedCount++;
        if (job.State == JobState.Error) _errorCount++;
        UpsertRow(job);
        UpdateCounts();
    }

    private void UpsertRow(PrintJob job)
    {
        if (!_rows.TryGetValue(job, out var row))
        {
            _grid.Rows.Insert(0, 1);
            row = _grid.Rows[0];
            row.Tag = job;
            _rows[job] = row;

            while (_grid.Rows.Count > MaxRows)
            {
                var last = _grid.Rows[^1];
                if (last.Tag is PrintJob old) _rows.Remove(old);
                _grid.Rows.Remove(last);
            }
        }

        row.SetValues(
            job.ReceivedAt.ToString("HH:mm:ss"),
            job.DocumentId + (job.IsReprint && !job.IsTest ? " (ซ้ำ)" : ""),
            job.TicketCode,
            job.DisplayName,
            job.Position,
            job.Organization,
            job.Category,
            StateText(job.State),
            job.Message);

        row.DefaultCellStyle.BackColor = job.State switch
        {
            JobState.Printed => Color.FromArgb(220, 245, 220),
            JobState.Error => Color.FromArgb(255, 215, 215),
            JobState.Printing => Color.FromArgb(255, 245, 200),
            JobState.Waiting => Color.FromArgb(220, 235, 255),
            JobState.Skipped => Color.FromArgb(235, 235, 235),
            _ => Color.White,
        };
    }

    private static string StateText(JobState s) => s switch
    {
        JobState.Waiting => "รอสั่งพิมพ์",
        JobState.Queued => "อยู่ในคิว",
        JobState.Printing => "กำลังพิมพ์",
        JobState.Printed => "พิมพ์แล้ว",
        JobState.Skipped => "ข้าม",
        JobState.Error => "ผิดพลาด",
        _ => s.ToString(),
    };

    private void UpdateTitle() =>
        Text = string.IsNullOrWhiteSpace(_settings.StationId)
            ? "Sticker Print — Firebase Auto Print"
            : $"Sticker Print — Printer Station #{_settings.StationId}";

    private void UpdateCounts() =>
        _lblCounts.Text = $"พิมพ์แล้ว: {_printedCount}   ผิดพลาด: {_errorCount}   รอ: {_waiting.Count}";

    // ------------------------------------------------------------------ preview

    private void RefreshPreview()
    {
        var job = _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is PrintJob selected ? selected : PrintJob.Sample();
        var size = StickerRenderer.StickerSizeMm(_settings);
        _lblPreviewInfo.Text = $"{size.Width:0.#} × {size.Height:0.#} mm  →  {StickerPrinter.Describe(_settings)}"
                               + (job.IsTest ? "\n(ข้อมูลตัวอย่าง)" : $"\n{job.DocumentId}");

        var box = _preview.ClientSize;
        if (box.Width < 20 || box.Height < 20 || size.Width <= 0 || size.Height <= 0)
            return;

        var pxPerMm = Math.Min((box.Width - 20) / size.Width, (box.Height - 20) / size.Height);
        try
        {
            var bmp = StickerRenderer.RenderPreview(job, _settings, Math.Max(1f, pxPerMm));
            using (var g = Graphics.FromImage(bmp))
                g.DrawRectangle(Pens.Gray, 0, 0, bmp.Width - 1, bmp.Height - 1);
            var old = _preview.Image;
            _preview.Image = bmp;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            _lblPreviewInfo.Text = $"แสดงตัวอย่างไม่ได้: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ helpers

    private void LogLine(string message)
    {
        var line = AppLog.Format(message);
        AppLog.Write(line);
        Ui(() =>
        {
            if (_txtLog.TextLength > 200_000)
                _txtLog.Text = _txtLog.Text[^100_000..];
            _txtLog.AppendText(line + Environment.NewLine);
        });
    }

    private void Ui(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return; // events before the window exists are still written to the log file
        if (!InvokeRequired)
        {
            action();
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Form closed between the check and the call.
        }
    }
}
