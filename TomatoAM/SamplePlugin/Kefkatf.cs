using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;

namespace SamplePlugin;

public class KefkaTF : IDisposable
{
    // ── Name-based classification ─────────────────────────────────
    // Instead of maintaining an ID table, we look up the action name
    // from the game sheet and classify by name. This handles all ID
    // variants automatically regardless of patch changes.
    //
    // Each entry: action name (lowercase) → (element, reality)
    // Where multiple IDs share the same name, the FIRST one seen per
    // pull is REAL, subsequent ones (within ~1s) are FAKE.
    private static readonly Dictionary<string, string> NameToElement = new(StringComparer.OrdinalIgnoreCase)
    {
        { "blizzard blitz",    "ICE"     },
        { "thrumming thunder", "THUNDER" },
        { "flagrant fire",     "FIRE"    },
    };

    // Track first-seen timestamp per element to split REAL vs FAKE:
    // if two same-element casts fire within 2s of each other, the
    // second is FAKE. If they're far apart, both are REAL (new wave).
    private readonly Dictionary<string, DateTime> lastElementSeen = new();
    private const double FakeWindowSeconds = 2.0;

    // ── Mana Charge tracking ──────────────────────────────────────
    private const uint ManaChargeId  = 0x28D1;
    private const uint ManaReleaseId = 0x28D2;

    private bool   manaIsCharged      = false;
    private string? lastChargedElement = null;

    // ── State ─────────────────────────────────────────────────────
    private readonly IPluginLog     log;
    private readonly IObjectTable   objectTable;
    private readonly IFramework     framework;
    private readonly IDataManager   dataManager;

    // Dedupe per action ID per pull (prevents per-frame spam)
    private readonly HashSet<uint> seenThisPull = new();

    public KefkaTF(
        IPluginLog log,
        IObjectTable objectTable,
        IFramework framework,
        IDataManager dataManager)
    {
        this.log         = log;
        this.objectTable = objectTable;
        this.framework   = framework;
        this.dataManager = dataManager;

        framework.Update += OnUpdate;
        log.Info("[KefkaTF] Started.");
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            foreach (var obj in objectTable)
            {
                if (obj is not IBattleNpc npc)
                    continue;

                if (!npc.IsCasting)
                    continue;

                var id  = npc.CastActionId;
                var src = npc.Name.TextValue;

                if (!seenThisPull.Add(id))
                    continue;

                // ── Mana Charge ───────────────────────────────────
                if (id == ManaChargeId)
                {
                    manaIsCharged      = true;
                    lastChargedElement = null;
                    log.Info("[KefkaTF] 🔋 MANA CHARGE — element TBD...");
                    continue;
                }

                // ── Mana Release ──────────────────────────────────
                if (id == ManaReleaseId)
                {
                    var charged = lastChargedElement ?? "UNKNOWN";
                    log.Info($"[KefkaTF] 💥 MANA RELEASE — charged element: {charged}");
                    manaIsCharged      = false;
                    lastChargedElement = null;
                    continue;
                }

                // ── Classify by action name ───────────────────────
                var actionName = GetActionName(id);

                if (NameToElement.TryGetValue(actionName, out var element))
                {
                    var now     = DateTime.UtcNow;
                    var reality = "REAL";

                    if (lastElementSeen.TryGetValue(element, out var lastSeen)
                        && (now - lastSeen).TotalSeconds < FakeWindowSeconds)
                    {
                        reality = "FAKE";
                    }
                    lastElementSeen[element] = now;

                    if (manaIsCharged)
                        lastChargedElement = element;

                    log.Info($"[KefkaTF] ⚡ {element,-7} {reality,-4}  (0x{id:X4})  src=\"{src}\"");
                }
                else
                {
                    // Still log unknowns for discovery
                    log.Info($"[KefkaTF] ❓ (0x{id:X4}) \"{actionName}\"  src=\"{src}\"");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] OnUpdate error");
        }
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
        seenThisPull.Clear();
        lastElementSeen.Clear();
        manaIsCharged      = false;
        lastChargedElement = null;
        log.Info("[KefkaTF] Pull reset.");
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
    }
}