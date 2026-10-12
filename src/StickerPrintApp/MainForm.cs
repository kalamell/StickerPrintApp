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
    private int _roundRobin;

    // Top bar
    private readonly Label _lblStatus = new() { AutoSize = true, Font = new Font("Tahoma", 10f, FontStyle.Bold), Padding = new Padding(0, 6, 12, 0) };
    private readonly Button _btnConnect = new() { Text = "เชื่อมต่อ", AutoSize = true };
    private readonly CheckBox _chkAutoPrint = new() { Text = "พิมพ์อัตโนมัติ (Auto print)", AutoSize = true, Padding = new Padding(12, 4, 0, 0) };
    private readonly Button _btnSave = new() { Text = "บันทึกการตั้งค่า", AutoSize = true };
    private readonly Label _lblCounts = new() { AutoSize = true, Padding = new Padding(12, 6, 0, 0) };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private TabPage _jobsPage = null!;

    // Jobs tab
    private readonly DataGridView _grid = new();
    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };

    // Preview panel
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.FromArgb(230, 230, 230) };
    private readonly Label _lblPreviewInfo = new() { Dock = DockStyle.Top, Height = 44, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _btnPrintSelected = new() { Text = "พิมพ์ / พิมพ์ซ้ำ รายการที่เลือก", Dock = DockStyle.Bottom, Height = 36 };
    private readonly Button _btnTest = new() { Text = "พิมพ์ทดสอบ (เครื่องที่กำลังแก้ไข)", Dock = DockStyle.Bottom, Height = 36 };
    private List<ElementHit> _previewHits = [];
    private float _previewPxPerMm = 1;
    private int _previewProfileId;
    private (string Key, PointF StartMm, double X, double Y)? _drag;

    // Printers tab — selection
    private readonly ComboBox _cboProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _chkEnabled = new() { Text = "เปิดใช้เครื่องนี้", AutoSize = true, Padding = new Padding(12, 4, 0, 0) };
    private readonly TextBox _txtLabel = new() { Width = 180, PlaceholderText = "ชื่อเรียก เช่น โต๊ะ A" };
    private PrinterProfile _profile = null!;

    // Printers tab — output
    private readonly ComboBox _cboPrinter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button _btnRefreshPrinters = new() { Text = "รีเฟรช", AutoSize = true };
    private readonly ComboBox _cboRawLang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _cboDpi = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly NumericUpDown _numGap = Num(0, 20, 0.5m);
    private readonly ComboBox _cboBaud = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly CheckBox _chkRotate180 = new() { Text = "กลับหัว 180°", AutoSize = true };

    // Printers tab — paper
    private readonly ComboBox _cboSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly NumericUpDown _numW = Num(10, 300, 1);
    private readonly NumericUpDown _numH = Num(10, 300, 1);
    private readonly CheckBox _chkLandscape = new() { Text = "แนวนอน (หมุน 90°)", AutoSize = true };
    private readonly NumericUpDown _numOffX = Num(-20, 20, 0.5m);
    private readonly NumericUpDown _numOffY = Num(-20, 20, 0.5m);
    private readonly NumericUpDown _numCopies = new() { Minimum = 1, Maximum = 20, Width = 70 };

    // Printers tab — layout
    private readonly TextRow _rowName = new("ชื่อ-นามสกุล");
    private readonly TextRow _rowPosition = new("ตำแหน่ง");
    private readonly TextRow _rowOrganization = new("บริษัท/องค์กร");
    private readonly TextRow _rowTicket = new("Ticket code");
    private readonly TextRow _rowCategory = new("Category");
    private readonly CheckBox _chkQr = new() { Text = "", AutoSize = true, Anchor = AnchorStyles.None };
    private readonly NumericUpDown _numQrX = Num(-50, 500, 0.5m);
    private readonly NumericUpDown _numQrY = Num(-50, 500, 0.5m);
    private readonly NumericUpDown _numQrSize = Num(3, 200, 0.5m);
    private readonly CheckBox _chkTwoLines = new() { Text = "ชื่อ 2 บรรทัด (คำนำหน้า+ชื่อ / นามสกุล)", AutoSize = true };
    private readonly CheckBox _chkShowTitle = new() { Text = "แสดงคำนำหน้า (Title)", AutoSize = true };
    private readonly NumericUpDown _numNameSpacing = new() { Minimum = 0.8m, Maximum = 2.5m, Increment = 0.05m, DecimalPlaces = 2, Width = 70 };
    private readonly CheckBox _chkBorder = new() { Text = "พิมพ์กรอบรอบสติกเกอร์", AutoSize = true };
    private readonly Button _btnResetLayout = new() { Text = "รีเซ็ตเลย์เอาต์ตามขนาดกระดาษ", AutoSize = true };
    private readonly ComboBox _cboCopyTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Button _btnCopyLayout = new() { Text = "คัดลอกกระดาษ+เลย์เอาต์ไป", AutoSize = true };

    // Firebase tab
    private readonly TextBox _txtCred = new() { Width = 360 };
    private readonly Button _btnBrowse = new() { Text = "เลือกไฟล์...", AutoSize = true };
    private readonly Label _lblAccount = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox _txtProject = new() { Width = 240 };
    private readonly TextBox _txtDatabase = new() { Width = 240 };
    private readonly TextBox _txtCollection = new() { Width = 240 };
    private readonly TextBox _txtPrinterIdField = new() { Width = 240 };
    private readonly ComboBox _cboUnassigned = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _cboPending = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _cboConnMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _numPoll = new() { Minimum = 1, Maximum = 60, Width = 70 };
    private readonly CheckBox _chkWriteBack = new() { Text = "หลังพิมพ์ mark printed = true / status กลับไปที่ Firestore", AutoSize = true };
    private readonly CheckBox _chkAutoConnect = new() { Text = "เชื่อมต่ออัตโนมัติเมื่อเปิดโปรแกรม", AutoSize = true };
    private readonly TextBox _txtFTicket = new() { Width = 200 };
    private readonly TextBox _txtFTitle = new() { Width = 200 };
    private readonly TextBox _txtFFirst = new() { Width = 200 };
    private readonly TextBox _txtFLast = new() { Width = 200 };
    private readonly TextBox _txtFFullName = new() { Width = 200 };
    private readonly TextBox _txtFPosition = new() { Width = 200 };
    private readonly TextBox _txtFOrganization = new() { Width = 200 };
    private readonly TextBox _txtFCategory = new() { Width = 200 };
    private readonly TextBox _txtFQr = new() { Width = 200 };
    private readonly ComboBox _cboFont = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button _btnAddFont = new() { Text = "เพิ่มไฟล์ฟอนต์...", AutoSize = true };

    /// <summary>Editor controls for one text element (one row of the layout table).</summary>
    private sealed class TextRow(string caption)
    {
        public readonly string Caption = caption;
        public readonly CheckBox Visible = new() { AutoSize = true, Anchor = AnchorStyles.None };
        public readonly NumericUpDown X = Num(-50, 500, 0.5m);
        public readonly NumericUpDown Y = Num(-50, 500, 0.5m);
        public readonly NumericUpDown Size = Num(3, 96, 0.5m);
        public readonly NumericUpDown MaxW = Num(0, 500, 1);
        public readonly CheckBox Bold = new() { AutoSize = true, Anchor = AnchorStyles.None };
        public readonly CheckBox Upper = new() { AutoSize = true, Anchor = AnchorStyles.None };
        public readonly ComboBox Align = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Items = { "ซ้าย", "กลาง", "ขวา" } };

        public IEnumerable<Control> Controls => [Visible, X, Y, Size, MaxW, Bold, Upper, Align];

        public void Load(TextElement e)
        {
            Visible.Checked = e.Visible;
            X.Value = Clamp(X, e.X);
            Y.Value = Clamp(Y, e.Y);
            Size.Value = Clamp(Size, e.FontSizePt);
            MaxW.Value = Clamp(MaxW, e.MaxWidthMm);
            Bold.Checked = e.Bold;
            Upper.Checked = e.Uppercase;
            Align.SelectedIndex = (int)e.Align;
        }

        public void Save(TextElement e)
        {
            e.Visible = Visible.Checked;
            e.X = (double)X.Value;
            e.Y = (double)Y.Value;
            e.FontSizePt = (double)Size.Value;
            e.MaxWidthMm = (double)MaxW.Value;
            e.Bold = Bold.Checked;
            e.Uppercase = Upper.Checked;
            e.Align = (TextAlign)Math.Max(0, Align.SelectedIndex);
        }
    }

    public MainForm()
    {
        Size = new Size(1360, 860);
        MinimumSize = new Size(1100, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9f);

        _processor = new JobProcessor(() => _settings, () => _source, _appCts.Token);
        _processor.JobChanged += job => Ui(() => OnJobChanged(job));
        _processor.Log += LogLine;

        _profile = _settings.Printers[0];
        BuildLayout();
        LoadSettingsIntoControls();
        WireEvents();
        SetConnected(false, "ไม่ได้เชื่อมต่อ");
        UpdateTitle();
        UpdateCounts();
        RefreshPreview();
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
        top.Controls.AddRange([_lblStatus, _btnConnect, _chkAutoPrint, _btnSave, _lblCounts]);

        var previewPanel = new Panel { Dock = DockStyle.Right, Width = 440, Padding = new Padding(8) };
        var previewTitle = new Label
        {
            Text = "ตัวอย่างสติกเกอร์ (ลากข้อความ/QR เพื่อย้ายตำแหน่ง)",
            Dock = DockStyle.Top, Height = 24, Font = new Font("Tahoma", 9.5f, FontStyle.Bold),
        };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(_lblPreviewInfo);
        previewPanel.Controls.Add(previewTitle);
        previewPanel.Controls.Add(_btnTest);
        previewPanel.Controls.Add(_btnPrintSelected);

        _jobsPage = BuildJobsTab();
        _tabs.TabPages.Add(_jobsPage);
        _tabs.TabPages.Add(BuildPrintersTab());
        _tabs.TabPages.Add(BuildFirebaseTab());

        Controls.Add(_tabs);
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
        _grid.Columns.Add("printer", "เครื่อง");
        _grid.Columns.Add("ticket", "Ticket code");
        _grid.Columns.Add("name", "ชื่อ-นามสกุล");
        _grid.Columns.Add("position", "ตำแหน่ง");
        _grid.Columns.Add("org", "องค์กร");
        _grid.Columns.Add("state", "สถานะ");
        _grid.Columns.Add("msg", "รายละเอียด");
        _grid.Columns["time"]!.FillWeight = 50;
        _grid.Columns["printer"]!.FillWeight = 55;
        _grid.Columns["state"]!.FillWeight = 55;
        _grid.Columns["msg"]!.FillWeight = 150;

        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 160, Padding = new Padding(0, 4, 0, 0) };
        logPanel.Controls.Add(_txtLog);

        var page = new TabPage("งานพิมพ์");
        page.Controls.Add(_grid);
        page.Controls.Add(new Splitter { Dock = DockStyle.Bottom });
        page.Controls.Add(logPanel);
        return page;
    }

    private TabPage BuildPrintersTab()
    {
        var flow = ScrollFlow();

        var header = Row(new Label { Text = "แก้ไขเครื่อง:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _cboProfile, _chkEnabled, new Label { Text = "ชื่อเรียก", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, _txtLabel);
        header.Margin = new Padding(4, 4, 4, 8);
        flow.Controls.Add(header);
        flow.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(4, 0, 4, 8),
            Text = "job ที่มี printerId = 1..5 จะพิมพ์ที่เครื่องหมายเลขนั้น (ต้องเปิดใช้); job ที่ไม่มี printerId ตั้งค่าได้ที่แท็บ Firebase",
        });

        flow.Controls.Add(Group("เครื่องพิมพ์",
            ("เครื่องพิมพ์", Row(_cboPrinter, _btnRefreshPrinters)),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Bluetooth ที่ไม่มีไดรเวอร์: จับคู่ใน Windows แล้วเลือก \"Bluetooth / COM\"" }),
            ("ภาษาเครื่องพิมพ์ (COM)", _cboRawLang),
            ("dpi / gap (mm) / baud", Row(_cboDpi, _numGap, _cboBaud, _chkRotate180))));

        flow.Controls.Add(Group("กระดาษ",
            ("ขนาดสติกเกอร์", _cboSize),
            ("กว้าง × สูง (mm)", Row(_numW, Lbl("×"), _numH, _chkLandscape)),
            ("ชดเชยตำแหน่ง X / Y (mm)", Row(_numOffX, _numOffY)),
            ("จำนวนต่อคน", _numCopies),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "เปลี่ยนขนาดกระดาษแล้วตำแหน่งข้อความจะย่อ/ขยายตามสัดส่วนให้อัตโนมัติ" })));

        flow.Controls.Add(BuildLayoutGroup());

        var page = new TabPage("เครื่องพิมพ์ 1–5");
        page.Controls.Add(flow);
        return page;
    }

    private GroupBox BuildLayoutGroup()
    {
        var table = new TableLayoutPanel { ColumnCount = 9, AutoSize = true, Location = new Point(8, 22), Padding = new Padding(4) };
        string[] headers = ["แสดง", "ส่วน", "X (mm)", "Y (mm)", "ขนาด (pt)", "กว้างสุด (mm)", "ตัวหนา", "ตัวใหญ่", "จัดแนว"];
        foreach (var h in headers)
            table.Controls.Add(new Label { Text = h, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.None });

        foreach (var row in TextRows())
        {
            table.Controls.Add(row.Visible);
            table.Controls.Add(new Label { Text = row.Caption, AutoSize = true, Anchor = AnchorStyles.Left });
            foreach (var c in row.Controls.Skip(1))
                table.Controls.Add(c);
        }

        table.Controls.Add(_chkQr);
        table.Controls.Add(new Label { Text = "QR code", AutoSize = true, Anchor = AnchorStyles.Left });
        table.Controls.Add(_numQrX);
        table.Controls.Add(_numQrY);
        table.Controls.Add(_numQrSize);
        table.Controls.Add(new Label { Text = "← ขนาด QR (mm)", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left });
        table.SetColumnSpan(table.Controls[^1], 4);

        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.Add(Row(_chkTwoLines, _chkShowTitle));
        options.Controls.Add(Row(Lbl("ระยะห่างบรรทัดชื่อ (เท่าของขนาดตัวอักษร)"), _numNameSpacing, _chkBorder));
        options.Controls.Add(Row(_btnResetLayout, Lbl("   "), _btnCopyLayout, _cboCopyTarget));
        options.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Color.DimGray,
            Text = "X/Y วัดจากมุมซ้ายบนของสติกเกอร์ (Y = ขอบบนของตัวอักษร) · กว้างสุด 0 = ถึงขอบกระดาษ · ข้อความยาวเกินจะย่อให้พอดีเอง",
        });

        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Location = new Point(8, 22) };
        table.Location = Point.Empty;
        stack.Controls.Add(table);
        stack.Controls.Add(options);

        var box = new GroupBox { Text = "ตำแหน่งข้อความ (เลย์เอาต์)", AutoSize = true, MinimumSize = new Size(760, 0), Padding = new Padding(8) };
        box.Controls.Add(stack);
        return box;
    }

    private TabPage BuildFirebaseTab()
    {
        var flow = ScrollFlow();
        flow.Controls.Add(Group("Firebase / Firestore",
            ("Service account (.json)", Row(_txtCred, _btnBrowse)),
            ("", _lblAccount),
            ("Project ID", _txtProject),
            ("Database", _txtDatabase),
            ("Collection", _txtCollection),
            ("ฟิลด์เลขเครื่องพิมพ์", _txtPrinterIdField),
            ("job ที่ไม่มี printerId", _cboUnassigned),
            ("งานที่ยังไม่พิมพ์คือ", _cboPending),
            ("การเชื่อมต่อ", _cboConnMode),
            ("Polling ทุก (วินาที)", _numPoll),
            ("", _chkWriteBack),
            ("", _chkAutoConnect)));

        flow.Controls.Add(Group("ชื่อฟิลด์ในเอกสาร (Field mapping)",
            ("Ticket code", _txtFTicket),
            ("Title (คำนำหน้า)", _txtFTitle),
            ("ชื่อ / นามสกุล", Row(_txtFFirst, _txtFLast)),
            ("Full name", _txtFFullName),
            ("Position", _txtFPosition),
            ("Organization", _txtFOrganization),
            ("Category", _txtFCategory),
            ("ข้อมูลใน QR code", _txtFQr),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "ถ้าไม่มี first_name/last_name จะแยกจาก fullname ที่ช่องว่างแรก" })));

        flow.Controls.Add(Group("ฟอนต์",
            ("ฟอนต์", Row(_cboFont, _btnAddFont)),
            ("", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "มี IBM Plex Sans Thai ในตัวแล้ว; ฟอนต์อื่นเลือกไฟล์ .ttf/.otf หรือวางในโฟลเดอร์ fonts ข้าง .exe" })));

        var page = new TabPage("Firebase");
        page.Controls.Add(flow);
        return page;
    }

    private IEnumerable<TextRow> TextRows() => [_rowName, _rowPosition, _rowOrganization, _rowTicket, _rowCategory];

    private static FlowLayoutPanel ScrollFlow() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(8),
    };

    private static GroupBox Group(string title, params (string Label, Control Control)[] rows)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = new Point(8, 22), Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, control) in rows)
        {
            table.RowCount++;
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
            table.Controls.Add(control);
        }

        var box = new GroupBox { Text = title, AutoSize = true, MinimumSize = new Size(760, 0), Padding = new Padding(8) };
        box.Controls.Add(table);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        p.Controls.AddRange(controls);
        return p;
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    private static NumericUpDown Num(decimal min, decimal max, decimal step) =>
        new() { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = 1, Width = 70 };

    private static decimal Clamp(NumericUpDown n, double v) => Math.Clamp((decimal)v, n.Minimum, n.Maximum);

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
            _txtPrinterIdField.Text = s.PrinterIdField;
            _cboPending.Items.AddRange(["status == \"pending\"", "printed == false"]);
            _cboPending.SelectedIndex = s.PendingMode == PendingMode.PrintedFalse ? 1 : 0;
            _cboConnMode.Items.AddRange(["Realtime (แนะนำ — ทันที)", "Polling (สำหรับเครือข่ายที่บล็อก gRPC)"]);
            _cboConnMode.SelectedIndex = s.ConnectionMode == ConnectionMode.Polling ? 1 : 0;
            _numPoll.Value = Math.Clamp(s.PollIntervalSeconds, 1, 60);
            _chkWriteBack.Checked = s.WriteBackStatus;
            _chkAutoConnect.Checked = s.AutoConnectOnStart;
            _chkAutoPrint.Checked = s.AutoPrint;

            _txtFTicket.Text = s.FieldTicketCode;
            _txtFTitle.Text = s.FieldTitle;
            _txtFFirst.Text = s.FieldFirstName;
            _txtFLast.Text = s.FieldLastName;
            _txtFFullName.Text = s.FieldFullName;
            _txtFPosition.Text = s.FieldPosition;
            _txtFOrganization.Text = s.FieldOrganization;
            _txtFCategory.Text = s.FieldCategory;
            _txtFQr.Text = s.FieldQr;
            LoadFonts(s.FontFamily);

            _cboRawLang.Items.AddRange(["TSPL — เครื่องพิมพ์ฉลาก (TSC, Xprinter, Gprinter, HPRT)", "ESC/POS — เครื่องพิมพ์ใบเสร็จ/พกพา"]);
            _cboDpi.Items.AddRange(["203", "300"]);
            _cboBaud.Items.AddRange(["9600", "19200", "38400", "57600", "115200"]);
            foreach (var p in StickerSize.Presets)
                _cboSize.Items.Add(p);

            RefreshProfileLists();
            _cboProfile.SelectedIndex = 0;
            LoadProfileControls(_profile);
            ShowAccountInfo();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Refreshes the printer-slot names in the selector, copy target and "no printerId" lists.</summary>
    private void RefreshProfileLists()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var selected = _cboProfile.SelectedIndex;
            _cboProfile.Items.Clear();
            foreach (var p in _settings.Printers)
                _cboProfile.Items.Add($"{p.DisplayName}{(p.Enabled ? "" : "  (ปิด)")}");
            if (selected >= 0)
                _cboProfile.SelectedIndex = selected;

            var target = _cboCopyTarget.SelectedIndex;
            _cboCopyTarget.Items.Clear();
            foreach (var p in _settings.Printers)
                _cboCopyTarget.Items.Add($"Printer {p.Id}");
            _cboCopyTarget.SelectedIndex = target >= 0 ? target : 1;

            _cboUnassigned.Items.Clear();
            _cboUnassigned.Items.Add("สลับกันทุกเครื่องที่เปิดใช้ (1 → 2 → 3 …)");
            _cboUnassigned.Items.Add("ไม่พิมพ์");
            foreach (var p in _settings.Printers)
                _cboUnassigned.Items.Add($"พิมพ์ที่ {p.DisplayName} เสมอ");
            _cboUnassigned.SelectedIndex = _settings.UnassignedPrinterId switch
            {
                AppSettings.RoundRobin => 0,
                0 => 1,
                var id => 1 + Math.Clamp(id, 1, PrinterProfile.MaxProfiles),
            };
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void LoadProfileControls(PrinterProfile p)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _chkEnabled.Checked = p.Enabled;
            _txtLabel.Text = p.Label;

            _cboRawLang.SelectedIndex = p.RawLanguage == RawLanguage.EscPos ? 1 : 0;
            _cboDpi.SelectedItem = p.PrinterDpi >= 300 ? "300" : "203";
            _numGap.Value = Clamp(_numGap, p.LabelGapMm);
            _cboBaud.SelectedItem = p.SerialBaudRate.ToString();
            if (_cboBaud.SelectedIndex < 0) _cboBaud.SelectedIndex = 0;
            _chkRotate180.Checked = p.Rotate180;
            LoadPrinters();

            _cboSize.SelectedItem = StickerSize.Presets.FirstOrDefault(x => x.Name == p.StickerSizeName)
                                    ?? StickerSize.Presets.First(x => x.Name == StickerSize.CustomName);
            _numW.Value = Clamp(_numW, p.StickerWidthMm);
            _numH.Value = Clamp(_numH, p.StickerHeightMm);
            UpdateSizeEditable();
            _chkLandscape.Checked = p.Landscape;
            _numOffX.Value = Clamp(_numOffX, p.OffsetXMm);
            _numOffY.Value = Clamp(_numOffY, p.OffsetYMm);
            _numCopies.Value = Clamp(_numCopies, p.Copies);

            LoadLayoutControls(p.Layout);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void LoadLayoutControls(StickerLayout l)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _rowName.Load(l.Name);
            _rowPosition.Load(l.Position);
            _rowOrganization.Load(l.Organization);
            _rowTicket.Load(l.TicketCode);
            _rowCategory.Load(l.Category);
            _chkQr.Checked = l.Qr.Visible;
            _numQrX.Value = Clamp(_numQrX, l.Qr.X);
            _numQrY.Value = Clamp(_numQrY, l.Qr.Y);
            _numQrSize.Value = Clamp(_numQrSize, l.Qr.SizeMm);
            _chkTwoLines.Checked = l.NameTwoLines;
            _chkShowTitle.Checked = l.ShowTitle;
            _numNameSpacing.Value = Clamp(_numNameSpacing, l.Name.LineSpacing);
            _chkBorder.Checked = l.Border;
        }
        finally
        {
            _loading = wasLoading;
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
                foreach (var name in StickerPrinter.InstalledPrinters())
                    _cboPrinter.Items.Add(new PrinterChoice(PrinterConnection.Windows, name, name));
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

            var p = _profile;
            var wanted = p.Connection == PrinterConnection.SerialPort
                ? new PrinterChoice(PrinterConnection.SerialPort, p.SerialPortName, $"Bluetooth / COM: {p.SerialPortName} (ไม่พบตอนนี้)")
                : new PrinterChoice(PrinterConnection.Windows, p.PrinterName, $"{p.PrinterName} (ไม่พบตอนนี้)");
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
        foreach (var c in new Control[] { _cboRawLang, _cboDpi, _cboBaud, _chkRotate180 })
            c.Enabled = serial;
        _numGap.Enabled = serial && _cboRawLang.SelectedIndex != 1;
    }

    private void UpdateSizeEditable()
    {
        var custom = (_cboSize.SelectedItem as StickerSize)?.Name == StickerSize.CustomName;
        _numW.Enabled = custom;
        _numH.Enabled = custom;
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

    private void ReadControlsIntoSettings()
    {
        var s = _settings;
        s.CredentialPath = _txtCred.Text.Trim();
        s.ProjectId = _txtProject.Text.Trim();
        s.DatabaseId = _txtDatabase.Text.Trim();
        s.Collection = _txtCollection.Text.Trim();
        s.PrinterIdField = _txtPrinterIdField.Text.Trim();
        s.UnassignedPrinterId = _cboUnassigned.SelectedIndex switch
        {
            0 => AppSettings.RoundRobin,
            1 => 0,
            var i => i - 1,
        };
        s.PendingMode = _cboPending.SelectedIndex == 1 ? PendingMode.PrintedFalse : PendingMode.StatusPending;
        s.ConnectionMode = _cboConnMode.SelectedIndex == 1 ? ConnectionMode.Polling : ConnectionMode.Realtime;
        s.PollIntervalSeconds = (int)_numPoll.Value;
        s.WriteBackStatus = _chkWriteBack.Checked;
        s.AutoConnectOnStart = _chkAutoConnect.Checked;
        s.AutoPrint = _chkAutoPrint.Checked;

        s.FieldTicketCode = _txtFTicket.Text.Trim();
        s.FieldTitle = _txtFTitle.Text.Trim();
        s.FieldFirstName = _txtFFirst.Text.Trim();
        s.FieldLastName = _txtFLast.Text.Trim();
        s.FieldFullName = _txtFFullName.Text.Trim();
        s.FieldPosition = _txtFPosition.Text.Trim();
        s.FieldOrganization = _txtFOrganization.Text.Trim();
        s.FieldCategory = _txtFCategory.Text.Trim();
        s.FieldQr = _txtFQr.Text.Trim();
        s.FontFamily = _cboFont.SelectedItem as string ?? FontManager.DefaultFamily;

        ReadProfileControls(_profile);
    }

    private void ReadProfileControls(PrinterProfile p)
    {
        p.Enabled = _chkEnabled.Checked;
        p.Label = _txtLabel.Text.Trim();

        if (_cboPrinter.SelectedItem is PrinterChoice printer)
        {
            p.Connection = printer.Kind;
            if (printer.Kind == PrinterConnection.SerialPort)
                p.SerialPortName = printer.Name;
            else
                p.PrinterName = printer.Name;
        }
        p.RawLanguage = _cboRawLang.SelectedIndex == 1 ? RawLanguage.EscPos : RawLanguage.Tspl;
        p.PrinterDpi = int.TryParse(_cboDpi.SelectedItem as string, out var dpi) ? dpi : 203;
        p.LabelGapMm = (double)_numGap.Value;
        p.SerialBaudRate = int.TryParse(_cboBaud.SelectedItem as string, out var baud) ? baud : 9600;
        p.Rotate180 = _chkRotate180.Checked;

        // Keep the design proportional when the paper size or orientation changes.
        var before = StickerRenderer.StickerSizeMm(p);
        p.StickerSizeName = (_cboSize.SelectedItem as StickerSize)?.Name ?? StickerSize.CustomName;
        p.StickerWidthMm = (double)_numW.Value;
        p.StickerHeightMm = (double)_numH.Value;
        p.Landscape = _chkLandscape.Checked;
        var after = StickerRenderer.StickerSizeMm(p);
        p.OffsetXMm = (double)_numOffX.Value;
        p.OffsetYMm = (double)_numOffY.Value;
        p.Copies = (int)_numCopies.Value;

        var l = p.Layout;
        _rowName.Save(l.Name);
        _rowPosition.Save(l.Position);
        _rowOrganization.Save(l.Organization);
        _rowTicket.Save(l.TicketCode);
        _rowCategory.Save(l.Category);
        l.Qr.Visible = _chkQr.Checked;
        l.Qr.X = (double)_numQrX.Value;
        l.Qr.Y = (double)_numQrY.Value;
        l.Qr.SizeMm = (double)_numQrSize.Value;
        l.NameTwoLines = _chkTwoLines.Checked;
        l.ShowTitle = _chkShowTitle.Checked;
        l.Name.LineSpacing = (double)_numNameSpacing.Value;
        l.Border = _chkBorder.Checked;

        if (Math.Abs(before.Width - after.Width) > 0.01 || Math.Abs(before.Height - after.Height) > 0.01)
        {
            l.Scale(after.Width / before.Width, after.Height / before.Height);
            LoadLayoutControls(l);
        }
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
        _btnSave.Click += (_, _) => SaveSettings();

        _btnTest.Click += (_, _) =>
        {
            ReadControlsIntoSettings();
            _processor.Enqueue(PrintJob.Sample(_profile.Id));
            _tabs.SelectedTab = _jobsPage;
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
        _tabs.SelectedIndexChanged += (_, _) => RefreshPreview();

        _cboProfile.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _cboProfile.SelectedIndex < 0) return;
            ReadProfileControls(_profile);
            _profile = _settings.Printers[_cboProfile.SelectedIndex];
            LoadProfileControls(_profile);
            RefreshPreview();
        };
        _chkEnabled.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _profile.Enabled = _chkEnabled.Checked;
            RefreshProfileLists();
            UpdateTitle();
        };
        _txtLabel.Leave += (_, _) =>
        {
            _profile.Label = _txtLabel.Text.Trim();
            RefreshProfileLists();
        };

        _btnBrowse.Click += (_, _) => BrowseCredential();
        _txtCred.TextChanged += (_, _) => ShowAccountInfo();
        _btnRefreshPrinters.Click += (_, _) => LoadPrinters();
        _btnAddFont.Click += (_, _) => AddFontFile();

        _cboSize.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (_cboSize.SelectedItem is StickerSize { WidthMm: > 0 } p)
            {
                _loading = true;
                _numW.Value = Clamp(_numW, p.WidthMm);
                _numH.Value = Clamp(_numH, p.HeightMm);
                _loading = false;
            }
            UpdateSizeEditable();
            OnLiveSettingChanged();
        };

        _btnResetLayout.Click += (_, _) =>
        {
            var size = StickerRenderer.StickerSizeMm(_profile);
            _profile.Layout = StickerLayout.CreateDefault(size.Width, size.Height);
            LoadLayoutControls(_profile.Layout);
            RefreshPreview();
        };
        _btnCopyLayout.Click += (_, _) => CopyLayout();

        // Any profile/layout control → live preview.
        var live = new List<Control>
        {
            _cboPrinter, _numW, _numH, _chkLandscape, _numOffX, _numOffY, _numCopies,
            _cboRawLang, _cboDpi, _numGap, _cboBaud, _chkRotate180,
            _chkQr, _numQrX, _numQrY, _numQrSize, _chkTwoLines, _chkShowTitle, _numNameSpacing, _chkBorder, _cboFont,
        };
        foreach (var row in TextRows())
            live.AddRange(row.Controls);
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
        _preview.MouseDown += PreviewMouseDown;
        _preview.MouseMove += PreviewMouseMove;
        _preview.MouseUp += (_, _) => _drag = null;
    }

    private void OnLiveSettingChanged()
    {
        if (_loading) return;
        UpdatePrinterControls();
        ReadControlsIntoSettings();
        RefreshPreview();
    }

    private void CopyLayout()
    {
        ReadControlsIntoSettings();
        var target = _settings.Printers[Math.Max(0, _cboCopyTarget.SelectedIndex)];
        if (target == _profile)
            return;
        if (MessageBox.Show(this, $"คัดลอกขนาดกระดาษและเลย์เอาต์ของ Printer {_profile.Id} ไปทับ Printer {target.Id}?",
                "คัดลอกเลย์เอาต์", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        var copy = _profile.Clone();
        target.StickerSizeName = copy.StickerSizeName;
        target.StickerWidthMm = copy.StickerWidthMm;
        target.StickerHeightMm = copy.StickerHeightMm;
        target.Landscape = copy.Landscape;
        target.Layout = copy.Layout;
        LogLine($"คัดลอกกระดาษ+เลย์เอาต์ Printer {_profile.Id} → Printer {target.Id}");
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

        RefreshProfileLists();
        UpdateTitle();
        if (_connectedWith is not null && ConnectionChanged(_connectedWith, _settings))
        {
            LogLine("ค่าการเชื่อมต่อ/การรับงานเปลี่ยน — กำลังเชื่อมต่อใหม่");
            Disconnect();
            Connect();
        }
        RefreshPreview();
    }

    private static bool ConnectionChanged(AppSettings a, AppSettings b) =>
        a.CredentialPath != b.CredentialPath || a.ProjectId != b.ProjectId || a.DatabaseId != b.DatabaseId ||
        a.Collection != b.Collection || a.PrinterIdField != b.PrinterIdField || a.UnassignedPrinterId != b.UnassignedPrinterId ||
        a.PendingMode != b.PendingMode || a.ConnectionMode != b.ConnectionMode || a.PollIntervalSeconds != b.PollIntervalSeconds ||
        !a.Printers.Select(p => p.Enabled).SequenceEqual(b.Printers.Select(p => p.Enabled)) ||
        a.FieldTicketCode != b.FieldTicketCode || a.FieldTitle != b.FieldTitle || a.FieldFullName != b.FieldFullName ||
        a.FieldFirstName != b.FieldFirstName || a.FieldLastName != b.FieldLastName || a.FieldQr != b.FieldQr ||
        a.FieldPosition != b.FieldPosition || a.FieldOrganization != b.FieldOrganization || a.FieldCategory != b.FieldCategory;

    // ------------------------------------------------------------------ connection

    private void Connect()
    {
        ReadControlsIntoSettings();
        if (!File.Exists(_settings.CredentialPath))
        {
            MessageBox.Show(this, "กรุณาเลือกไฟล์ Service account (.json) ในแท็บ Firebase", "ยังไม่ได้ตั้งค่า",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_settings.Printers.Any(p => p.Enabled))
            LogLine("ยังไม่ได้เปิดใช้เครื่องพิมพ์เครื่องใดเลย — เปิดได้ที่แท็บ เครื่องพิมพ์ 1–5");

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
        // Jobs without a printerId rotate over the printers enabled right now.
        if (job.ProfileId == AppSettings.RoundRobin)
        {
            var enabled = _settings.Printers.Where(p => p.Enabled).Select(p => p.Id).ToList();
            if (enabled.Count == 0)
            {
                LogLine($"ข้าม {job.DocumentId}: ไม่มีเครื่องพิมพ์ที่เปิดใช้");
                return;
            }
            job.ProfileId = enabled[_roundRobin++ % enabled.Count];
        }

        LogLine($"รับงานใหม่: {job.DisplayName} [{job.TicketCode}] → Printer {job.ProfileId}" +
                (job.PrinterId.Length == 0 ? " (ไม่ระบุ printerId)" : "") + $" ({job.DocumentId})");
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
        UpdateCounts();
    }

    private void FlushWaiting()
    {
        foreach (var job in _waiting.Values.ToList())
        {
            _waiting.Remove(job.DocumentId);
            _processor.Enqueue(job);
        }
        UpdateCounts();
    }

    private void PrintSelected()
    {
        ReadControlsIntoSettings();
        var jobs = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PrintJob>().Reverse().ToList();
        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "กรุณาเลือกรายการในตาราง (แท็บ งานพิมพ์)", "Sticker Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        UpdateCounts();
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
            $"Printer {job.ProfileId}" + (job.PrinterId.Length == 0 && !job.IsTest ? " (สลับ)" : ""),
            job.TicketCode,
            job.DisplayName,
            job.Position,
            job.Organization,
            StateText(job.State) + (job.IsReprint && !job.IsTest ? " (ซ้ำ)" : job.IsTest ? " (ทดสอบ)" : ""),
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

    private void UpdateTitle()
    {
        var enabled = _settings.Printers.Where(p => p.Enabled).Select(p => p.Id).ToList();
        Text = "Sticker Print — " + (enabled.Count == 0 ? "ยังไม่ได้เปิดใช้เครื่องพิมพ์" : $"Printer {string.Join(", ", enabled)}");
    }

    private void UpdateCounts() =>
        _lblCounts.Text = $"พิมพ์แล้ว: {_printedCount}   ผิดพลาด: {_errorCount}   รอ: {_waiting.Count}";

    // ------------------------------------------------------------------ preview

    private void RefreshPreview()
    {
        // On the jobs tab show the selected job on its own printer; elsewhere the printer being edited.
        var job = _tabs.SelectedTab == _jobsPage && _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is PrintJob selected
            ? selected
            : PrintJob.Sample(_profile.Id);
        var profile = _settings.FindPrinter(job.ProfileId) ?? _profile;
        var size = StickerRenderer.StickerSizeMm(profile);
        _lblPreviewInfo.Text = $"{profile.DisplayName}: {size.Width:0.#} × {size.Height:0.#} mm\n→ {StickerPrinter.Describe(profile)}"
                               + (job.IsTest ? "  (ข้อมูลตัวอย่าง)" : $"  ({job.DocumentId})");

        var box = _preview.ClientSize;
        if (box.Width < 20 || box.Height < 20 || size.Width <= 0 || size.Height <= 0)
            return;

        var pxPerMm = Math.Max(1f, Math.Min((box.Width - 20) / size.Width, (box.Height - 20) / size.Height));
        try
        {
            var hits = new List<ElementHit>();
            var bmp = StickerRenderer.RenderPreview(job, profile, _settings.FontFamily, pxPerMm, hits);
            using (var g = Graphics.FromImage(bmp))
                g.DrawRectangle(Pens.Gray, 0, 0, bmp.Width - 1, bmp.Height - 1);
            var old = _preview.Image;
            _preview.Image = bmp;
            old?.Dispose();
            _previewHits = hits;
            _previewPxPerMm = pxPerMm;
            _previewProfileId = profile.Id;
        }
        catch (Exception ex)
        {
            _lblPreviewInfo.Text = $"แสดงตัวอย่างไม่ได้: {ex.Message}";
        }
    }

    /// <summary>Converts a mouse position on the preview to sticker millimetres.</summary>
    private PointF? PreviewToMm(Point p)
    {
        if (_preview.Image is not { } img)
            return null;
        var left = (_preview.ClientSize.Width - img.Width) / 2f;
        var top = (_preview.ClientSize.Height - img.Height) / 2f;
        return new PointF((p.X - left) / _previewPxPerMm, (p.Y - top) / _previewPxPerMm);
    }

    private ElementHit? HitTest(Point p)
    {
        if (_previewProfileId != _profile.Id || PreviewToMm(p) is not { } mm)
            return null; // dragging edits only the printer being edited
        for (var i = _previewHits.Count - 1; i >= 0; i--)
        {
            var r = _previewHits[i].Bounds;
            r.Inflate(0.8f, 0.8f);
            if (r.Contains(mm))
                return _previewHits[i];
        }
        return null;
    }

    private void PreviewMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || HitTest(e.Location) is not { } hit || PreviewToMm(e.Location) is not { } mm)
            return;
        var (x, y) = ElementPosition(hit.Key);
        _drag = (hit.Key, mm, x, y);
    }

    private void PreviewMouseMove(object? sender, MouseEventArgs e)
    {
        if (_drag is not { } d)
        {
            _preview.Cursor = HitTest(e.Location) is null ? Cursors.Default : Cursors.SizeAll;
            return;
        }
        if (PreviewToMm(e.Location) is not { } mm)
            return;

        static decimal Snap(double v) => (decimal)(Math.Round(v * 2) / 2); // 0.5 mm steps
        var (nx, ny) = (Snap(d.X + mm.X - d.StartMm.X), Snap(d.Y + mm.Y - d.StartMm.Y));
        var (cx, cy) = ElementControls(d.Key);
        nx = Math.Clamp(nx, cx.Minimum, cx.Maximum);
        ny = Math.Clamp(ny, cy.Minimum, cy.Maximum);
        if (cx.Value == nx && cy.Value == ny)
            return;

        _loading = true;
        cx.Value = nx;
        cy.Value = ny;
        _loading = false;
        OnLiveSettingChanged();
    }

    private (double X, double Y) ElementPosition(string key)
    {
        var (x, y) = ElementControls(key);
        return ((double)x.Value, (double)y.Value);
    }

    private (NumericUpDown X, NumericUpDown Y) ElementControls(string key) => key switch
    {
        StickerRenderer.KeyName => (_rowName.X, _rowName.Y),
        StickerRenderer.KeyPosition => (_rowPosition.X, _rowPosition.Y),
        StickerRenderer.KeyOrganization => (_rowOrganization.X, _rowOrganization.Y),
        StickerRenderer.KeyTicket => (_rowTicket.X, _rowTicket.Y),
        StickerRenderer.KeyCategory => (_rowCategory.X, _rowCategory.Y),
        _ => (_numQrX, _numQrY),
    };

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
