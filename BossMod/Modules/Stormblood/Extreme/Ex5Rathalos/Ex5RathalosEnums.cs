namespace BossMod.Stormblood.Extreme.Ex5Rathalos;

public enum OID : uint
{
    Boss = 0x212F, // R5.460, x1
    Helper = 0x18D6, // R1.300, x1, mixed
    WyvernsTail = 0x23D9, // R3.900, x1, Part type
    SteppeSheep = 0x2131, // R0.700, x0 (spawn during fight)
    SteppeYamaa = 0x2132, // R1.920, x0 (spawn during fight)
    SteppeYamaa1 = 0x2133, // R1.920, x0 (spawn during fight)
    SteppeCoeurl = 0x2134, // R3.150, x0 (spawn during fight)
    Garula = 0x2130, // R4.000, x0 (spawn during fight)
    Fireball = 0x1E9927
}

public enum AID : uint
{
    Roar1 = 11459, // Boss->self, no cast, range 50+R circle
    MangleVisual = 10323, // Boss->self, 2.5s cast, range 10 120-degree cone
    Mangle = 10332, // Helper->self, no cast, range 10 120-degree cone
    TailSmash = 10324, // Helper->self, no cast, range 11 ?-degree cone, this gets used immediately after Mangle but i guess it only hits behind him? i don't feel like testing it fuck that
    RushVisual1 = 10326, // Boss->location, 2.0s cast, width 9 rect charge
    Rush1 = 10813, // Helper->location, no cast, width 9 rect charge
    TailSwingVisual = 10325, // Boss->self, no cast, range 11 180-degree cone
    TailSwing = 10812, // Helper->self, no cast, range 11 180-degree cone
    Roar2 = 10333, // Boss->self, no cast, range 50+R circle, applies stun
    KingOfTheSkiesVisual = 10334, // Boss->location, no cast, range 50 circle
    KingOfTheSkies = 11545, // Helper->location, no cast, range 50 circle
    SweepingFlamesVisual = 10338, // Boss->self, no cast, range 11 120-degree cone
    SweepingFlames = 11446, // Helper->self, no cast, range 11 120-degree cone
    Mangle2Visual = 10339, // Boss->self, 0.7s cast, range 9 90-degree cone
    Mangle2 = 11447, // Helper->self, no cast, range 9 90-degree cone
    FireballBossFirst = 10335, // Boss->player, 5.0s cast, range 5 circle
    FireballFirst = 10336, // Helper->player, no cast, range 5 circle
    FireballBossRest = 11530, // Boss->player, 3.0s cast, range 5 circle
    FireballRest = 11531, // Helper->player, no cast, range 5 circle
    RushVisual2 = 10337, // Boss->location, 1.0s cast, width 10 rect charge
    Rush2 = 11445, // Helper->location, no cast, width 9 rect charge
    VeniVidiVici = 21847, // Boss->location, no cast, width 10 rect charge

    GarulaRush = 10344, // Garula->Boss, 2.0s cast, width 8 rect charge, stuns boss
    CoeurlAuto = 870, // SteppeCoeurl->player/Boss, no cast, single-target
    MobAutos = 872, // SteppeYamaa1/SteppeYamaa/SteppeSheep/Garula->player/Boss, no cast, single-target
    Lanolin = 10328, // SteppeYamaa1->self, 2.5s cast, single-target
    Lullaby = 10340, // SteppeSheep->self, 3.0s cast, range 3+R circle
    HeadButt = 10341, // SteppeYamaa1->location, 2.5s cast, range 3+R width 3 rect
}

public enum SID : uint
{
    // Applied to the boss the moment King of the Skies resolves and never removed for the rest
    // of the pull - confirmed via replay (STA+ on the boss, index 0, right after the Helper's
    // KingOfTheSkies cast). Its own stack/param value isn't visible in BossMod's replay format
    // (only the initial application is logged, not later updates), so this can't be read as a
    // live 0-100 gauge from here - but its presence marks "the topple sequence has started".
    KingOfTheSkiesMode = 1518,
    // The actual stagger/topple: boss gets stunned for ~20s. Replay evidence put this ~162s after
    // KingOfTheSkiesMode first applied. This is presumably what "the gauge reaching 100" cashes
    // out as in observable game state, regardless of the exact mechanism that fills it - treat
    // this stun as the real "now go break the tail" signal instead of switching the instant the
    // tail becomes targetable (which can be many seconds before the boss is actually staggered,
    // and pulling DPS off the boss body early only slows down reaching the stagger in the first
    // place if it's fed by damage dealt to the boss).
    Toppled = 1521,
}
