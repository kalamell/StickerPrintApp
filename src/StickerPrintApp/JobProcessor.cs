using System.Collections.Concurrent;
using System.Threading.Channels;

namespace StickerPrintApp;

/// <summary>
/// Prints jobs: claim in Firestore → print → write status back. Each printer slot has its own queue
/// and worker, so a slow or offline printer never holds up the others.
/// </summary>
public sealed class JobProcessor
{
    private readonly ConcurrentDictionary<int, Channel<PrintJob>> _queues = new();
    private readonly Func<AppSettings> _settings;
    private readonly Func<FirestoreJobSource?> _source;
    private readonly CancellationToken _ct;
    private readonly PrintedLog _printedLog = new();
    private readonly HashSet<string> _inFlight = [];

    public JobProcessor(Func<AppSettings> settings, Func<FirestoreJobSource?> source, CancellationToken ct)
    {
        _settings = settings;
        _source = source;
        _ct = ct;
    }

    public event Action<PrintJob>? JobChanged;
    public event Action<string>? Log;

    /// <summary>Queues a job on its printer. Returns false if it is already being handled or was printed before.</summary>
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
        job.Message = $"รอคิว Printer {job.ProfileId}";
        JobChanged?.Invoke(job);
        QueueFor(job.ProfileId).Writer.TryWrite(job);
        return true;
    }

    private Channel<PrintJob> QueueFor(int profileId) =>
        _queues.GetOrAdd(profileId, id =>
        {
            var channel = Channel.CreateUnbounded<PrintJob>(new UnboundedChannelOptions { SingleReader = true });
            _ = Task.Run(() => RunAsync(channel.Reader, _ct));
            return channel;
        });

    private async Task RunAsync(ChannelReader<PrintJob> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(ct))
                await ProcessAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            // app closing
        }
    }

    private async Task ProcessAsync(PrintJob job, CancellationToken ct)
    {
        var settings = _settings().Clone(); // settings may change in the UI while we print
        var source = _source();
        var claimed = false;
        var writeBack = settings.WriteBackStatus && !job.IsTest && source is not null;
        try
        {
            var profile = settings.FindPrinter(job.ProfileId)
                          ?? throw new InvalidOperationException($"ไม่มี Printer {job.ProfileId} ในการตั้งค่า");
            var label = $"Printer {profile.Id}: {StickerPrinter.Describe(profile)}";

            if (!job.IsReprint && settings.WriteBackStatus)
            {
                if (source is null)
                    throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อ Firestore");
                claimed = await source.TryClaimAsync(job, ct);
                if (!claimed)
                {
                    Update(job, JobState.Skipped, "งานนี้ถูกพิมพ์/รับไปแล้วโดยเครื่องอื่น");
                    return;
                }
            }

            Update(job, JobState.Printing, $"กำลังพิมพ์ที่ {label}");
            StickerPrinter.Print(job, profile, settings.FontFamily, profile.Copies, Log);

            if (!job.IsReprint && !job.IsTest)
                _printedLog.Add(job.DocumentId);

            if (writeBack)
            {
                try
                {
                    await source!.MarkPrintedAsync(job, label, ct);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"พิมพ์ {job.DocumentId} แล้ว แต่บันทึกสถานะกลับ Firestore ไม่สำเร็จ: {ex.GetBaseException().Message}");
                }
            }

            Update(job, JobState.Printed, $"{label} × {Math.Max(1, profile.Copies)}");
            Log?.Invoke($"พิมพ์แล้ว: {job.DisplayName} ({job.DocumentId}) → {label}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(job, JobState.Error, "ยกเลิก");
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex.GetBaseException().Message;
            Update(job, JobState.Error, msg);
            Log?.Invoke($"พิมพ์ {job.DocumentId} ไม่สำเร็จ: {msg}");
            if (writeBack && (claimed || job.IsReprint))
            {
                try { await source!.MarkErrorAsync(job, msg, CancellationToken.None); }
                catch (Exception ex2) { Log?.Invoke($"บันทึกสถานะ error ไม่สำเร็จ: {ex2.GetBaseException().Message}"); }
            }
        }
        finally
        {
            if (!job.IsReprint)
                lock (_inFlight) _inFlight.Remove(job.DocumentId);
        }
    }

    private void Update(PrintJob job, JobState state, string message)
    {
        job.State = state;
        job.Message = message;
        JobChanged?.Invoke(job);
    }
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
