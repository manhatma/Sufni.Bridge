using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sufni.Bridge.Services;

namespace Sufni.Bridge.Models;

public static class TrackTimeline
{
    public static async Task<List<WallClockSlice>> FlattenAsync(
        Guid sessionId,
        IDatabaseService databaseService,
        IReadOnlyDictionary<Guid, Session> byId,
        HashSet<Guid> combinedIds,
        double sessionStartSeconds = 0)
    {
        var sourceIds = await databaseService.GetCombinedSourcesAsync(sessionId);
        if (sourceIds.Count == 0 || !combinedIds.Contains(sessionId))
        {
            if (!byId.TryGetValue(sessionId, out var leaf) || leaf.Timestamp is null)
                return [];
            var durationMs = (long)(leaf.DurationSeconds ?? 0) * 1000;
            if (durationMs <= 0) return [];
            return [new WallClockSlice((long)leaf.Timestamp.Value * 1000, durationMs, sessionStartSeconds)];
        }

        var slices = new List<WallClockSlice>();
        var offset = sessionStartSeconds;
        foreach (var sourceId in sourceIds)
        {
            var nested = await FlattenAsync(sourceId, databaseService, byId, combinedIds, offset);
            slices.AddRange(nested);
            if (byId.TryGetValue(sourceId, out var source))
                offset += source.DurationSeconds ?? 0;
            else
                offset += nested.Sum(s => s.DurationMs / 1000.0);
        }

        return slices;
    }
}
