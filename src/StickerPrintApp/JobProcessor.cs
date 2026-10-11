using System.Threading.Channels;

namespace StickerPrintApp;

/// <summary>Prints queued jobs one at a time: claim in Firestore → print → write status back.</summary>
public sealed class JobProcessor
{
    private readonly Channel<PrintJob> _queue = Channel.CreateUnbounded<PrintJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<AppSettings> _settings;
    private readonly Func<FirestoreJobSource?> _source;
    private readonly PrintedLog _printedLog = new();
    private readonly HashSet<string> _inFlight = [];

    public JobProcessor(Func<AppSettings> settings, Func<FirestoreJobSource?> source)
    {
        _settings = settings;
        _source = source;
    }

    public event Action<PrintJob>? JobChanged;
    public event Action<string>? Log;

    /// <summary>Queues a job. Returns false if it is already being handled or was printed before.</summary>
    public bool Enqueue(PrintJob job)
    {
        if (!job.IsReprint)
        {
            lock (_inFlight)
            {
                if (!_settings().WriteBackStatus && _printedLog.Contains(job.DocumentId))
                    return false;
                if (!_inFlight.Add(job.DocumentId))
                    return false;
            }
        }

        job.State = JobState.Queued;
        job.Message = "";
        JobChanged?.Invoke(job);
        _queue.Writer.TryWrite(job);
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            var s = _settings().Clone(); // settings may change in the UI while we print
            var source = _source();
            var writeBack = s.WriteBackStatus && !job.IsTest && source is not null;
            var claimed = false;
            try
            {
                if (!job.IsReprint && s.WriteBackStatus)
                {
                    if (source is null)
                        throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อ Firestore");
                    claimed = await source.TryClaimAsync(job.DocumentId, ct);
                    if (!claimed)
                    {
                        Update(job, JobState.Skipped, "งานนี้ถูกพิมพ์/รับไปแล้วโดยเครื่องอื่น");
                        continue;
                    }
                }

                Update(job, JobState.Printing, $"กำลังพิมพ์ที่ {PrinterLabel(s)}");
                StickerPrinter.Print(job, s, s.Copies, Log);

                if (!job.IsReprint && !job.IsTest)
                    _printedLog.Add(job.DocumentId);

                if (writeBack)
                {
                    try
                    {
                        await source!.MarkPrintedAsync(job.DocumentId, PrinterLabel(s), ct);
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"พิมพ์ {job.DocumentId} แล้ว แต่บันทึกสถานะกลับ Firestore ไม่สำเร็จ: {ex.GetBaseException().Message}");
                    }
                }

                Update(job, JobState.Printed, $"{PrinterLabel(s)} × {Math.Max(1, s.Copies)}");
                Log?.Invoke($"พิมพ์แล้ว: {job.DisplayName} ({job.DocumentId})");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Update(job, JobState.Error, "ยกเลิก");
                break;
            }
            catch (Exception ex)
            {
                var msg = ex.GetBaseException().Message;
                Update(job, JobState.Error, msg);
                Log?.Invoke($"พิมพ์ {job.DocumentId} ไม่สำเร็จ: {msg}");
                if (writeBack && (claimed || job.IsReprint))
                {
                    try { await source!.MarkErrorAsync(job.DocumentId, msg, CancellationToken.None); }
                    catch (Exception ex2) { Log?.Invoke($"บันทึกสถานะ error ไม่สำเร็จ: {ex2.GetBaseException().Message}"); }
                }
            }
            finally
            {
                if (!job.IsReprint)
                    lock (_inFlight) _inFlight.Remove(job.DocumentId);
            }
        }
    }

    private void Update(PrintJob job, JobState state, string message)
    {
        job.State = state;
        job.Message = message;
        JobChanged?.Invoke(job);
    }

    private static string PrinterLabel(AppSettings s) => StickerPrinter.Describe(s);
}

/// <summary>
/// Remembers printed document IDs on disk. Only consulted when status write-back is turned off,
/// so restarting the app does not reprint jobs that are still marked "pending" in Firestore.
/// </summary>
internal sealed class PrintedLog
{
    private readonly string _path = Path.Combine(AppSettings.Folder, "printed_ids.txt");
    private readonly HashSet<string> _ids;

    public PrintedLog()
    {
        try
        {
            _ids = File.Exists(_path) ? [.. File.ReadAllLines(_path).Where(l => l.Length > 0)] : [];
        }
        catch
        {
            _ids = [];
        }
    }

    public bool Contains(string id)
    {
        lock (_ids) return _ids.Contains(id);
    }

    public void Add(string id)
    {
        lock (_ids)
        {
            if (!_ids.Add(id))
                return;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllLines(_path, [id]);
            }
            catch
            {
                // Best effort; the in-memory set still prevents duplicates in this session.
            }
        }
    }
}
