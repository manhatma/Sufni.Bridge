using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sufni.Bridge.Models;

public static class TrackTimeline
{
    /// <summary>
    /// Expands a session into the wall-clock windows its samples actually came from, recursing
    /// through combined sessions until it reaches recordings. A combined session may itself hold
    /// combined sessions, and its own timestamp plus duration then describe one contiguous block
    /// that the recordings never occupied — the runs are minutes apart on the clock. Only the
    /// leaves carry real wall-clock time.
    /// </summary>
    /// <param name="sampleRateFor">
    /// Samples per second for one session, used to turn a leaf's crop into a wall-clock window.
    /// Combining bakes each source's crop into the combined data (see SessionListViewModel.Combine),
    /// so a cropped leaf contributes only its cropped span. Return 0 for a session whose rate is
    /// unknown; its crop is then ignored and the window is the full recording, which is off by the
    /// trimmed seconds. Pass null when no rate is available at all.
    /// </param>
    /// <param name="getSources">
    /// Combined-session sources by id, in session order. A function rather than the database
    /// service so the recursion can be tested without one.
    /// </param>
    public static async Task<List<WallClockSlice>> FlattenAsync(
        Guid sessionId,
        Func<Guid, Task<List<Guid>>> getSources,
        IReadOnlyDictionary<Guid, Session> byId,
        HashSet<Guid> combinedIds,
        double sessionStartSeconds = 0,
        Func<Guid, double>? sampleRateFor = null)
    {
        var sourceIds = await getSources(sessionId);
        if (sourceIds.Count == 0 || !combinedIds.Contains(sessionId))
        {
            if (!byId.TryGetValue(sessionId, out var leaf) || leaf.Timestamp is null)
                return [];

            var startMs = (long)leaf.Timestamp.Value * 1000;
            var durationMs = (long)(leaf.DurationSeconds ?? 0) * 1000;

            var rate = sampleRateFor?.Invoke(sessionId) ?? 0;
            if (rate > 0 &&
                leaf.CropStartSample is { } cropStart &&
                leaf.CropEndSample is { } cropEnd &&
                cropEnd > cropStart)
            {
                startMs += (long)(cropStart / rate * 1000.0);
                durationMs = (long)((cropEnd - cropStart) / rate * 1000.0);
            }

            if (durationMs <= 0) return [];
            return [new WallClockSlice(startMs, durationMs, sessionStartSeconds)];
        }

        var slices = new List<WallClockSlice>();
        var offset = sessionStartSeconds;
        foreach (var sourceId in sourceIds)
        {
            var nested = await FlattenAsync(sourceId, getSources, byId, combinedIds, offset, sampleRateFor);
            slices.AddRange(nested);

            // Advance by what the nested slices actually cover, so a cropped leaf shifts everything
            // after it by the trimmed amount — the same way the combined sample stream does.
            var covered = 0.0;
            foreach (var slice in nested)
                covered += slice.DurationMs / 1000.0;
            if (covered <= 0 && byId.TryGetValue(sourceId, out var source))
                covered = source.DurationSeconds ?? 0;
            offset += covered;
        }

        return slices;
    }
}
