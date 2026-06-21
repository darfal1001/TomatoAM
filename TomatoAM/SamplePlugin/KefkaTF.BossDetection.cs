using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TomatoAM;


public partial class KefkaTF
{

    private IBattleNpc? FindBoss(IBattleNpc castingNpc)
    {
        if (KnownBossNames.Contains(castingNpc.Name.TextValue))
            return castingNpc;

        return objectTable
            .OfType<IBattleNpc>()
            .FirstOrDefault(n => n.IsValid() && KnownBossNames.Contains(n.Name.TextValue));
    }


    private void AnnounceElement(string label, IBattleNpc castingNpc)
        => AnnounceElementCore(label, castingNpc, fallbackToPlainAnnounce: false);


    private void AnnounceElementOrReal(string label, IBattleNpc castingNpc)
        => AnnounceElementCore(label, castingNpc, fallbackToPlainAnnounce: true);

    private void AnnounceElementCore(string label, IBattleNpc castingNpc, bool fallbackToPlainAnnounce)
    {
        var now = DateTime.UtcNow;

        if (lastLabelAnnounced.TryGetValue(label, out var lastAnnounced)
            && (now - lastAnnounced).TotalSeconds < LabelDedupWindowSeconds)
            return;

        var boss = FindBoss(castingNpc);

        if (boss == null)
        {
            if (fallbackToPlainAnnounce)
            {
                lastLabelAnnounced[label] = now;
                Announce(label);
                lastElementResults.Add(label);
            }
            return;
        }

        bool isFake      = false;
        bool isReal      = false;
        bool isCharged   = false;
        var  chargedList = new List<string>();

        foreach (var status in boss.StatusList)
        {
            if (status == null) continue;
            switch (status.StatusId)
            {
                case StatusJestersAntics:  isFake    = true;           break;
                case StatusJestersTruths:  isReal    = true;           break;
                case StatusManaCharge:     isCharged = true;           break;
                case StatusFireCharged:    chargedList.Add("FIRE");    break;
                case StatusIceCharged:     chargedList.Add("ICE");     break;
                case StatusThunderCharged: chargedList.Add("THUNDER"); break;
            }
        }


        if (!isFake && !isReal && !isCharged)
        {
            if (fallbackToPlainAnnounce)
            {
                lastLabelAnnounced[label] = now;
                Announce(label);
                lastElementResults.Add(label);
            }
            return;
        }

        lastLabelAnnounced[label] = now;

        if (isCharged && chargedList.Count > 1)
        {
            var elements = string.Join(" + ", chargedList);
            Announce($"{label} — CHARGED [{elements}]");
            foreach (var e in chargedList)
                lastElementResults.Add($"{e} CHARGED");
            return;
        }

        if (isFake)
        {
            Announce($"{label} — FAKE");
            lastElementResults.Add($"{label} FAKE");
        }
        else if (isReal)
        {
            Announce($"{label} — REAL");
            lastElementResults.Add($"{label} REAL");
        }
    }
}
