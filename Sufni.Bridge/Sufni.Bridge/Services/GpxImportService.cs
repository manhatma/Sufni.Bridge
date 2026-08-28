using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.Services;

public class GpxImportService : IGpxImportService
{
    private readonly IDatabaseService databaseService;

    public GpxImportService(IDatabaseService databaseService)
    {
        this.databaseService = databaseService;
    }

    public async Task<GpxImportResult> ImportAsync(Stream gpx, string fileName)
    {
        TrackPoints points;
        string? parsedName;
        try
        {
            points = GpxParser.Parse(gpx, out parsedName);
        }
        catch (Exception ex)
        {
            return new GpxImportResult
            {
                Success = false,
                Error = ex.Message
            };
        }

        var name = string.IsNullOrWhiteSpace(parsedName)
            ? Path.GetFileNameWithoutExtension(fileName)
            : parsedName;
        if (string.IsNullOrWhiteSpace(name))
            name = "GPX track";

        var track = new Track
        {
            Id = Guid.NewGuid(),
            Name = name,
            Imported = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            StartTimeMs = points.TimeMs[0],
            EndTimeMs = points.TimeMs[^1],
            Points = MessagePackSerializer.Serialize(points)
        };
        await databaseService.PutTrackAsync(track);

        var candidates = await databaseService.GetSessionsInRangeAsync(track.StartTimeMs, track.EndTimeMs);
        var allSessions = await databaseService.GetSessionsAsync();
        var byId = allSessions.ToDictionary(s => s.Id);
        var combinedIds = await databaseService.GetAllCombinedIdsAsync();
        // A cropped recording contributes only its cropped span to a combined session, so the
        // wall-clock windows need its sample rate to convert the crop's sample indices to seconds.
        var sampleRates = await databaseService.GetSampleRatesAsync();
        double RateFor(Guid id) => sampleRates.TryGetValue(id, out var r) ? r : 0;

        var assignedNames = new List<string>();
        var assignedIds = new List<Guid>();

        foreach (var session in candidates)
        {
            if (session.Track is not null) continue;
            if (!await OverlapsTrackAsync(session, track.StartTimeMs, track.EndTimeMs, byId, combinedIds, RateFor))
                continue;

            session.Track = track.Id;
            await databaseService.PutSessionAsync(session);
            assignedNames.Add(session.Name);
            assignedIds.Add(session.Id);
        }

        var intervals = new List<WallClockSlice>();
        foreach (var sessionId in assignedIds)
        {
            var slices = await TrackTimeline.FlattenAsync(
                sessionId, databaseService.GetCombinedSourcesAsync, byId, combinedIds, sampleRateFor: RateFor);
            intervals.AddRange(slices);
        }

        var estimated = TrackTimeOffsetEstimator.Estimate(points, intervals);
        if (estimated is not null)
        {
            track.TimeOffsetMs = estimated.Value;
            await databaseService.PutTrackAsync(track);
        }

        return new GpxImportResult
        {
            Success = true,
            TrackId = track.Id,
            TrackName = track.Name,
            StartTimeMs = track.StartTimeMs,
            EndTimeMs = track.EndTimeMs,
            AssignedSessionNames = assignedNames,
            AssignedSessionIds = assignedIds,
            TimeOffsetMs = track.TimeOffsetMs,
            TimeOffsetEstimated = estimated is not null
        };
    }

    private async Task<bool> OverlapsTrackAsync(
        Session session,
        long trackStartMs,
        long trackEndMs,
        Dictionary<Guid, Session> byId,
        HashSet<Guid> combinedIds,
        Func<Guid, double> sampleRateFor)
    {
        var slices = await TrackTimeline.FlattenAsync(
            session.Id, databaseService.GetCombinedSourcesAsync, byId, combinedIds, sampleRateFor: sampleRateFor);
        if (slices.Count == 0)
        {
            if (session.Timestamp is null) return false;
            var start = (long)session.Timestamp.Value * 1000;
            var end = start + (long)(session.DurationSeconds ?? 0) * 1000;
            return start <= trackEndMs && end >= trackStartMs;
        }

        foreach (var slice in slices)
        {
            var start = slice.StartUnixMs;
            var end = slice.StartUnixMs + slice.DurationMs;
            if (start <= trackEndMs && end >= trackStartMs)
                return true;
        }

        return false;
    }
}
