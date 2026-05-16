using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HestonVolCalibrator.Calibration;

namespace HestonVolCalibrator.Web;

public record SnapshotListEntry(
    string Name,
    DateTime CreatedAtUtc,
    string Ticker,
    string Source,
    bool HasCalibration,
    long SizeBytes);

// Filesystem-backed snapshot store. One JSON file per snapshot under <root>/snapshots/.
// Names are user-supplied; we sanitise to a strict character set to keep them safe as
// filenames cross-platform.
public sealed class SnapshotService
{
    public const string SnapshotVersion = "1.0";

    private readonly string _dir;
    private readonly JsonSerializerOptions _opts;

    public SnapshotService(string contentRoot)
    {
        _dir = Path.Combine(contentRoot, "snapshots");
        System.IO.Directory.CreateDirectory(_dir);
        _opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new NaNAsNullDoubleConverter()
            }
        };
    }

    public string Directory => _dir;

    // Lowercased letters/digits/dash/underscore/dot only. Anything else collapses to '_'.
    // Empty / dot-only names are rejected so we never produce a hidden or empty file.
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Snapshot name is required.", nameof(name));
        var chars = name.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                      c == '-' || c == '_' || c == '.';
            if (!ok) chars[i] = '_';
        }
        var s = new string(chars).Trim('.', '_');
        if (string.IsNullOrEmpty(s))
            throw new ArgumentException("Snapshot name has no usable characters.", nameof(name));
        return s;
    }

    private string PathFor(string name) => Path.Combine(_dir, Sanitize(name) + ".json");

    public async Task SaveAsync(string name, Snapshot snap)
    {
        var path = PathFor(name);
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, snap, _opts);
    }

    public async Task<Snapshot?> LoadAsync(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Snapshot>(fs, _opts);
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // Listing reads a small header from each file (Version, CreatedAtUtc, surface metadata,
    // calibration flag) without materialising the full grids — cheap even with many snapshots.
    public List<SnapshotListEntry> List()
    {
        var result = new List<SnapshotListEntry>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var info = new FileInfo(path);
                using var fs = File.OpenRead(path);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                string ticker = "?";
                string source = "?";
                bool hasCalib = false;
                DateTime created = info.LastWriteTimeUtc;
                if (root.TryGetProperty("createdAtUtc", out var cEl) && cEl.ValueKind == JsonValueKind.String &&
                    cEl.TryGetDateTime(out var dt)) created = dt;
                if (root.TryGetProperty("surface", out var sEl) && sEl.ValueKind == JsonValueKind.Object)
                {
                    if (sEl.TryGetProperty("ticker", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                        ticker = tEl.GetString() ?? "?";
                    if (sEl.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String)
                        source = srcEl.GetString() ?? "?";
                }
                if (root.TryGetProperty("calibration", out var calEl) && calEl.ValueKind == JsonValueKind.Object)
                    hasCalib = true;
                result.Add(new SnapshotListEntry(name, created, ticker, source, hasCalib, info.Length));
            }
            catch
            {
                // Corrupt files are silently skipped from listing; they remain on disk for inspection.
            }
        }
        return result.OrderByDescending(e => e.CreatedAtUtc).ToList();
    }
}
