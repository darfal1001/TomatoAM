using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SamplePlugin;

public class KefkaTF : IDisposable
{

    private const uint StatusJestersAntics = 1486;
    private const uint StatusJestersTruths = 1487;


    private const uint FlagrantFireActionId = 0x28CE;

    private static readonly Dictionary<string, string> ElementNameLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Blizzard Blitz",    "ICE"     },
        { "Thrumming Thunder", "THUNDER" },
    };

    private static readonly Dictionary<uint, string> SpecialActions = new()
    {
        { 0x28D1, "MANA CHARGE"  },
        { 0x28D2, "MANA RELEASE" },
    };

    private readonly Dictionary<uint, DateTime> lastSeen = new();
    private const double DedupWindowSeconds = 5.0;


    private readonly Dictionary<string, DateTime> lastLabelAnnounced = new(StringComparer.OrdinalIgnoreCase);
    private const double LabelDedupWindowSeconds = 2.0;

    private string? lastElementResult = null;


    private readonly StreamWriter? castLogWriter;
    private readonly Dictionary<uint, DateTime> castLogLastSeen = new();
    private const double CastLogDedupSeconds = 1.0;


    private readonly string knownActionsPath;
    private readonly Dictionary<uint, KnownActionEntry> knownActions = new();

    private readonly IPluginLog   log;
    private readonly IObjectTable objectTable;
    private readonly IFramework   framework;
    private readonly IDataManager dataManager;
    private readonly IChatGui     chatGui;

    public KefkaTF(
        IPluginLog log,
        IObjectTable objectTable,
        IFramework framework,
        IDataManager dataManager,
        IChatGui chatGui)
    {
        this.log         = log;
        this.objectTable = objectTable;
        this.framework   = framework;
        this.dataManager = dataManager;
        this.chatGui     = chatGui;

        var desktop  = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        knownActionsPath = Path.Combine(desktop, "KefkaTF_KnownActions.txt");
        LoadKnownActions();

        try
        {
            var fileName = $"KefkaTF_CastLog_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
            var path     = Path.Combine(desktop, fileName);

            castLogWriter = new StreamWriter(path, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
            castLogWriter.WriteLine($"# KefkaTF cast log — session started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            castLogWriter.WriteLine("# time | actionId(hex) | actionId(dec) | actionName | casterName | casterStatuses");

            log.Info($"[KefkaTF] Cast log writing to: {path}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to open cast log file on Desktop.");
            castLogWriter = null;
        }

        framework.Update += OnUpdate;
        log.Info("[KefkaTF] Started.");
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            var now           = DateTime.UtcNow;
            var seenThisFrame = new HashSet<uint>();

            foreach (var obj in objectTable)
            {
                if (obj is not IBattleNpc npc)
                    continue;

                if (!npc.IsCasting)
                    continue;

                var id = npc.CastActionId;

                if (!seenThisFrame.Add(id))
                    continue;

                LogCastForAnalysis(id, npc);

                if (lastSeen.TryGetValue(id, out var last)
                    && (now - last).TotalSeconds < DedupWindowSeconds)
                    continue;

                lastSeen[id] = now;

                if (id == FlagrantFireActionId)
                {
                    AnnounceElement("FIRE", npc);
                    continue;
                }

                if (SpecialActions.TryGetValue(id, out var special))
                {
                    if (special == "MANA CHARGE")
                        lastElementResult = null; 

                    if (special == "MANA RELEASE" && lastElementResult != null)
                        Announce($"{special} - {lastElementResult}");
                    else
                        Announce(special);
                    continue;
                }

                var actionName = GetActionName(id);

                if (ElementNameLabel.TryGetValue(actionName, out var label))
                {
                    AnnounceElement(label, npc);
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] OnUpdate error");
        }
    }


    private void AnnounceElement(string label, IBattleNpc castingNpc)
    {
        var now = DateTime.UtcNow;

        if (lastLabelAnnounced.TryGetValue(label, out var lastAnnounced)
            && (now - lastAnnounced).TotalSeconds < LabelDedupWindowSeconds)
            return;

        lastLabelAnnounced[label] = now;


        var kefka = castingNpc.Name.TextValue == "Kefka"
            ? castingNpc
            : objectTable.OfType<IBattleNpc>().FirstOrDefault(n => n.Name.TextValue == "Kefka");

        bool isFake = false;
        bool isReal = false;

        if (kefka != null)
        {
            foreach (var status in kefka.StatusList)
            {
                if (status == null)
                    continue;

                if (status.StatusId == StatusJestersAntics)
                    isFake = true;
                else if (status.StatusId == StatusJestersTruths)
                    isReal = true;
            }
        }

        if (isFake)
        {
            Announce($"{label} — FAKE");
            lastElementResult = $"{label} FAKE";
        }
        else if (isReal)
        {
            Announce($"{label} — REAL");
            lastElementResult = $"{label} REAL";
        }
        else
        {

            log.Info($"[KefkaTF] UNEXPECTED {label} cast with no Jester's status found.");
            if (kefka != null)
            {
                foreach (var status in kefka.StatusList)
                {
                    if (status == null) continue;
                    log.Info($"[KefkaTF]   statusId={status.StatusId} name=\"{status.GameData.Value.Name}\"");
                }
            }
        }
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
            var actionName = GetActionName(id);
            var statuses   = string.Join(", ", npc.StatusList
                .Where(s => s != null)
                .Select(s => $"{s.StatusId}:{s.GameData.Value.Name}"));

            castLogWriter.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} | 0x{id:X} | {id} | \"{actionName}\" | \"{npc.Name}\" | {statuses}");

            RecordKnownAction(id, npc);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to write cast log line.");
        }
    }

    private void Announce(string message)
    {
        log.Info($"[TomatoAM] {message}");
    }

    private string GetActionName(uint id)
    {
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (sheet != null && sheet.TryGetRow(id, out var row))
        {
            var name = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return "???";
    }

    public void ResetPull()
    {
        lastSeen.Clear();
        lastLabelAnnounced.Clear();
        lastElementResult = null;
        Announce("Pull reset.");
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        castLogWriter?.Flush();
        castLogWriter?.Dispose();
        SaveKnownActions();
    }



    private record KnownActionEntry(uint Id, string Name, string Casters, string FirstSeen, string LastSeen)
    {
        public string LastSeen { get; set; } = LastSeen;
        public string Casters  { get; set; } = Casters;
    }

    private void LoadKnownActions()
    {
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

                var name      = parts[2].Trim().Trim('"');
                var casters   = parts[3].Trim();
                var firstSeen = parts[4].Trim();
                var lastSeen  = parts[5].Trim();

                knownActions[id] = new KnownActionEntry(id, name, casters, firstSeen, lastSeen);
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
            {
                writer.WriteLine(
                    $"0x{entry.Id:X8} | {entry.Id,10} | \"{entry.Name}\" | {entry.Casters} | {entry.FirstSeen} | {entry.LastSeen}");
            }
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