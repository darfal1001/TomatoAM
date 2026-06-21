using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;

namespace TomatoAM;


public partial class KefkaTF : IDisposable
{
    private readonly Dictionary<uint, DateTime>   lastSeen           = new();
    private readonly Dictionary<string, DateTime> lastLabelAnnounced = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>                 lastElementResults = new();

    private const double DedupWindowSeconds      = 5.0;
    private const double LabelDedupWindowSeconds = 2.0;

    private readonly IPluginLog   log;
    private readonly IObjectTable objectTable;
    private readonly IFramework   framework;
    private readonly IDataManager dataManager;
    private readonly IChatGui     chatGui;
    private readonly IPartyList   partyList;

    public KefkaTF(
        IPluginLog log,
        IObjectTable objectTable,
        IFramework framework,
        IDataManager dataManager,
        IChatGui chatGui,
        IPartyList partyList,
        ISigScanner sigScanner)
    {
        this.log         = log;
        this.objectTable = objectTable;
        this.framework   = framework;
        this.dataManager = dataManager;
        this.chatGui     = chatGui;
        this.partyList   = partyList;

        InitializePhase4Markers(sigScanner);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        LoadKnownActions(desktop);
        InitializeCastLog(desktop);

        framework.Update += OnUpdate;
        log.Info("[KefkaTF] Started.");
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            UpdatePhase4Scan();

            var now           = DateTime.UtcNow;
            var seenThisFrame = new HashSet<uint>();

            foreach (var obj in objectTable)
            {
                if (obj is null || !obj.IsValid())
                    continue;

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

                if (!Actions.TryGetValue(id, out var def))
                    continue;

                switch (def.Kind)
                {
                    case ActionKind.Element:
                        AnnounceElement(def.Label, npc);
                        break;

                    case ActionKind.ElementOrReal:
                        AnnounceElementOrReal(def.Label, npc);
                        break;

                    case ActionKind.ManaCharge:
                        lastElementResults.Clear();
                        Announce(def.Label);
                        break;

                    case ActionKind.ManaRelease:
                        if (lastElementResults.Count > 0)
                            Announce($"{def.Label} - {string.Join(" + ", lastElementResults)}");
                    else
                        Announce(def.Label);
                    break;

                    case ActionKind.AlwaysReal:
                        Announce(def.Label);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] OnUpdate error");
        }
    }

    private void Announce(string message) => log.Info($"[TomatoAM] {message}");

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
        lastElementResults.Clear();
        phase4Marks.Clear();
        phase4ClaimedMarks.Clear();
        phase4AlreadyLoggedThisCycle = false;
        Announce("Pull reset.");
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        DisposeCastLog();
    }
}
