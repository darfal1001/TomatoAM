using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TomatoAM;


public partial class KefkaTF
{
    // ---- Exdeath debuffs (players) ----
    private const uint StatusCompressedWater  = 5545; // TODO: real status ID
    private const uint StatusForkedLightning  = 5544; // TODO: real status ID
    private const uint StatusAccelerationBomb = 5546; // TODO: real status ID
    private const uint StatusCursedShriek     = 5543; // TODO: real status ID

    // ---- Chaos debuffs (players) ----
    private const uint StatusEntropy      = 5547; // TODO: real status ID
    private const uint StatusDynamicFluid = 5548; // TODO: real status ID

    // ---- Wound (assigned to everyone) ----
    private const uint StatusWhiteWound = 5541; // TODO: real status ID
    private const uint StatusBlackWound = 5542; // TODO: real status ID

    // ---- Field (4 + 4 split) ----
    private const uint StatusAllaganField = 0; // TODO: real status ID — "must SOLVE mechanics"
    private const uint StatusBeyondDeath  = 5464; // TODO: real status ID — "must FAIL mechanics"

    private enum ExdeathDebuff { CompressedWater, ForkedLightning, AccelerationBomb, CursedShriek }
    private enum ChaosDebuff   { Entropy, DynamicFluid }
    private enum WoundType     { White, Black }
    private enum FieldType     { AllaganField, BeyondDeath }


    private static readonly Dictionary<ExdeathDebuff, (string Real, string Fake)> ExdeathResolution = new()
    {
        { ExdeathDebuff.CompressedWater,  ("3 Player Stack", "Spread") },
        { ExdeathDebuff.ForkedLightning,  ("Spread", "3 Player Stack") },
        { ExdeathDebuff.AccelerationBomb, ("Stillness", "Motion") },
        { ExdeathDebuff.CursedShriek,     ("Gaze", "Inverted Gaze") },
    };

    private static readonly Dictionary<ChaosDebuff, (string Real, string Fake)> ChaosResolution = new()
    {
        { ChaosDebuff.Entropy,      ("Delayed AoE (twister)", "Delayed Donut") },
        { ChaosDebuff.DynamicFluid, ("Delayed Donut", "Delayed AoE (twister)") },
    };

    private enum MarkCategory { Exdeath, Chaos, Wound, Field }

    private record PlayerDebuffMark(
        string PlayerName,
        int Slot,
        bool IsSupport,
        MarkCategory Category,
        string DebuffLabel,
        string Resolution,
        bool IsReal);


    private readonly List<PlayerDebuffMark> phase4Marks = new();

    // Scanning runs every frame, all the time — no command needed to "start" it.
    // We only log once per application of debuffs (i.e. once everyone currently has
    // their assigned debuff at the same time), then wait for that to clear before
    // logging again on the next Kefka Says.
    private bool phase4AlreadyLoggedThisCycle;

    // Actual marker assignment/sending stays off for now — detection only logs what
    // it sees. Flip this once the detection logic has been verified against real pulls.
    private const bool Phase4MarkingEnabled = false;

    // Dedupes within a single frame's scan pass (e.g. if a status list ever contains
    // more than one entry mapping to the same category for a player).
    private readonly HashSet<(string Player, MarkCategory Category)> phase4ClaimedMarks = new();


    private static readonly string[] StackMarkers     = { "square", "circle" };
    private static readonly string[] SpreadMarkers    = { "attack1", "attack2", "attack3", "attack4" };
    private static readonly string[] GazeMarkers      = { "cross", "triangle" };
    private static readonly string[] StillnessMarkers = { "bind1", "bind2", "bind3" };
    private static readonly string[] WoundMarkers     = { "ignore1", "ignore2" };
    private static readonly string[] FieldMarkers     = { "attack5", "attack6" };


    private delegate void PostCommandDelegate(IntPtr uiModule, IntPtr cmd, IntPtr unk1, byte unk2);
    private PostCommandDelegate? postCommand;

    private void InitializePhase4Markers(ISigScanner sigScanner)
    {
        try
        {
            if (sigScanner.TryScanText(
                "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F2 48 8B F9 45 84 C9",
                out var ptr))
            {
                postCommand = Marshal.GetDelegateForFunctionPointer<PostCommandDelegate>(ptr);
            }
            else
            {
                log.Warning("[KefkaTF] Could not find PostCommand signature — head markers will be unavailable.");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to set up marker command sender.");
        }
    }

    private unsafe void SendSlashCommand(string text)
    {
        if (postCommand == null) return;

        var ui = (UIModule*)UIModule.Instance();

        var bytes = Encoding.UTF8.GetBytes(text);
        var buf   = stackalloc byte[400];
        var len   = Math.Min(bytes.Length, 398);
        bytes.AsSpan(0, len).CopyTo(new Span<byte>(buf, len));
        buf[len] = 0;

        var header = stackalloc byte[32];
        *(long*)(header + 0)  = (long)buf;
        *(long*)(header + 8)  = len;
        *(long*)(header + 16) = 64;
        *(long*)(header + 24) = 0;

        postCommand((IntPtr)ui, (IntPtr)header, IntPtr.Zero, 0);
    }


    public void TriggerPhase4Scan()
    {
        // Detection now runs every frame on its own — this just forces the "already
        // logged" latch open so the next pass logs immediately if the party currently
        // qualifies, handy for manually re-checking.
        phase4AlreadyLoggedThisCycle = false;
        log.Info("[KefkaTF] Phase 4 manual check requested.");
    }

    // Called every frame from OnUpdate, unconditionally. Rebuilds a fresh snapshot of
    // who currently has what, and logs once if everyone has their debuff at the same
    // time. If they don't (yet, or not anymore), it does nothing.
    private void UpdatePhase4Scan()
    {
        phase4Marks.Clear();
        phase4ClaimedMarks.Clear();

        ScanPlayerDebuffs();

        if (Phase4ScanComplete())
        {
            if (!phase4AlreadyLoggedThisCycle)
            {
                phase4AlreadyLoggedThisCycle = true;
                LogPhase4Detection();

                if (Phase4MarkingEnabled)
                    ApplyPhase4Markers();
            }
        }
        else
        {
            // Not everyone has their debuff right now (either it hasn't fully landed
            // yet, or the previous application has worn off) — reset so the next time
            // everyone lines up, we log again.
            phase4AlreadyLoggedThisCycle = false;
        }
    }

    // Wound and Field debuffs are assigned to every party member, so once every current
    // party member has one of each, we know the boss is done handing out debuffs.
    private bool Phase4ScanComplete()
    {
        var partyNames = partyList
            .Where(m => m != null)
            .Select(m => m.Name.TextValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (partyNames.Count == 0)
            return false;

        var withWound = phase4Marks
            .Where(m => m.Category == MarkCategory.Wound)
            .Select(m => m.PlayerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var withField = phase4Marks
            .Where(m => m.Category == MarkCategory.Field)
            .Select(m => m.PlayerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return partyNames.All(n => withWound.Contains(n)) && partyNames.All(n => withField.Contains(n));
    }

    // Returns false if this player/category was already resolved earlier in this
    // frame's scan pass, so we don't add a duplicate mark.
    private bool TryClaimMark(string playerName, MarkCategory category)
        => phase4ClaimedMarks.Add((playerName, category));

    // Detection-only output: what everyone has and what it resolves to, no marker
    // assignment, no slash commands. Safe to leave running 24/7.
    private void LogPhase4Detection()
    {
        log.Info("[KefkaTF] Phase 4 — debuffs fully landed on the party (log only, marking disabled):");

        foreach (var mark in phase4Marks.OrderBy(m => m.Category).ThenBy(m => m.Slot))
        {
            var realFake = mark.IsReal ? "REAL" : "FAKE";
            var line     = $"{mark.PlayerName} (slot {mark.Slot}) | {mark.DebuffLabel} | {realFake} \u2192 {mark.Resolution}";

            log.Info($"[KefkaTF]     {line}");
            castLogWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} | PHASE4-DETECT | {line}");
        }
    }


    private bool IsSupportRole(IPartyMember member)
    {
        var role = member.ClassJob.Value.Role;
        return role is 1 or 4;
    }

    private (bool isReal, bool isFake) GetRealFakeForBoss(string bossName)
    {
        var boss = objectTable
            .OfType<IBattleNpc>()
            .FirstOrDefault(n => n.IsValid()
                && string.Equals(n.Name.TextValue, bossName, StringComparison.OrdinalIgnoreCase));

        if (boss == null)
            return (false, false);

        bool isReal = false, isFake = false;

        foreach (var status in boss.StatusList)
        {
            if (status == null) continue;
            if (status.StatusId == StatusJestersTruths) isReal = true;
            else if (status.StatusId == StatusJestersAntics) isFake = true;
        }

        return (isReal, isFake);
    }

    private void ScanPlayerDebuffs()
    {
        var members = partyList.Where(m => m != null).ToList();

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var slot   = i + 1; 

            foreach (var status in member.Statuses)
            {
                switch (status.StatusId)
                {
                    case StatusCompressedWater:
                        
                        if (IsShortSet(status.RemainingTime)) MarkExdeathDebuff(member, slot, ExdeathDebuff.CompressedWater);
                        break;
                    case StatusForkedLightning:
                        
                        if (IsShortSet(status.RemainingTime)) MarkExdeathDebuff(member, slot, ExdeathDebuff.ForkedLightning);
                        break;
                    case StatusAccelerationBomb: MarkExdeathDebuff(member, slot, ExdeathDebuff.AccelerationBomb); break;
                    case StatusCursedShriek:     MarkExdeathDebuff(member, slot, ExdeathDebuff.CursedShriek);     break;

                    case StatusEntropy:      MarkChaosDebuff(member, slot, ChaosDebuff.Entropy);      break;
                    case StatusDynamicFluid: MarkChaosDebuff(member, slot, ChaosDebuff.DynamicFluid); break;

                    case StatusWhiteWound: MarkWound(member, slot, WoundType.White); break;
                    case StatusBlackWound: MarkWound(member, slot, WoundType.Black); break;

                    case StatusAllaganField: MarkField(member, slot, FieldType.AllaganField); break;
                    case StatusBeyondDeath:  MarkField(member, slot, FieldType.BeyondDeath);  break;
                }
            }
        }
    }


    private static bool IsShortSet(float remainingTime) => remainingTime < 15f;

    private void MarkExdeathDebuff(IPartyMember member, int slot, ExdeathDebuff debuff)
    {
        if (!TryClaimMark(member.Name.TextValue, MarkCategory.Exdeath))
            return;

        var (isReal, isFake) = GetRealFakeForBoss("Exdeath");
        bool exdeathIsReal = isReal || !isFake; // default real if no status found


        bool resolvesToWater =
            (debuff == ExdeathDebuff.CompressedWater && exdeathIsReal) ||
            (debuff == ExdeathDebuff.ForkedLightning && !exdeathIsReal);

        if (!resolvesToWater)
        {
            log.Info($"[KefkaTF] {member.Name.TextValue} | {debuff} — not water, no mark");
            return;
        }

        log.Info($"[KefkaTF] {member.Name.TextValue} | {debuff} — WATER → bind");

        var (real, fake) = ExdeathResolution[debuff];

        phase4Marks.Add(new PlayerDebuffMark(
            member.Name.TextValue,
            slot,
            IsSupportRole(member),
            MarkCategory.Exdeath,
            debuff.ToString(),
            isFake ? fake : real,
            !isFake));
    }

    private void MarkChaosDebuff(IPartyMember member, int slot, ChaosDebuff debuff)
    {
        if (!TryClaimMark(member.Name.TextValue, MarkCategory.Chaos))
            return;

        var (_, isFake) = GetRealFakeForBoss("Chaos");
        var (real, fake) = ChaosResolution[debuff];

        phase4Marks.Add(new PlayerDebuffMark(
            member.Name.TextValue,
            slot,
            IsSupportRole(member),
            MarkCategory.Chaos,
            debuff.ToString(),
            isFake ? fake : real,
            !isFake));
    }


    private void MarkWound(IPartyMember member, int slot, WoundType wound)
    {
        if (!TryClaimMark(member.Name.TextValue, MarkCategory.Wound))
            return;

        var label = wound == WoundType.White ? "White Wound" : "Black Wound";

        phase4Marks.Add(new PlayerDebuffMark(
            member.Name.TextValue,
            slot,
            IsSupportRole(member),
            MarkCategory.Wound,
            label,
            label,
            true));
    }

    private void MarkField(IPartyMember member, int slot, FieldType field)
    {
        if (!TryClaimMark(member.Name.TextValue, MarkCategory.Field))
            return;

        var label = field == FieldType.AllaganField
            ? "Allagan Field (MUST SOLVE mechanics)"
            : "Beyond Death (MUST FAIL mechanics)";

        phase4Marks.Add(new PlayerDebuffMark(
            member.Name.TextValue,
            slot,
            IsSupportRole(member),
            MarkCategory.Field,
            field.ToString(),
            label,
            true));
    }

 

    private void ApplyPhase4Markers()
    {

        var bindCursor = 0;
        foreach (var mark in phase4Marks.Where(m => m.Category == MarkCategory.Exdeath))
            ApplyMarker(StillnessMarkers[bindCursor++ % StillnessMarkers.Length], mark);


        var ignoreCursor = 0;
        foreach (var mark in phase4Marks.Where(m => m.Category == MarkCategory.Wound))
            ApplyMarker(WoundMarkers[ignoreCursor++ % WoundMarkers.Length], mark);
    }

    private static string ResolvePoolKey(string resolution)
    {
        if (resolution.Contains("Stack", StringComparison.OrdinalIgnoreCase))  return "stack";
        if (resolution.Contains("Spread", StringComparison.OrdinalIgnoreCase)) return "spread";
        if (resolution.Contains("Gaze", StringComparison.OrdinalIgnoreCase))   return "gaze";
        if (resolution.Contains("Stillness", StringComparison.OrdinalIgnoreCase)
            || resolution.Contains("Motion", StringComparison.OrdinalIgnoreCase)) return "stillness";
        return "stack";
    }

    private static string[] GetPool(string poolKey) => poolKey switch
    {
        "stack"     => StackMarkers,
        "spread"    => SpreadMarkers,
        "gaze"      => GazeMarkers,
        "stillness" => StillnessMarkers,
        _           => StackMarkers,
    };

    private void ApplyMarker(string marker, PlayerDebuffMark mark)
    {
        var realFake = mark.IsReal ? "REAL" : "FAKE";

        log.Info($"[KefkaTF] {mark.PlayerName} (slot {mark.Slot}) | {mark.DebuffLabel} | {realFake} \u2192 {mark.Resolution} -> /mk {marker}");
        castLogWriter?.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} | PHASE4 | {mark.PlayerName} (slot {mark.Slot}) | {mark.DebuffLabel} | {realFake} \u2192 {mark.Resolution} | /mk {marker}");
    }


    private void AnnouncePhase4Marks()
    {
        if (phase4Marks.Count == 0)
        {
            Announce("[Phase 4] No debuff marks found on players.");
            return;
        }

        foreach (var line in BuildPhase4Summary())
        {
            log.Info($"[KefkaTF] {line}");
            chatGui.Print(line);
        }
    }

    private List<string> BuildPhase4Summary()
    {
        var lines = new List<string> { "=== Phase 4: Kefka Says ===" };

        foreach (var category in new[] { MarkCategory.Exdeath, MarkCategory.Chaos })
        {
            foreach (var group in phase4Marks.Where(m => m.Category == category).GroupBy(m => m.DebuffLabel))
            {
                var resolution = group.First().Resolution;
                var realFake   = group.First().IsReal ? "REAL" : "FAKE";
                lines.Add($"{group.Key} [{realFake}] -> {resolution}:");

                foreach (var mark in group.OrderByDescending(m => m.IsSupport))
                    lines.Add($"    {mark.PlayerName} ({(mark.IsSupport ? "Support" : "DPS")})");
            }
        }

        var combo = phase4Marks
            .Where(m => m.Category is MarkCategory.Wound or MarkCategory.Field)
            .GroupBy(m => m.PlayerName);

        if (combo.Any())
        {
            lines.Add("Wound / Field assignments:");
            foreach (var playerGroup in combo)
            {
                var tags = string.Join(" + ", playerGroup.Select(m => m.Resolution));
                lines.Add($"    {playerGroup.Key}: {tags}");
            }
        }

        return lines;
    }

    private List<string> BuildPhase4EchoLines()
    {
        var lines = new List<string>();

        foreach (var category in new[] { MarkCategory.Exdeath, MarkCategory.Chaos })
        {
            foreach (var group in phase4Marks.Where(m => m.Category == category).GroupBy(m => m.DebuffLabel))
            {
                var realFake = group.First().IsReal ? "REAL" : "FAKE";
                var names    = string.Join(", ", group.Select(m => FirstName(m.PlayerName)));
                lines.Add($"/e {group.Key} [{realFake}] {group.First().Resolution}: {names}");
            }
        }

        foreach (var playerGroup in phase4Marks
                     .Where(m => m.Category is MarkCategory.Wound or MarkCategory.Field)
                     .GroupBy(m => m.PlayerName))
        {
            var tags = string.Join(" + ", playerGroup.Select(m => m.Resolution));
            lines.Add($"/e {FirstName(playerGroup.Key)}: {tags}");
        }

        return lines;
    }

    private static string FirstName(string fullName) => fullName.Split(' ')[0];


    public void RunPhase4Test()
    {
        log.Info("[KefkaTF] --PHASE4 FAKE TEST START");

        var rnd = new Random();

        var fakeParty = new[]
        {
            ("Fish",   1, true),
            ("Silver", 2, true),
            ("Halmon", 3, true),
            ("Yue",    4, true),
            ("Riso",   5, false),
            ("Faye",   6, false),
            ("Darfal", 7, false),
            ("Mimi",   8, false),
        };


        bool exdeathIsReal = rnd.NextDouble() > 0.5;
        var realFakeLabel = exdeathIsReal ? "REAL" : "FAKE";
        log.Info($"[KefkaTF] TEST — Exdeath: {realFakeLabel}");


        var shuffled = fakeParty.OrderBy(_ => rnd.Next()).ToArray();

        phase4AlreadyLoggedThisCycle = false;
        phase4ClaimedMarks.Clear();


        var debuffs = new[]
        {
            ExdeathDebuff.CompressedWater,  // Group 1
            ExdeathDebuff.CompressedWater,  // Group 1
            ExdeathDebuff.ForkedLightning,  // Group 1
            ExdeathDebuff.ForkedLightning,  // Group 1
            ExdeathDebuff.CompressedWater,  // Group 2 — ignored
            ExdeathDebuff.CompressedWater,  // Group 2 — ignored
            ExdeathDebuff.ForkedLightning,  // Group 2 — ignored
            ExdeathDebuff.ForkedLightning,  // Group 2 — ignored
        };

        phase4Marks.Clear();

        for (var i = 0; i < 8; i++)
        {
            var (name, slot, isSupport) = shuffled[i];
            var debuff    = debuffs[i];
            bool isGroup1 = i < 4;

            if (!isGroup1)
            {
                // Mark Group 2 water resolvers with ignore markers.
                bool resolvesToWaterG2 =
                    (debuff == ExdeathDebuff.CompressedWater && exdeathIsReal) ||
                    (debuff == ExdeathDebuff.ForkedLightning && !exdeathIsReal);

                if (resolvesToWaterG2)
                {
                    log.Info($"[KefkaTF] TEST   {name} (slot {slot}) | {debuff} | Group 2 — WATER → ignore");
                    phase4Marks.Add(new PlayerDebuffMark(
                        name, slot, isSupport,
                        MarkCategory.Wound,
                        debuff.ToString(),
                        "ignore",
                        exdeathIsReal));
                }
                else
                {
                    log.Info($"[KefkaTF] TEST   {name} (slot {slot}) | {debuff} | Group 2 — not water, skip");
                }
                continue;
            }

            // Mark only if this player resolves to water.
            bool resolvesToWater =
                (debuff == ExdeathDebuff.CompressedWater && exdeathIsReal) ||
                (debuff == ExdeathDebuff.ForkedLightning && !exdeathIsReal);

            if (!resolvesToWater)
            {
                log.Info($"[KefkaTF] TEST   {name} (slot {slot}) | {debuff} | Group 1 — not water, skip");
                continue;
            }

            log.Info($"[KefkaTF] TEST   {name} (slot {slot}) | {debuff} | Group 1 — WATER → bind");

            phase4Marks.Add(new PlayerDebuffMark(
                name, slot, isSupport,
                MarkCategory.Exdeath,
                debuff.ToString(),
                "3 Player Stack",
                exdeathIsReal));
        }

        ApplyPhase4Markers();
        log.Info("[KefkaTF] --PHASE4 FAKE TEST END");
    }

    private void WritePhase4EchoMacro()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path    = Path.Combine(desktop, "KefkaTF_Phase4_EchoMacro.txt");
            var lines   = BuildPhase4EchoLines();

            using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
            writer.WriteLine("# Phase 4 echo macro — paste into an in-game macro.");
            writer.WriteLine("# FFXIV macros are capped at 15 lines; split into macro #2, #3... as needed.");
            writer.WriteLine($"# generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine();

            var macroIndex = 1;
            var lineCount  = 0;

            foreach (var line in lines)
            {
                if (lineCount == 0)
                    writer.WriteLine($"--- macro #{macroIndex} ---");

                writer.WriteLine(line);
                lineCount++;

                if (lineCount >= 15)
                {
                    writer.WriteLine();
                    macroIndex++;
                    lineCount = 0;
                }
            }

            log.Info($"[KefkaTF] Phase 4 echo macro written to: {path}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[KefkaTF] Failed to write Phase 4 echo macro file.");
        }
    }
}
