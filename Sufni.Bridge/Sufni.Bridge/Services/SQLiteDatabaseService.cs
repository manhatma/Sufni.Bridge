using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using SQLite;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.Services;

public class SqLiteDatabaseService : IDatabaseService
{
    private Task Initialization { get; }
    private readonly SQLiteAsyncConnection connection;

    private static readonly List<CalibrationMethod> DefaultCalibrationMethods =
    [
        new(CalibrationMethod.FractionId,
            "fraction",
            "Sample is in fraction of maximum suspension stroke.",
            new CalibrationMethodProperties([], [], "sample * MAX_STROKE")),
        new(CalibrationMethod.PercentageId,
            "percentage",
            "Sample is in percentage of maximum suspension stroke.",
            new CalibrationMethodProperties(
                [],
                new Dictionary<string, string>()
                {
                    {"factor", "MAX_STROKE / 100.0"}
                },
                "sample * factor")),
        new(CalibrationMethod.LinearId,
            "linear",
            "Sample is linearly distributed within a given range.",
            new CalibrationMethodProperties(
                [
                    "min_measurement",
                    "max_measurement"
                ],
                new Dictionary<string, string>()
                {
                    {"factor", "MAX_STROKE / (max_measurement - min_measurement)"}
                },
                "(sample - min_measurement) * factor")),
        new(CalibrationMethod.LinearPotmeterId,
            "linear-potmeter",
            "Sample is the ADC value read from a linear potentiometer.",
            new CalibrationMethodProperties(
                [
                    "stroke",
                    "resolution"
                ],
                new Dictionary<string, string>()
                {
                    {"factor", "stroke / (2^resolution)"}
                },
                "sample * factor")),
        new(CalibrationMethod.IsoscelesId,
            "as5600-isosceles-triangle",
            "Triangle setup with the sensor between the base and leg.",
            new CalibrationMethodProperties(
                [
                    "arm",
                    "max"
                ],
                new Dictionary<string, string>()
                {
                    {"start_angle", "acos(max / 2.0 / arm)"},
                    {"factor", "2.0 * pi / 4096"},
                    {"dbl_arm", "2.0 * arm"},
                },
                "max - (dbl_arm * cos((factor*sample) + start_angle))")),
        new(CalibrationMethod.TriangleId,
            "as5600-triangle",
            "Triangle setup with the sensor between two known sides.",
            new CalibrationMethodProperties(
                [
                    "arm1",
                    "arm2",
                    "max"
                ],
                new Dictionary<string, string>()
                {
                    {"start_angle", "acos((arm1^2+arm2^2-max^2)/(2*arm1*arm2))"},
                    {"factor", "2.0 * pi / 4096"},
                    {"arms_sqr_sum", "arm1^2 + arm2^2"},
                    {"dbl_arm1_arm2", "2 * arm1 * arm2"},
                },
                "max - sqrt(arms_sqr_sum - dbl_arm1_arm2 * cos(start_angle-(factor*sample)))")),
    ];

    public SqLiteDatabaseService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sufni.Bridge");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        connection = new SQLiteAsyncConnection(Path.Combine(dir, "sst.db"));
        Initialization = Init();
    }

    private async Task Init()
    {
        if (connection == null)
        {
            throw new Exception("Database connection failed!");
        }

        await connection.EnableWriteAheadLoggingAsync();
        var result = await connection.CreateTablesAsync(CreateFlags.None, new[]
        {
            typeof(Board),
            typeof(Linkage),
            typeof(CalibrationMethod),
            typeof(Calibration),
            typeof(Setup),
            typeof(Session),
            typeof(SessionCache),
            typeof(Synchronization),
            typeof(CombinedSessionSource),
            typeof(PendingSetupChanges)
        });

        if (result.Results[typeof(CalibrationMethod)] == CreateTableResult.Created)
        {
            var timestamp = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            foreach (var calibrationMethod in DefaultCalibrationMethods)
            {
                calibrationMethod.Updated = timestamp;
            }
            await connection.InsertAllAsync(DefaultCalibrationMethods);
        }
        if (result.Results[typeof(Synchronization)] == CreateTableResult.Created)
        {
            await connection.QueryAsync<Synchronization>("INSERT INTO sync VALUES (0)");
        }

        await EnsureSessionColumns();
        await EnsureSessionCacheColumns();
        await EnsureSetupColumns();
        await EnsureDefaultCalibrationMethods();
    }

    private async Task EnsureSetupColumns()
    {
        var tableInfo = await connection.QueryAsync<TableInfoRecord>("PRAGMA table_info(setup)");
        var columnNames = tableInfo.Select(column => column.Name).ToHashSet();
        if (!columnNames.Contains("discipline"))
            await connection.ExecuteAsync("ALTER TABLE setup ADD COLUMN discipline INTEGER DEFAULT 1");

        // One-time backfill: classify legacy setups by name. Heuristic: names
        // containing "exc" → Downhill (id=2), everything else → Enduro (id=1).
        // Gated on PRAGMA user_version so it never runs twice.
        var userVersion = await connection.ExecuteScalarAsync<int>("PRAGMA user_version");
        if (userVersion < 1)
        {
            await connection.ExecuteAsync(
                "UPDATE setup SET discipline = 2 WHERE LOWER(name) LIKE '%exc%'");
            await connection.ExecuteAsync(
                "UPDATE setup SET discipline = 1 WHERE LOWER(name) NOT LIKE '%exc%'");
            await connection.ExecuteAsync("PRAGMA user_version = 1");
        }

        // Wheel-load component IDs and mass/IMU hooks. All nullable, default NULL,
        // so existing setups remain unchanged and wheel-load metrics stay opt-in.
        async Task AddColumnIfMissing(string name, string type)
        {
            if (!columnNames.Contains(name))
                await connection.ExecuteAsync($"ALTER TABLE setup ADD COLUMN {name} {type}");
        }
        await AddColumnIfMissing("front_spring_component", "TEXT");
        await AddColumnIfMissing("front_damper_component", "TEXT");
        await AddColumnIfMissing("rear_spring_component", "TEXT");
        await AddColumnIfMissing("rear_damper_component", "TEXT");
        await AddColumnIfMissing("front_unsprung_mass_kg", "REAL");
        await AddColumnIfMissing("rear_unsprung_mass_kg", "REAL");
        await AddColumnIfMissing("rear_linkage_effectiveness", "REAL");
        await AddColumnIfMissing("total_sprung_mass_kg", "REAL");
        await AddColumnIfMissing("imu_config", "TEXT");
    }

    private async Task EnsureDefaultCalibrationMethods()
    {
        var existing = await connection.Table<CalibrationMethod>().ToListAsync();
        var existingIds = existing.Select(m => m.Id).ToHashSet();
        var timestamp = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        foreach (var method in DefaultCalibrationMethods)
        {
            if (!existingIds.Contains(method.Id))
            {
                method.Updated = timestamp;
                await connection.InsertAsync(method);
            }
        }
    }

    private async Task EnsureSessionColumns()
    {
        var tableInfo = await connection.QueryAsync<TableInfoRecord>("PRAGMA table_info(session)");
        var columnNames = tableInfo.Select(column => column.Name).ToHashSet();
        async Task AddColumnIfMissing(string name, string type = "INTEGER")
        {
            if (!columnNames.Contains(name))
                await connection.ExecuteAsync($"ALTER TABLE session ADD COLUMN {name} {type} DEFAULT 0");
        }
        await AddColumnIfMissing("front_volspc");
        await AddColumnIfMissing("rear_volspc");
        await AddColumnIfMissing("source_id", "TEXT");
        await AddColumnIfMissing("crop_start_sample", "INTEGER");
        await AddColumnIfMissing("crop_end_sample", "INTEGER");
        await AddColumnIfMissing("front_tire_pressure", "REAL");
        await AddColumnIfMissing("rear_tire_pressure", "REAL");
        await AddColumnIfMissing("duration_seconds", "INTEGER");
    }

    private async Task EnsureSessionCacheColumns()
    {
        var tableInfo = await connection.QueryAsync<TableInfoRecord>("PRAGMA table_info(session_cache)");
        var columnNames = tableInfo.Select(column => column.Name).ToHashSet();
        async Task AddColumnIfMissing(string name, string type = "TEXT")
        {
            if (!columnNames.Contains(name))
                await connection.ExecuteAsync($"ALTER TABLE session_cache ADD COLUMN {name} {type} DEFAULT 0");
        }
        await AddColumnIfMissing("travel_comparison_histogram");
        await AddColumnIfMissing("front_rear_travel_scatter");
        await AddColumnIfMissing("front_position_distribution");
        await AddColumnIfMissing("rear_position_distribution");
        await AddColumnIfMissing("front_velocity_distribution");
        await AddColumnIfMissing("rear_velocity_distribution");
        await AddColumnIfMissing("front_position_velocity");
        await AddColumnIfMissing("rear_position_velocity");
        await AddColumnIfMissing("velocity_distribution_comparison");
        await AddColumnIfMissing("position_velocity_comparison");
        await AddColumnIfMissing("summary_json");
        await AddColumnIfMissing("plot_version", "INTEGER");
        await AddColumnIfMissing("combined_balance");
        await AddColumnIfMissing("crop_start_sample", "INTEGER");
        await AddColumnIfMissing("crop_end_sample", "INTEGER");
        await AddColumnIfMissing("travel_time_history");
        await AddColumnIfMissing("travel_time_cropped");
        await AddColumnIfMissing("velocity_time_cropped");
        await AddColumnIfMissing("acceleration_time_cropped");
        await AddColumnIfMissing("front_travel_time_cropped");
        await AddColumnIfMissing("rear_travel_time_cropped");
        await AddColumnIfMissing("front_velocity_time_cropped");
        await AddColumnIfMissing("rear_velocity_time_cropped");
        await AddColumnIfMissing("combined_travel_fft");
        await AddColumnIfMissing("combined_travel_fft_high");
        await AddColumnIfMissing("combined_velocity_fft");
        await AddColumnIfMissing("balance_metrics_json");
    }

    private class TableInfoRecord
    {
        public string Name { get; set; } = string.Empty;
    }

    public async Task<List<Board>> GetBoardsAsync()
    {
        await Initialization;

        return await connection.Table<Board>().Where(b => b.Deleted == null).ToListAsync();
    }

    public async Task<List<Board>> GetChangedBoardsAsync(int since)
    {
        await Initialization;

        return await connection.Table<Board>()
            .Where(b => b.Updated > since || (b.Deleted != null && b.Deleted > since))
            .ToListAsync();
    }

    public async Task PutBoardAsync(Board board)
    {
        await Initialization;

        board.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        var existing = await connection.Table<Board>()
            .Where(b => b.Id == board.Id && b.Deleted == null)
            .FirstOrDefaultAsync() is not null;
        if (existing)
        {
            await connection.UpdateAsync(board);
        }
        else
        {
            await connection.InsertAsync(board);
        }
    }

    public async Task DeleteBoardAsync(string id)
    {
        await Initialization;
        var board = await connection.Table<Board>()
            .Where(b => b.Id == id)
            .FirstOrDefaultAsync();
        if (board is not null)
        {
            board.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            await connection.UpdateAsync(board);
        }
    }

    public async Task<List<Linkage>> GetLinkagesAsync()
    {
        await Initialization;

        return await connection.Table<Linkage>().Where(t => t.Deleted == null).ToListAsync();
    }

    public async Task<List<Linkage>> GetChangedLinkagesAsync(int since)
    {
        await Initialization;

        return await connection.Table<Linkage>()
            .Where(l => l.Updated > since || (l.Deleted != null && l.Deleted > since))
            .ToListAsync();
    }

    public async Task<Linkage?> GetLinkageAsync(Guid id)
    {
        await Initialization;

        return await connection.Table<Linkage>()
            .Where(l => l.Id == id && l.Deleted == null)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> PutLinkageAsync(Linkage linkage)
    {
        await Initialization;

        var existing = await connection.Table<Linkage>()
            .Where(l => l.Id == linkage.Id && l.Deleted == null)
            .FirstOrDefaultAsync() is not null;
        linkage.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        if (existing)
        {
            await connection.UpdateAsync(linkage);
        }
        else
        {
            await connection.InsertAsync(linkage);
        }

        return linkage.Id;
    }

    public async Task DeleteLinkageAsync(Guid id)
    {
        await Initialization;
        var linkage = await connection.Table<Linkage>()
            .Where(l => l.Id == id)
            .FirstOrDefaultAsync();
        if (linkage is not null)
        {
            linkage.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            await connection.UpdateAsync(linkage);
        }
    }

    public async Task<List<CalibrationMethod>> GetCalibrationMethodsAsync()
    {
        await Initialization;
        return await connection.Table<CalibrationMethod>().Where(c => c.Deleted == null).ToListAsync();
    }

    public async Task<List<CalibrationMethod>> GetChangedCalibrationMethodsAsync(int since)
    {
        await Initialization;

        return await connection.Table<CalibrationMethod>()
            .Where(c => c.Updated > since || (c.Deleted != null && c.Deleted > since))
            .ToListAsync();
    }

    public async Task<CalibrationMethod?> GetCalibrationMethodAsync(Guid id)
    {
        await Initialization;

        return await connection.Table<CalibrationMethod>()
            .Where(c => c.Id == id && c.Deleted == null)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> PutCalibrationMethodAsync(CalibrationMethod calibrationMethod)
    {
        await Initialization;

        var existing = await connection.Table<CalibrationMethod>()
            .Where(c => c.Id == calibrationMethod.Id && c.Deleted == null)
            .FirstOrDefaultAsync() is not null;
        calibrationMethod.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        if (existing)
        {
            await connection.UpdateAsync(calibrationMethod);
        }
        else
        {
            await connection.InsertAsync(calibrationMethod);
        }

        return calibrationMethod.Id;
    }

    public async Task DeleteCalibrationMethodAsync(Guid id)
    {
        await Initialization;
        var calibrationMethod = await connection.Table<CalibrationMethod>()
            .Where(c => c.Id == id)
            .FirstOrDefaultAsync();
        if (calibrationMethod is not null)
        {
            calibrationMethod.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            await connection.UpdateAsync(calibrationMethod);
        }
    }

    public async Task<List<Calibration>> GetCalibrationsAsync()
    {
        await Initialization;
        return await connection.Table<Calibration>().Where(c => c.Deleted == null).ToListAsync();
    }

    public async Task<List<Calibration>> GetChangedCalibrationsAsync(int since)
    {
        await Initialization;

        return await connection.Table<Calibration>()
            .Where(c => c.Updated > since || (c.Deleted != null && c.Deleted > since))
            .ToListAsync();
    }

    public async Task<Calibration?> GetCalibrationAsync(Guid id)
    {
        await Initialization;

        return await connection.Table<Calibration>()
            .Where(c => c.Id == id && c.Deleted == null)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> PutCalibrationAsync(Calibration calibration)
    {
        await Initialization;

        var existing = await connection.Table<Calibration>()
            .Where(c => c.Id == calibration.Id && c.Deleted == null)
            .FirstOrDefaultAsync() is not null;
        calibration.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        if (existing)
        {
            await connection.UpdateAsync(calibration);
        }
        else
        {
            await connection.InsertAsync(calibration);
        }

        return calibration.Id;
    }

    public async Task DeleteCalibrationAsync(Guid id)
    {
        await Initialization;
        var calibration = await connection.Table<Calibration>()
            .Where(c => c.Id == id)
            .FirstOrDefaultAsync();
        if (calibration is not null)
        {
            calibration.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            await connection.UpdateAsync(calibration);
        }
    }

    public async Task<List<Setup>> GetSetupsAsync()
    {
        await Initialization;
        return await connection.Table<Setup>().Where(s => s.Deleted == null).ToListAsync();
    }

    public async Task<List<Setup>> GetChangedSetupsAsync(int since)
    {
        await Initialization;

        return await connection.Table<Setup>()
            .Where(s => s.Updated > since || (s.Deleted != null && s.Deleted > since))
            .ToListAsync();
    }

    public async Task<Setup?> GetSetupAsync(Guid id)
    {
        await Initialization;

        return await connection.Table<Setup>()
            .Where(s => s.Id == id && s.Deleted == null)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> PutSetupAsync(Setup setup)
    {
        await Initialization;

        var existing = await connection.Table<Setup>()
            .Where(s => s.Id == setup.Id && s.Deleted == null)
            .FirstOrDefaultAsync() is not null;
        setup.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        if (existing)
        {
            await connection.UpdateAsync(setup);
        }
        else
        {
            await connection.InsertAsync(setup);
        }

        return setup.Id;
    }

    public async Task DeleteSetupAsync(Guid id)
    {
        await Initialization;
        var setup = await connection.Table<Setup>()
            .Where(s => s.Id == id)
            .FirstOrDefaultAsync();
        if (setup is not null)
        {
            setup.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            await connection.UpdateAsync(setup);
        }
    }

    public async Task<List<Session>> GetSessionsAsync()
    {
        await Initialization;

        const string query = """
                             SELECT
                                 id,
                                 name,
                                 setup_id,
                                 description,
                                 timestamp,
                                 track_id,
                                 front_springrate, front_volspc, front_hsc, front_lsc, front_lsr, front_hsr,
                                 front_tire_pressure,
                                 rear_springrate, rear_volspc, rear_hsc, rear_lsc, rear_lsr, rear_hsr,
                                 rear_tire_pressure,
                                 crop_start_sample, crop_end_sample,
                                 duration_seconds,
                                 CASE
                                    WHEN data IS NOT NULL THEN 1
                                    ELSE 0
                                 END AS has_data
                             FROM
                                 session
                             WHERE
                                 deleted IS NULL
                             ORDER BY timestamp DESC
                             """;
        var sessions = await connection.QueryAsync<Session>(query);
        return sessions;
    }

    public async Task<List<Guid>> GetIncompleteSessionIdsAsync()
    {
        await Initialization;

        const string query = "SELECT id FROM session WHERE deleted IS null AND data IS null";
        return (await connection.QueryAsync<Session>(query)).Select(s => s.Id).ToList();
    }

    public async Task<List<Session>> GetChangedSessionsAsync(int since)
    {
        await Initialization;

        return await connection.Table<Session>()
            .Where(s => s.Updated > since || (s.Deleted != null && s.Deleted > since))
            .ToListAsync();
    }

    public async Task<TelemetryData?> GetSessionPsstAsync(Guid id)
    {
        await Initialization;
        var sessions = await connection.QueryAsync<Session>(
            "SELECT data FROM session WHERE deleted IS null AND id = ?", id);
        if (sessions.Count != 1) return null;

        var td = MessagePackSerializer.Deserialize<TelemetryData>(sessions[0].ProcessedData);
        if (td.ProcessingVersion < TelemetryData.CurrentProcessingVersion)
        {
            var updatedBlob = td.ReprocessVelocity();
            await connection.ExecuteAsync("UPDATE session SET data=? WHERE id=?", [updatedBlob, id]);
        }

        return td;
    }

    public async Task<byte[]?> GetSessionRawPsstAsync(Guid id)
    {
        await Initialization;
        var sessions = await connection.QueryAsync<Session>(
            "SELECT data FROM session WHERE deleted IS null AND id = ?", id);
        return sessions.Count == 1 ? sessions[0].ProcessedData : null;
    }

    public async Task<Guid> PutSessionAsync(Session session)
    {
        await Initialization;

        var existing = await connection.Table<Session>()
            .Where(s => s.Id == session.Id && s.Deleted == null)
            .FirstOrDefaultAsync() is not null;
        session.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        if (existing)
        {
            const string query = """
                                 UPDATE session
                                 SET
                                     name=?,
                                     description=?,
                                     front_springrate=?, front_volspc=?, front_hsc=?, front_lsc=?, front_lsr=?, front_hsr=?,
                                     front_tire_pressure=?,
                                     rear_springrate=?, rear_volspc=?, rear_hsc=?, rear_lsc=?, rear_lsr=?, rear_hsr=?,
                                     rear_tire_pressure=?,
                                     crop_start_sample=?, crop_end_sample=?,
                                     duration_seconds=?
                                 WHERE
                                     id=?
                                 """;
            await connection.ExecuteAsync(query,
                [
                    session.Name,
                    session.Description,
                    session.FrontSpringRate,
                    session.FrontVolSpc,
                    session.FrontHighSpeedCompression,
                    session.FrontLowSpeedCompression,
                    session.FrontLowSpeedRebound,
                    session.FrontHighSpeedRebound,
                    session.FrontTirePressure,
                    session.RearSpringRate,
                    session.RearVolSpc,
                    session.RearHighSpeedCompression,
                    session.RearLowSpeedCompression,
                    session.RearLowSpeedRebound,
                    session.RearHighSpeedRebound,
                    session.RearTirePressure,
                    session.CropStartSample,
                    session.CropEndSample,
                    session.DurationSeconds,
                    session.Id]);
        }
        else
        {
            await connection.InsertAsync(session);
        }

        return session.Id;
    }

    public async Task PatchSessionPsstAsync(Guid id, byte[] data)
    {
        await Initialization;

        var session = await connection.Table<Session>()
            .Where(s => s.Id == id && s.Deleted == null)
            .FirstOrDefaultAsync();
        if (session is null)
        {
            throw new Exception($"Session {id} does not exist.");
        }

        await connection.ExecuteAsync("UPDATE session SET data=? WHERE id=?", [data, id]);
    }

    public async Task DeleteSessionAsync(Guid id)
    {
        await Initialization;
        var session = await connection.Table<Session>()
            .Where(s => s.Id == id)
            .FirstOrDefaultAsync();
        if (session is not null)
        {
            session.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            await connection.UpdateAsync(session);
        }

        // Cascade: soft-delete any combined sessions that reference this session as a source
        var affectedCombinedIds = await connection.QueryAsync<CombinedSessionSource>(
            "SELECT DISTINCT combined_id FROM combined_session WHERE source_id = ?", id);
        foreach (var row in affectedCombinedIds)
        {
            var combined = await connection.Table<Session>()
                .Where(s => s.Id == row.CombinedId && s.Deleted == null)
                .FirstOrDefaultAsync();
            if (combined is not null)
            {
                combined.Deleted = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
                await connection.UpdateAsync(combined);
            }
        }
    }

    public async Task UndeleteAsync(Guid id, string table)
    {
        await Initialization;
        await connection.ExecuteAsync(
            $"UPDATE [{table}] SET deleted = NULL WHERE id = ?", id);
    }

    public async Task<bool> SessionExistsForTimestampAsync(int timestamp)
    {
        await Initialization;
        return await connection.Table<Session>()
            .Where(s => s.Deleted == null && s.Timestamp == timestamp)
            .FirstOrDefaultAsync() is not null;
    }

    public async Task<Session?> GetMostRecentSessionAsync()
    {
        await Initialization;
        const string query = """
                             SELECT
                                 description,
                                 front_springrate, front_volspc, front_hsc, front_lsc, front_lsr, front_hsr,
                                 front_tire_pressure,
                                 rear_springrate, rear_volspc, rear_hsc, rear_lsc, rear_lsr, rear_hsr,
                                 rear_tire_pressure
                             FROM session
                             WHERE deleted IS NULL
                             ORDER BY timestamp DESC
                             LIMIT 1
                             """;
        var results = await connection.QueryAsync<Session>(query);
        return results.Count == 1 ? results[0] : null;
    }

    public async Task<HashSet<string>> GetImportedSourceIdentifiersAsync()
    {
        await Initialization;
        var sessions = await connection.QueryAsync<Session>(
            "SELECT source_id FROM session WHERE source_id IS NOT NULL");
        return sessions
            .Where(s => s.SourceIdentifier != null)
            .Select(s => s.SourceIdentifier!)
            .ToHashSet();
    }

    public async Task<SessionCache?> GetSessionCacheAsync(Guid sessionId)
    {
        await Initialization;
        return await connection.Table<SessionCache>()
            .Where(s => s.SessionId == sessionId)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid> PutSessionCacheAsync(SessionCache sessionCache)
    {
        await Initialization;
        await connection.InsertOrReplaceAsync(sessionCache);
        return sessionCache.SessionId;
    }

    public async Task<int> ReassignSetupInSessionsAsync(Guid oldSetupId, Guid newSetupId)
    {
        await Initialization;

        var newSetup = await GetSetupAsync(newSetupId)
            ?? throw new Exception("Target setup not found.");
        var newLinkage = await GetLinkageAsync(newSetup.LinkageId)
            ?? throw new Exception("Target setup's linkage not found.");

        var sessions = await connection.QueryAsync<Session>(
            "SELECT id FROM session WHERE setup_id = ? AND deleted IS NULL AND data IS NOT NULL", oldSetupId);

        var count = 0;
        foreach (var session in sessions)
        {
            var rawData = await GetSessionRawPsstAsync(session.Id);
            if (rawData == null) continue;

            var td = MessagePackSerializer.Deserialize<TelemetryData>(rawData);
            td.Linkage = newLinkage;
            var newBlob = td.ReprocessVelocity();

            await connection.ExecuteAsync(
                "UPDATE session SET setup_id=?, data=? WHERE id=?",
                [newSetupId, newBlob, session.Id]);
            await connection.ExecuteAsync("DELETE FROM session_cache WHERE session_id=?", session.Id);
            count++;
        }

        return count;
    }

    public async Task ReassignSessionSetupAsync(Guid sessionId, Guid newSetupId)
    {
        await Initialization;

        var newSetup = await GetSetupAsync(newSetupId)
            ?? throw new Exception("Target setup not found.");
        var newLinkage = await GetLinkageAsync(newSetup.LinkageId)
            ?? throw new Exception("Target setup's linkage not found.");

        var rawData = await GetSessionRawPsstAsync(sessionId);
        if (rawData == null) return;

        var td = MessagePackSerializer.Deserialize<TelemetryData>(rawData);
        td.Linkage = newLinkage;
        var newBlob = td.ReprocessVelocity();

        await connection.ExecuteAsync(
            "UPDATE session SET setup_id=?, data=? WHERE id=?",
            [newSetupId, newBlob, sessionId]);
        await connection.ExecuteAsync("DELETE FROM session_cache WHERE session_id=?", sessionId);
    }

    public async Task<int> GetLastSyncTimeAsync()
    {
        await Initialization;

        var s = await connection.Table<Synchronization>().FirstOrDefaultAsync();
        return s?.LastSyncTime ?? 0;
    }

    public async Task UpdateLastSyncTimeAsync()
    {
        await Initialization;

        await connection.QueryAsync<Synchronization>("UPDATE sync SET last_sync_time = ?",
            (int)DateTimeOffset.Now.ToUnixTimeSeconds());
    }

    public async Task<List<Guid>> GetCombinedSourcesAsync(Guid combinedId)
    {
        await Initialization;
        var rows = await connection.Table<CombinedSessionSource>()
            .Where(r => r.CombinedId == combinedId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
        return rows.Select(r => r.SourceId).ToList();
    }

    public async Task<HashSet<Guid>> GetAllCombinedIdsAsync()
    {
        await Initialization;
        var rows = await connection.QueryAsync<CombinedSessionSource>(
            "SELECT DISTINCT combined_id FROM combined_session");
        return rows.Select(r => r.CombinedId).ToHashSet();
    }

    public async Task PutCombinedSourcesAsync(Guid combinedId, List<Guid> sourceIds)
    {
        await Initialization;
        for (var i = 0; i < sourceIds.Count; i++)
        {
            await connection.InsertAsync(new CombinedSessionSource
            {
                CombinedId = combinedId,
                SourceId = sourceIds[i],
                SortOrder = i
            });
        }
    }

    public async Task DeleteCombinedSourcesAsync(Guid combinedId)
    {
        await Initialization;
        await connection.ExecuteAsync("DELETE FROM combined_session WHERE combined_id = ?", combinedId);
    }

    public async Task BackfillDurationAsync()
    {
        await Initialization;
        var sessions = await connection.QueryAsync<Session>(
            "SELECT id, data FROM session WHERE deleted IS NULL AND data IS NOT NULL AND duration_seconds IS NULL");
        foreach (var s in sessions)
        {
            try
            {
                var td = MessagePackSerializer.Deserialize<TelemetryData>(s.ProcessedData);
                var sampleCount = Math.Max(td.Front.Travel?.Length ?? 0, td.Rear.Travel?.Length ?? 0);
                var duration = td.SampleRate > 0 ? sampleCount / td.SampleRate : 0;
                await connection.ExecuteAsync("UPDATE session SET duration_seconds=? WHERE id=?", duration, s.Id);
            }
            catch { /* skip sessions with corrupt data */ }
        }
    }

    public async Task<PendingSetupChanges?> GetPendingSetupChangesAsync(Guid setupId)
    {
        await Initialization;
        return await connection.Table<PendingSetupChanges>()
            .Where(p => p.SetupId == setupId)
            .FirstOrDefaultAsync();
    }

    public async Task PutPendingSetupChangesAsync(PendingSetupChanges pending)
    {
        await Initialization;
        pending.Updated = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
        await connection.InsertOrReplaceAsync(pending);
        PendingSetupChanges.RaiseChanged(pending.SetupId);
    }

    public async Task DeletePendingSetupChangesAsync(Guid setupId)
    {
        await Initialization;
        await connection.ExecuteAsync("DELETE FROM pending_setup_changes WHERE setup_id = ?", setupId);
        PendingSetupChanges.RaiseChanged(setupId);
    }
}
