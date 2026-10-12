using Google.Api.Gax.Grpc.Rest;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

namespace StickerPrintApp;

/// <summary>
/// Subscribes to this station's unprinted jobs in Firestore and writes the result back
/// (printed = true, status = printed / error).
/// </summary>
public sealed class FirestoreJobSource
{
    public const string StatusPending = "pending";
    public const string StatusPrinting = "printing";
    public const string StatusPrinted = "printed";
    public const string StatusError = "error";

    private static readonly TimeSpan FirstSnapshotWarning = TimeSpan.FromSeconds(20);

    private readonly AppSettings _settings;
    private FirestoreDb? _db;

    public FirestoreJobSource(AppSettings settings) => _settings = settings;

    public event Action<PrintJob>? JobArrived;
    public event Action<string>? Log;
    public event Action<bool, string>? ConnectionChanged;

    public string Description =>
        $"{_settings.ProjectId}/{_settings.Collection}" +
        $" (printer {string.Join(", ", _settings.Printers.Where(p => p.Enabled).Select(p => p.Id))})" +
        (_settings.ConnectionMode == ConnectionMode.Polling ? " [Polling]" : "");

    /// <summary>Connects and keeps listening, reconnecting with back-off, until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _db = await BuildDbAsync(ct);
                if (_settings.ConnectionMode == ConnectionMode.Polling)
                    await PollAsync(_db, ct, () => delay = TimeSpan.FromSeconds(3));
                else
                    await ListenAsync(_db, ct, () => delay = TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Firestore error: {ex.GetBaseException().Message}");
            }

            if (ct.IsCancellationRequested)
                break;

            ConnectionChanged?.Invoke(false, $"การเชื่อมต่อหลุด — จะลองใหม่ใน {delay.TotalSeconds:0} วินาที");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }

        ConnectionChanged?.Invoke(false, "ไม่ได้เชื่อมต่อ");
    }

    private async Task ListenAsync(FirestoreDb db, CancellationToken ct, Action onConnected)
    {
        var gotFirst = 0;
        HashSet<string> emitted = []; // callbacks are sequential, so no locking needed
        var listener = BuildQuery(db).Listen(snapshot =>
        {
            // "Connected" only once data actually flows; a blocked gRPC stream otherwise looks healthy.
            if (Interlocked.Exchange(ref gotFirst, 1) == 0)
            {
                onConnected();
                ConnectionChanged?.Invoke(true, $"เชื่อมต่อแล้ว: {Description}");
            }

            // A job re-enters the query (ChangeType.Added) whenever it becomes unprinted again,
            // which is how a reprint can be requested from the backend. Modified covers a job whose
            // printerId changes to one of ours while still pending.
            foreach (var change in snapshot.Changes)
            {
                var id = change.Document.Id;
                if (change.ChangeType == DocumentChange.Type.Removed || !IsForThisPc(change.Document))
                    emitted.Remove(id);
                else if (emitted.Add(id))
                    Emit(change.Document);
            }
        }, ct);

        try
        {
            var warning = Task.Delay(FirstSnapshotWarning, ct);
            if (await Task.WhenAny(listener.ListenerTask, warning) == warning && !ct.IsCancellationRequested &&
                Volatile.Read(ref gotFirst) == 0)
            {
                Log?.Invoke("ยังไม่ได้รับข้อมูลจาก Firestore — ตรวจสอบอินเทอร์เน็ต/Firewall/Proxy " +
                            "หรือเปลี่ยน \"การเชื่อมต่อ\" เป็นโหมด Polling ในแท็บ ตั้งค่า");
            }
            await listener.ListenerTask; // completes when stopped or on a non-retryable error
        }
        finally
        {
            try { await listener.StopAsync(CancellationToken.None); } catch { /* already stopped */ }
        }
    }

    private async Task PollAsync(FirestoreDb db, CancellationToken ct, Action onConnected)
    {
        var query = BuildQuery(db);
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollIntervalSeconds, 1, 60));
        HashSet<string> previous = [];
        var first = true;

        while (!ct.IsCancellationRequested)
        {
            var snapshot = await query.GetSnapshotAsync(ct);
            if (first)
            {
                first = false;
                onConnected();
                ConnectionChanged?.Invoke(true, $"เชื่อมต่อแล้ว: {Description}");
            }

            // Only report documents that newly appeared, mirroring the listener's "Added" events.
            var current = new HashSet<string>();
            foreach (var doc in snapshot.Documents.Where(IsForThisPc))
            {
                current.Add(doc.Id);
                if (!previous.Contains(doc.Id))
                    Emit(doc);
            }
            previous = current;

            await Task.Delay(interval, ct);
        }
    }

    private void Emit(DocumentSnapshot doc)
    {
        try
        {
            JobArrived?.Invoke(ToJob(doc));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"อ่านเอกสาร {doc.Id} ไม่สำเร็จ: {ex.Message}");
        }
    }

    private Task<FirestoreDb> BuildDbAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.CredentialPath) || !File.Exists(_settings.CredentialPath))
            throw new FileNotFoundException("ไม่พบไฟล์ Service Account (.json) — ไปที่แท็บ ตั้งค่า เพื่อเลือกไฟล์");

        // Load explicitly as a service account so a different credential type in the file is rejected.
        var credential = CredentialFactory
            .FromFile<ServiceAccountCredential>(_settings.CredentialPath)
            .ToGoogleCredential();

        var builder = new FirestoreDbBuilder
        {
            ProjectId = _settings.ProjectId,
            DatabaseId = string.IsNullOrWhiteSpace(_settings.DatabaseId) ? "(default)" : _settings.DatabaseId,
            GoogleCredential = credential,
        };
        if (_settings.ConnectionMode == ConnectionMode.Polling)
            builder.GrpcAdapter = RestGrpcAdapter.Default; // plain HTTPS/JSON; works through most proxies

        return builder.BuildAsync(ct);
    }

    private Query BuildQuery(FirestoreDb db)
    {
        Query q = db.Collection(_settings.Collection);
        q = _settings.PendingMode == PendingMode.PrintedFalse
            ? q.WhereEqualTo("printed", false)
            : q.WhereEqualTo("status", StatusPending);

        // Routing by printerId happens client-side (IsForThisPc): Firestore cannot query for a missing
        // field, and only pending jobs are read, so this is cheap.
        return q;
    }

    private readonly HashSet<string> _ignored = [];

    private bool IsForThisPc(DocumentSnapshot doc)
    {
        var printerId = ReadField(doc.ToDictionary(), _settings.PrinterIdField);
        if (_settings.ResolvePrinter(printerId) != 0)
            return true;

        lock (_ignored)
        {
            if (_ignored.Add(doc.Id))
                Log?.Invoke(printerId.Length == 0
                    ? $"ข้าม job {doc.Id}: ไม่มี {_settings.PrinterIdField} และตั้งค่าไว้ว่าไม่พิมพ์งานที่ไม่ระบุเครื่อง"
                    : $"ข้าม job {doc.Id}: {_settings.PrinterIdField} = {printerId} ไม่ได้เปิดใช้ในเครื่องนี้");
        }
        return false;
    }

    private PrintJob ToJob(DocumentSnapshot doc)
    {
        var data = doc.ToDictionary();
        var first = ReadField(data, _settings.FieldFirstName);
        var last = ReadField(data, _settings.FieldLastName);
        var fullName = ReadField(data, _settings.FieldFullName);
        if (fullName.Length == 0)
            fullName = $"{first} {last}".Trim();
        var printerId = ReadField(data, _settings.PrinterIdField);

        return new PrintJob
        {
            DocumentId = doc.Id,
            TicketCode = ReadField(data, _settings.FieldTicketCode),
            Title = ReadField(data, _settings.FieldTitle),
            FirstName = first,
            LastName = last,
            FullName = fullName,
            Position = ReadField(data, _settings.FieldPosition),
            Organization = ReadField(data, _settings.FieldOrganization),
            Category = ReadField(data, _settings.FieldCategory),
            QrData = ReadField(data, _settings.FieldQr),
            PrinterId = printerId,
            ProfileId = _settings.ResolvePrinter(printerId),
        };
    }

    /// <summary>Reads a field by name or dotted path ("guest.position"). Missing/null values become "".</summary>
    public static string ReadField(IDictionary<string, object> data, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        object? current = data;
        foreach (var part in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is IDictionary<string, object> map && map.TryGetValue(part, out var next))
                current = next;
            else
                return "";
        }

        return current switch
        {
            null => "",
            string s => s.Trim(),
            Timestamp ts => ts.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            IDictionary<string, object> => "",
            _ => Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "",
        };
    }

    /// <summary>
    /// Atomically marks the job as "printing" so that two stations / app instances never print the
    /// same job. Returns false if it is no longer pending (already printed or taken by someone else).
    /// </summary>
    public async Task<bool> TryClaimAsync(PrintJob job, CancellationToken ct)
    {
        var db = _db ?? throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อ Firestore");
        var docRef = db.Collection(_settings.Collection).Document(job.DocumentId);

        return await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(docRef, ct);
            if (!snap.Exists || !IsPending(snap))
                return false;

            tx.Update(docRef, new Dictionary<string, object>
            {
                ["status"] = StatusPrinting,
                ["printing_by"] = Environment.MachineName,
                ["printing_printer"] = job.ProfileId,
                ["printing_at"] = FieldValue.ServerTimestamp,
            });
            return true;
        }, cancellationToken: ct);
    }

    private bool IsPending(DocumentSnapshot snap)
    {
        snap.TryGetValue<string>("status", out var status);
        if (_settings.PendingMode == PendingMode.StatusPending)
            return status == StatusPending;

        var printed = snap.TryGetValue<bool>("printed", out var p) && p;
        return !printed && status is not (StatusPrinting or StatusPrinted);
    }

    public Task MarkPrintedAsync(PrintJob job, string printer, CancellationToken ct) =>
        UpdateAsync(job.DocumentId, new Dictionary<string, object>
        {
            ["printed"] = true,
            ["status"] = StatusPrinted,
            ["printed_at"] = FieldValue.ServerTimestamp,
            ["printed_by"] = Environment.MachineName,
            ["printed_printer"] = job.ProfileId,
            ["printer"] = printer,
        }, ct);

    public Task MarkErrorAsync(PrintJob job, string message, CancellationToken ct) =>
        UpdateAsync(job.DocumentId, new Dictionary<string, object>
        {
            ["status"] = StatusError,
            ["error_message"] = message,
            ["error_at"] = FieldValue.ServerTimestamp,
        }, ct);

    private Task UpdateAsync(string documentId, Dictionary<string, object> updates, CancellationToken ct)
    {
        var db = _db ?? throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อ Firestore");
        return db.Collection(_settings.Collection).Document(documentId).UpdateAsync(updates, cancellationToken: ct);
    }
}
