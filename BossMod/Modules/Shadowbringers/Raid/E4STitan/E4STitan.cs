namespace BossMod.Shadowbringers.Raid.E4STitan;

// VERIFICATION SUMMARY: only P1 (小泰坦/Titan phase) mechanics are confirmed against a real replay.
// Every P2/Titan-Maximum and P3 mechanic below is either estimated from cactbot/huijiwiki or explicitly
// marked non-functional/unverified in its own comment - the analysed pull never got a clean run past
// the first Orogenesis. Don't treat a P2/P3 component's presence as proof it dodges correctly in-game.
//
// NOTE (WIP): originally hand-built from cactbot's e4s.ts data; ability IDs / cast times / caster OIDs
// have since been cross-checked against a real replay (see E4STitanEnums.cs header). Shapes/radii of
// the newly-wired AOE components (Weight of the Land / Evil Earth / Giant Rock puddles) are still
// best-effort circle radii. Several Titan Maximum mechanics (Megalith, Plate Fracture, Tectonic
// Uplift, Rock Throw / Granite Gaol tether, Earthen Fury enrage) never occurred in the analysed pull
// and remain unverified.
//
// PHASE STRUCTURE (from replay): Titan (0x298F) and Titan Maximum (0x2990) both spawn at pull start;
// the fight alternates Titan-phase <-> Titan-Maximum-phase via Orogenesis, and at every transition
// BOTH actors are destroyed and a fresh pair spawned. The transition can happen multiple times if the
// group is slow. The state machine therefore keeps every component active the whole time rather than
// trying to gate Titan-Maximum mechanics behind a one-way "phase 3".
[ModuleInfo(BossModuleInfo.Maturity.WIP,
    Contributors = "Community (cactbot data) + Claude",
    GroupType = BossModuleInfo.GroupType.CFC,
    // 台服 ContentFinderCondition row 690 =「伊甸零式希望樂園 覺醒之章4」(內部名 n4g4_2)。
    // 已由實機 replay 的 ZONE 事件 (856|690) 確認正確。
    GroupID = 690u,
    NameID = 8350u, // "泰坦" BNpcName - confirmed from replay ACT+ event
    PlanLevel = 80)]
public class E4STitan : BossModule
{
    public readonly List<Actor> Bombs;
    public readonly List<Actor> GiantRocks;
    public Actor? BossMaximum() => Enemies((uint)OID.BossMaximum).Count != 0 ? Enemies((uint)OID.BossMaximum)[0] : null;

    // Platform centred on (100,100). Weight of the Land telegraphs land on a 4x4 grid at X/Z in
    // {85,95,105,115} and bombs on a 3x3 grid at {86,100,114} (replay-confirmed); every replayed
    // position (incl. knockback landings) stays within ~r21-22 in ALL directions - it's a ROUND
    // platform, not a square. A square arena gave the AI ~r29 diagonal corners that don't exist, so it
    // dodged AOEs by running into them and fell off. Circle radius 22 (a hair over the real edge) so
    // the pathfinding grid - which is exactly 2r wide with no margin - still contains a player pushed
    // out to r20-21 (an r20 grid clips them and locks up navigation, = the in-combat stutter report).
    public E4STitan(WorldState ws, Actor primary) : base(ws, primary, new(100, 100), new ArenaBoundsCircle(22))
    {
        Bombs = Enemies((uint)OID.BombBoulder);
        GiantRocks = Enemies((uint)OID.GiantRock);
    }

    protected override void DrawEnemies(int pcSlot, Actor pc)
    {
        Arena.Actor(PrimaryActor, allowDeadAndUntargetable: true);
        var max = BossMaximum();
        if (max != null)
            Arena.Actor(max, allowDeadAndUntargetable: true);
        Arena.Actors(Bombs, Colors.Object);
        Arena.Actors(GiantRocks, Colors.Object);
    }
}
