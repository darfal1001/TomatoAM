using System;
using System.Collections.Generic;

namespace SamplePlugin;


public partial class KefkaTF
{
    private const uint StatusJestersAntics  = 1486; 
    private const uint StatusJestersTruths  = 1487; 
    private const uint StatusManaCharge     = 1482; 
    private const uint StatusFireCharged    = 1483;
    private const uint StatusIceCharged     = 1484;
    private const uint StatusThunderCharged = 1485;


    private static readonly HashSet<string> KnownBossNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Kefka", "Chaos", "Exdeath"
    };

    private enum ActionKind { Element, ManaCharge, ManaRelease, AlwaysReal, ElementOrReal }

    private record ActionDef(string Label, ActionKind Kind);

    private static readonly Dictionary<uint, ActionDef> Actions = new()
    {

        { 0x28CE, new("FIRE",    ActionKind.Element) }, 
        { 0xBA94, new("FIRE",    ActionKind.Element) }, // "Mystery Magic"
        { 0x291E, new("FIRE",    ActionKind.Element) }, // "Flagrant Fire" (from cast log)
        { 0x291F, new("FIRE",    ActionKind.Element) }, // "Flagrant Fire" (from cast log)


        { 0x28C5, new("ICE",     ActionKind.Element) }, 
        { 0x28C6, new("ICE",     ActionKind.Element) },
        { 0x28C7, new("ICE",     ActionKind.Element) },
        { 0x28C9, new("ICE",     ActionKind.Element) },
        { 0x2B2C, new("ICE",     ActionKind.Element) },
        { 0xBA95, new("ICE",     ActionKind.Element) }, // "Blizzard III Blowout"
        { 0xBA98, new("ICE",     ActionKind.Element) }, // "Blizzard III Blowout"
        { 0xBA9B, new("ICE",     ActionKind.Element) }, // "Blizzard III Blowout"
        { 0xBA9E, new("ICE",     ActionKind.Element) }, // "Blizzard III Blowout"
        { 0xBB0D, new("ICE",     ActionKind.ElementOrReal) }, // Exdeath "Blizzard III" (from cast log)
        { 0xBB0F, new("ICE",     ActionKind.ElementOrReal) }, // Exdeath "Blizzard III" (from cast log)
        { 0xBB11, new("ICE",     ActionKind.ElementOrReal) }, // Exdeath "Blizzard III" (from cast log)
        { 0x2918, new("ICE",     ActionKind.Element) }, // "Blizzard Blitz" (from cast log)
        { 0x2917, new("ICE",     ActionKind.Element) }, // "Blizzard Blitz" (from cast log)
        { 0x2914, new("ICE",     ActionKind.Element) }, // "Blizzard Blitz" (from cast log)
        { 0x2913, new("ICE",     ActionKind.Element) }, // "Blizzard Blitz" (from cast log)

        { 0x28CA, new("THUNDER", ActionKind.Element) }, 
        { 0x28CB, new("THUNDER", ActionKind.Element) },
        { 0x28CC, new("THUNDER", ActionKind.Element) },
        { 0x28CD, new("THUNDER", ActionKind.Element) },
        { 0x2B2F, new("THUNDER", ActionKind.Element) },
        { 0x2B30, new("THUNDER", ActionKind.Element) },
        { 0x2B31, new("THUNDER", ActionKind.Element) },
        { 0xC5DE, new("THUNDER", ActionKind.Element) }, 
        { 0xBA9F, new("THUNDER", ActionKind.Element) }, // "Thrumming Thunder III"
        { 0xBAA0, new("THUNDER", ActionKind.Element) }, // "Thrumming Thunder III"
        { 0xBAA1, new("THUNDER", ActionKind.Element) }, // "Thrumming Thunder III"
        { 0xBB09, new("THUNDER", ActionKind.ElementOrReal) }, // Exdeath "Thunder III" (from cast log)
        { 0xBB12, new("THUNDER", ActionKind.ElementOrReal) }, // Exdeath "Thunder III" (from cast log)
        { 0x291D, new("THUNDER", ActionKind.Element) }, // "Thrumming Thunder" (from cast log)
        { 0x291A, new("THUNDER", ActionKind.Element) }, // "Thrumming Thunder" (from cast log)

        { 0x28D1, new("MANA CHARGE",  ActionKind.ManaCharge)  },
        { 0xBAA4, new("MANA CHARGE",  ActionKind.ManaCharge)  },
        { 0x28D2, new("MANA RELEASE", ActionKind.ManaRelease) },

        { 0x28E7, new("ULTIMA UPSURGE",  ActionKind.AlwaysReal) },
        { 0x28E8, new("HYPERDRIVE",      ActionKind.ElementOrReal) },
        { 0x292E, new("HYPERDRIVE",      ActionKind.ElementOrReal) }, // second Hyperdrive id (from cast log)
        { 0xC2DC, new("KEFKA SAYS",      ActionKind.AlwaysReal) },
        { 0xBB14, new("GRAND CROSS",     ActionKind.AlwaysReal) },
        { 0xC396, new("EDGE OF DEATH",   ActionKind.AlwaysReal) },
        { 0xC395, new("BLACK ANTILIGHT", ActionKind.AlwaysReal) },
        { 0xC3A2, new("FLOOD OF NAUGHT", ActionKind.AlwaysReal) },


        { 0xC61E, new("METEOR",          ActionKind.ElementOrReal) },
        { 0xC258, new("METEOR",          ActionKind.ElementOrReal) }, // second Meteor id (from cast log)
        { 0xBB1F, new("TSUNAMI",         ActionKind.ElementOrReal) },
        { 0xBB21, new("TSUNAMI",         ActionKind.ElementOrReal) },
        { 0xBB1E, new("INFERNO",         ActionKind.ElementOrReal) },
        { 0xBB20, new("INFERNO",         ActionKind.ElementOrReal) },
        { 0xBB23, new("STRAY FLAMES",    ActionKind.ElementOrReal) },
        { 0xBB24, new("STRAY SPRAY",     ActionKind.ElementOrReal) },
        { 0xBB25, new("STRAY SPRAY",     ActionKind.ElementOrReal) }, // second Stray Spray id (from cast log)

        { 0xC571, new("EARTHQUAKE",      ActionKind.ElementOrReal) },
        { 0xC572, new("EARTHQUAKE",      ActionKind.ElementOrReal) },
    };
}
