using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sufni.Bridge.Models;
using Xunit;

namespace Sufni.Bridge.Tests;

public class TrackTimelineTests
{
    private const double Rate = 860.0;

    private static Session Leaf(Guid id, string name, long unixSeconds, int durationSeconds,
        int? cropStart = null, int? cropEnd = null) =>
        new(id, name, "", null, (int)unixSeconds)
        {
            DurationSeconds = durationSeconds,
            CropStartSample = cropStart,
            CropEndSample = cropEnd
        };

    private static Session Combined(Guid id, string name, long unixSeconds, int durationSeconds) =>
        new(id, name, "", null, (int)unixSeconds) { DurationSeconds = durationSeconds };

    /// <summary>
    /// Session of 27/08/2026 that exposed the bug: two combined sessions combined again. The runs
    /// are spread over 1.5 hours of wall-clock time, so the outer session's own timestamp plus
    /// duration describes a block the recordings never occupied.
    /// </summary>
    [Fact]
    public async Task Flatten_NestedCombinedSessions_YieldsEveryLeafWindow()
    {
        var pizMus = Guid.NewGuid();
        var jungle1 = Guid.NewGuid();
        var flaedle1 = Guid.NewGuid();
        var flaedle2 = Guid.NewGuid();
        var spaetzle = Guid.NewGuid();
        var jungle2 = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var root = Guid.NewGuid();

        var sessions = new List<Session>
        {
            Leaf(pizMus, "Piz Mus DH", 1787844620, 55),
            Leaf(jungle1, "Jungle #1", 1787844986, 54, 860, 45840),
            Leaf(flaedle1, "FlädLE #1", 1787845797, 135),
            Leaf(flaedle2, "FlädLE #2", 1787847936, 137),
            Leaf(spaetzle, "SpätzLE", 1787849282, 136, 1720, 116194),
            Leaf(jungle2, "Jungle #2", 1787849571, 53),
            Combined(first, "first", 1787844620, 244),
            Combined(second, "second", 1787847936, 325),
            Combined(root, "root", 1787844620, 569)
        };

        var byId = sessions.ToDictionary(s => s.Id);
        var combinedIds = new HashSet<Guid> { first, second, root };
        var sources = new Dictionary<Guid, List<Guid>>
        {
            [root] = [first, second],
            [first] = [pizMus, jungle1, flaedle1],
            [second] = [flaedle2, spaetzle, jungle2]
        };

        Task<List<Guid>> GetSources(Guid id) =>
            Task.FromResult(sources.TryGetValue(id, out var s) ? s : []);

        var slices = await TrackTimeline.FlattenAsync(root, GetSources, byId, combinedIds, sampleRateFor: _ => Rate);

        Assert.Equal(6, slices.Count);

        // Wall-clock starts are the leaves' own, not one contiguous block from the root.
        Assert.Equal(1787844620_000, slices[0].StartUnixMs);
        Assert.Equal(1787845797_000, slices[2].StartUnixMs);
        Assert.Equal(1787849571_000, slices[5].StartUnixMs);

        // Session-time offsets run without gaps: each leaf starts where the previous one ended.
        var expectedOffset = 0.0;
        foreach (var slice in slices)
        {
            Assert.Equal(expectedOffset, slice.SessionStartSeconds, 3);
            expectedOffset += slice.DurationMs / 1000.0;
        }
    }

    [Fact]
    public async Task Flatten_CroppedLeaf_UsesCroppedWindow()
    {
        // Combining bakes each source's crop into the combined data, so the leaf contributes only
        // its cropped span — one second later and 1.7 seconds shorter here.
        var leaf = Guid.NewGuid();
        var root = Guid.NewGuid();
        var sessions = new List<Session>
        {
            Leaf(leaf, "Jungle #1", 1787844986, 54, 860, 45840),
            Combined(root, "root", 1787844986, 52)
        };

        Task<List<Guid>> GetSources(Guid id) =>
            Task.FromResult<List<Guid>>(id == root ? [leaf] : []);

        var slices = await TrackTimeline.FlattenAsync(
            root, GetSources, sessions.ToDictionary(s => s.Id), [root], sampleRateFor: _ => Rate);

        var slice = Assert.Single(slices);
        Assert.Equal(1787844986_000 + 1000, slice.StartUnixMs);
        Assert.Equal((long)((45840 - 860) / Rate * 1000.0), slice.DurationMs);
    }

    [Fact]
    public async Task Flatten_RateKnownPerSession_AppliesOnlyWhereKnown()
    {
        // The rate comes from each session's cache row, and rows written before that column exists
        // have none. A leaf without a known rate keeps its full window instead of guessing.
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var root = Guid.NewGuid();
        var sessions = new List<Session>
        {
            Leaf(known, "known", 1787844986, 54, 860, 45840),
            Leaf(unknown, "unknown", 1787849282, 136, 1720, 116194),
            Combined(root, "root", 1787844986, 190)
        };

        Task<List<Guid>> GetSources(Guid id) =>
            Task.FromResult<List<Guid>>(id == root ? [known, unknown] : []);

        var slices = await TrackTimeline.FlattenAsync(
            root, GetSources, sessions.ToDictionary(s => s.Id), [root],
            sampleRateFor: id => id == known ? Rate : 0);

        Assert.Equal(2, slices.Count);
        Assert.Equal(1787844986_000 + 1000, slices[0].StartUnixMs);
        Assert.Equal((long)((45840 - 860) / Rate * 1000.0), slices[0].DurationMs);
        Assert.Equal(1787849282_000, slices[1].StartUnixMs);
        Assert.Equal(136_000, slices[1].DurationMs);
    }

    [Fact]
    public async Task Flatten_NoSampleRate_KeepsFullLeafWindow()
    {
        // A session that was never cached has no known rate; its crop is then not applied.
        var leaf = Guid.NewGuid();
        var root = Guid.NewGuid();
        var sessions = new List<Session>
        {
            Leaf(leaf, "Jungle #1", 1787844986, 54, 860, 45840),
            Combined(root, "root", 1787844986, 54)
        };

        Task<List<Guid>> GetSources(Guid id) =>
            Task.FromResult<List<Guid>>(id == root ? [leaf] : []);

        var slices = await TrackTimeline.FlattenAsync(
            root, GetSources, sessions.ToDictionary(s => s.Id), [root]);

        var slice = Assert.Single(slices);
        Assert.Equal(1787844986_000, slice.StartUnixMs);
        Assert.Equal(54_000, slice.DurationMs);
    }
}
