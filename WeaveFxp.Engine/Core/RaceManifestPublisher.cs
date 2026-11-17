using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

internal readonly record struct RaceManifestObservation(string Name, long? Size, bool? Covered, bool Available);

internal sealed class RaceManifestPublisher
{
    private readonly JsonStore _store;
    private readonly string _jobId;
    private readonly object _sync = new();
    private readonly Dictionary<string, RaceSfvFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _next;

    public RaceManifestPublisher(JsonStore store, string jobId)
    {
        _store = store;
        _jobId = jobId;
        _store.UpdateJobTransient(jobId, job =>
        {
            foreach (var file in job.SfvFiles)
            {
                _files[file.Name] = file;
                if (file.UploadedSize is long uploaded) _uploads[file.Name] = uploaded;
                file.Status = "unknown";
            }
            foreach (var file in job.Files.Where(f => f.Status == "done")) _uploads[file.Name] = file.Size;
        });
    }

    public bool Due { get { lock (_sync) return DateTime.UtcNow >= _next; } }

    public void Register(string sfv, string parent, IEnumerable<SfvFile> entries)
    {
        lock (_sync)
            _store.UpdateJobTransient(_jobId, job =>
            {
                foreach (var entry in entries)
                {
                    var name = string.IsNullOrEmpty(parent) ? entry.Name : parent + "/" + entry.Name;
                    if (!_files.TryGetValue(name, out var file))
                    {
                        file = new RaceSfvFile { Name = name };
                        _files.Add(name, file);
                        job.SfvFiles.Add(file);
                    }
                    file.Crc = entry.Crc;
                    file.Sfv = sfv;
                    UpdateUpload(file);
                }
            });
    }

    // Called only after the successful final FTP replies, including uploads before SFV parsing.
    public void Uploaded(string name, long size)
    {
        lock (_sync)
        {
            _uploads[name] = size;
            if (_files.TryGetValue(name, out var file))
                _store.UpdateJobTransient(_jobId, _ => UpdateUpload(file));
        }
    }

    public void Publish(IEnumerable<RaceManifestObservation> observations)
    {
        lock (_sync)
        {
            _next = DateTime.UtcNow.AddMilliseconds(250);
            _store.UpdateJobTransient(_jobId, _ =>
            {
                foreach (var observation in observations)
                {
                    if (!_files.TryGetValue(observation.Name, out var file)) continue;
                    file.Size = observation.Size;
                    UpdateUpload(file);
                    file.Status = observation.Covered switch
                    {
                        true => "covered",
                        false => observation.Available ? "waiting" : "missing",
                        null => "unknown",
                    };
                }
            });
        }
    }

    private void UpdateUpload(RaceSfvFile file)
    {
        file.UploadedSize = _uploads.TryGetValue(file.Name, out var size) ? size : null;
        file.UploadedByUs = file.UploadedSize is > 0 &&
            (!file.Size.HasValue || file.Size.Value == size) &&
            (size != 1 || !WeaveEngine.SfvExcludesSingleByte(file.Crc));
    }
}
