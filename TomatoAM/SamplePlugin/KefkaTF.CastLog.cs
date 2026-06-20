using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SamplePlugin;


public partial class KefkaTF
{
    private const double CastLogDedupSeconds = 1.0;

    private StreamWriter?                       castLogWriter;
    private readonly Dictionary<uint, DateTime> castLogLastSeen = new();

    private string                                      knownActionsPath = string.Empty;
    private readonly Dictionary<uint, KnownActionEntry> knownActions = new();

    private record KnownActionEntry(uint Id, string Name, string Casters, string FirstSeen, string LastSeen)
    {
        public string LastSeen { get; set; } = LastSeen;
        public string Casters  { get; set; } = Casters;
    }

    private void InitializeCastLog(string desktop)
    {
        try
        {
            var path = Path.Combine(desktop, "KefkaTF_CastLog.txt");

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            castLogWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            castLogWriter.WriteLine();
            castLogWriter.WriteLine($"# session started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            castLogWriter.WriteLine("# time | actionId(hex) | actionId(dec) | actionName | casterName | cast=elapsed/total | casterStatuses=[...] | bossStatuses=[...]");
            castLogWriter.WriteLine("# time | PHASE4-DETECT | playerName (slot N) | debuffLabel | REAL/FAKE \u2192 resolution   (detection only — no marker assigned)");


            log.Info($"[KefkaTF] Cast log writing to: {path}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to open cast log file on Desktop.");
            castLogWriter = null;
        }
    }

    private void DisposeCastLog()
    {
        castLogWriter?.Flush();
        castLogWriter?.Dispose();
        SaveKnownActions();
    }

    private void LogCastForAnalysis(uint id, IBattleNpc npc)
    {
        if (castLogWriter == null)
            return;

        var now = DateTime.UtcNow;
        var key = id ^ (uint)npc.GameObjectId.GetHashCode();

        if (castLogLastSeen.TryGetValue(key, out var last)
            && (now - last).TotalSeconds < CastLogDedupSeconds)
            return;

        castLogLastSeen[key] = now;

        try
        {
            var actionName     = GetActionName(id);
            var castProgress   = npc.IsCasting ? $"{npc.CurrentCastTime:F2}/{npc.TotalCastTime:F2}s" : "not casting";
            var casterStatuses = string.Join(", ", npc.StatusList
                .Where(s => s != null)
                .Select(s => $"{s.StatusId}:{s.GameData.Value.Name}"));

            var boss = FindBoss(npc);

            var bossStatuses = boss != null
                ? string.Join(", ", boss.StatusList
                    .Where(s => s != null)
                    .Select(s => $"{s.StatusId}:{s.GameData.Value.Name}"))
                : "boss not found";

            castLogWriter.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} | 0x{id:X} | {id} | \"{actionName}\" | \"{npc.Name}\" | cast={castProgress} | casterStatuses=[{casterStatuses}] | bossStatuses=[{bossStatuses}]");

            RecordKnownAction(id, npc);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to write cast log line.");
        }
    }

    private void LoadKnownActions(string desktop)
    {
        knownActionsPath = Path.Combine(desktop, "KefkaTF_KnownActions.txt");

        if (!File.Exists(knownActionsPath))
            return;

        try
        {
            foreach (var line in File.ReadAllLines(knownActionsPath, Encoding.UTF8))
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split('|');
                if (parts.Length < 6)
                    continue;

                if (!uint.TryParse(parts[1].Trim(), out var id))
                    continue;

                knownActions[id] = new KnownActionEntry(
                    id,
                    parts[2].Trim().Trim('"'),
                    parts[3].Trim(),
                    parts[4].Trim(),
                    parts[5].Trim());
            }

            log.Info($"[KefkaTF] Loaded {knownActions.Count} known actions from catalogue.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to load known actions catalogue.");
        }
    }

    private void SaveKnownActions()
    {
        try
        {
            using var writer = new StreamWriter(knownActionsPath, append: false, Encoding.UTF8);
            writer.WriteLine("# KefkaTF — persistent action catalogue (updated every session)");
            writer.WriteLine("# actionId(hex) | actionId(dec) | actionName | knownCasters | firstSeen | lastSeen");
            writer.WriteLine($"# last saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {knownActions.Count} entries");
            writer.WriteLine();

            foreach (var entry in knownActions.Values.OrderBy(e => e.Id))
                writer.WriteLine($"0x{entry.Id:X8} | {entry.Id,10} | \"{entry.Name}\" | {entry.Casters} | {entry.FirstSeen} | {entry.LastSeen}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to save known actions catalogue.");
        }
    }

    private void RecordKnownAction(uint id, IBattleNpc npc)
    {
        var now        = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var casterName = npc.Name.TextValue;
        var actionName = GetActionName(id);

        if (knownActions.TryGetValue(id, out var existing))
        {
            existing.LastSeen = now;
            if (!existing.Casters.Contains(casterName, StringComparison.OrdinalIgnoreCase))
                existing.Casters += $", {casterName}";
        }
        else
        {
            knownActions[id] = new KnownActionEntry(id, actionName, casterName, now, now);
            log.Info($"[KefkaTF] New action discovered: 0x{id:X} \"{actionName}\" cast by \"{casterName}\"");
        }
    }
}