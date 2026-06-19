using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SamplePlugin;

public unsafe class CrustLogicSim : IDisposable
{
    private readonly IPartyList _party;
    private readonly IPluginLog _log;
    private readonly IFramework _framework;

    private const uint StatusAccretion      = 1604;
    private const uint StatusPrimordiaCrust = 1605;
    private const uint StatusFirstInLine    = 3004;
    private const uint StatusSecondInLine   = 3005;
    private const uint StatusThirdInLine    = 3006;

    private bool _fired = false;

    private delegate void PostCommandDelegate(IntPtr uiModule, IntPtr cmd, IntPtr unk1, byte unk2);
    private readonly PostCommandDelegate? _postCmd;

    private static readonly Dictionary<SignEnum, string> SignWords = new()
    {
        { SignEnum.Attack1, "attack1" },
        { SignEnum.Attack2, "attack2" },
        { SignEnum.Attack3, "attack3" },
        { SignEnum.Bind1,   "bind1"   },
        { SignEnum.Bind2,   "bind2"   },
        { SignEnum.Bind3,   "bind3"   },
        { SignEnum.Ignore1, "ignore1" },
        { SignEnum.Ignore2, "ignore2" },
    };

    public enum SignEnum
    {
        Attack1, Attack2, Attack3,
        Bind1, Bind2, Bind3,
        Ignore1, Ignore2,
    }

    private static bool IsSupport(int slot) => slot <= 4;
    private static bool IsDps(int slot) => slot >= 5;

    public CrustLogicSim(IPartyList party, IPluginLog log, ISigScanner sigScanner, IFramework framework)
    {
        _party = party;
        _log = log;
        _framework = framework;

        if (sigScanner.TryScanText(
            "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F2 48 8B F9 45 84 C9",
            out var ptr))
        {
            _postCmd = Marshal.GetDelegateForFunctionPointer<PostCommandDelegate>(ptr);
        }

        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var members = _party.Where(m => m != null).ToList();
        if (members.Count == 0) return;

        bool anyActive = members.Any(m => m.Statuses.Any(s =>
            s.StatusId == StatusFirstInLine ||
            s.StatusId == StatusSecondInLine ||
            s.StatusId == StatusThirdInLine ||
            s.StatusId == StatusAccretion ||
            s.StatusId == StatusPrimordiaCrust));

        if (!anyActive && _fired)
        {
            _fired = false;
            _log.Info("Reset mechanic");
            return;
        }

        if (_fired) return;

        var all = members.Select((m, i) =>
        {
            var (tier, acc) = ReadStatuses(m);
            return (m.Name.TextValue, i + 1, tier, acc);
        }).ToList();

        if (!all.All(p => p.Item3 > 0)) return;

        _fired = true;
        AssignAndMark(all);
    }

    private void AssignAndMark(List<(string Name, int PartySlot, int Tier, bool Acc)> all)
    {
        var attack = all.Where(p => p.Tier == 1).ToList();
        var bind   = all.Where(p => p.Tier == 2).ToList();
        var ignore = all.Where(p => p.Tier == 3).ToList();

        LogAndAssign("ATTACK", attack, SignEnum.Attack1, SignEnum.Attack2, SignEnum.Attack3);
        LogAndAssign("BIND", bind, SignEnum.Bind1, SignEnum.Bind2, SignEnum.Bind3);
        LogIgnore("IGNORE", ignore);
    }

    private void LogAndAssign(
        string label,
        List<(string Name, int PartySlot, int Tier, bool Acc)> group,
        SignEnum first,
        SignEnum second,
        SignEnum third)
    {
        _log.Info($"=== {label} GROUP ===");

        var acc = group.FirstOrDefault(p => p.Acc);
        var remaining = group.Where(p => !p.Acc).ToList();

        var m1 = remaining.FirstOrDefault(p => IsDps(p.PartySlot));
        if (m1 == default && remaining.Count > 0)
            m1 = remaining[0];

        if (m1 != default)
        {
            _log.Info($"{m1.Name} → {first}");
            ApplyMarker(first, m1.PartySlot);
        }

        remaining.Remove(m1);

        var m2 = remaining.FirstOrDefault(p => IsSupport(p.PartySlot));
        if (m2 == default && remaining.Count > 0)
            m2 = remaining[0];

        if (m2 != default)
        {
            _log.Info($"{m2.Name} → {second}");
            ApplyMarker(second, m2.PartySlot);
        }

        if (acc != default)
        {
            _log.Info($"{acc.Name} → {third} (Accretion)");
            ApplyMarker(third, acc.PartySlot);
        }
    }

    private void LogIgnore(
        string label,
        List<(string Name, int PartySlot, int Tier, bool Acc)> group)
    {
        _log.Info($"=== {label} GROUP ===");

        var dps = group.FirstOrDefault(p => IsDps(p.PartySlot));
        var sup = group.FirstOrDefault(p => IsSupport(p.PartySlot));

        if (dps != default)
        {
            _log.Info($"{dps.Name} → Ignore1");
            ApplyMarker(SignEnum.Ignore1, dps.PartySlot);
        }

        if (sup != default)
        {
            _log.Info($"{sup.Name} → Ignore2");
            ApplyMarker(SignEnum.Ignore2, sup.PartySlot);
        }
    }

    private static (int Tier, bool Acc) ReadStatuses(IPartyMember member)
    {
        int tier = 0;
        bool acc = false;

        foreach (var s in member.Statuses)
        {
            switch (s.StatusId)
            {
                case StatusFirstInLine: tier = 1; break;
                case StatusSecondInLine: tier = 2; break;
                case StatusThirdInLine: tier = 3; break;
                case StatusAccretion: acc = true; break;
            }
        }

        return (tier, acc);
    }

    private void ApplyMarker(SignEnum sign, int slot)
    {
        if (!SignWords.TryGetValue(sign, out var word)) return;
        SendCommand($"/mk {word} <{slot}>");
    }

    private void SendCommand(string text)
    {
        if (_postCmd == null) return;

        var ui = (UIModule*)FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();

        var bytes = Encoding.UTF8.GetBytes(text);
        var buf = stackalloc byte[400];
        var len = Math.Min(bytes.Length, 398);
        bytes.AsSpan(0, len).CopyTo(new Span<byte>(buf, len));
        buf[len] = 0;

        var header = stackalloc byte[32];
        *(long*)(header + 0) = (long)buf;
        *(long*)(header + 8) = len;
        *(long*)(header + 16) = 64;
        *(long*)(header + 24) = 0;

        _postCmd((IntPtr)ui, (IntPtr)header, IntPtr.Zero, 0);
    }

    public void Run()
    {
        _log.Info("--CRUST MANUAL TRIGGER");

        var members = _party.Where(m => m != null).ToList();
        if (members.Count == 0) return;

        var all = members.Select((m, i) =>
        {
            var (tier, acc) = ReadStatuses(m);
            return (m.Name.TextValue, i + 1, tier, acc);
        }).ToList();

        AssignAndMark(all);
    }

    public void RunTest()
    {
        _log.Info("--CRUST FAKE TEST START");

        var rnd = new Random();

        var all = membersForTest()
            .Select(p => (p.Name, p.Slot, rnd.Next(1, 4), rnd.NextDouble() > 0.5))
            .ToList();

        foreach (var p in all)
            _log.Info($"{p.Name} | Slot {p.Slot} | Tier {p.Item3} | Acc {p.Item4}");

        AssignAndMark(all);

        _log.Info("--CRUST FAKE TEST END");
    }

    private IEnumerable<(string Name, int Slot)> membersForTest()
    {
        return new[]
        {
            ("Fish", 1),
            ("Silver", 2),
            ("Halmon", 3),
            ("Yue", 4),
            ("Riso", 5),
            ("Faye", 6),
            ("Darfal", 7),
            ("Mimi", 8),
        };
    }
}