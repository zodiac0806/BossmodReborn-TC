using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BossMod.Stormblood.Extreme.Ex5Rathalos;

// AIHints.SetPriority only steers BossMod's own AI-mode targeting - it does nothing when combat
// is actually driven by RotationSolverReborn (the AutoDuty+RSR setup this fight is normally run
// with), since RSR has its own independent target selection. RSR exposes a real
// AddPriorityNameID/RemovePriorityNameID IPC pair for exactly this ("top priority named
// hostile" - see RotationSolverReborn's own ObjectHelper.IsTopPriorityNamedHostile), which is
// what actually needs to be used to make the AI rush Garula down.
//
// This used to instead BLACKLIST Garula until King of the Skies resolved, on the theory that
// killing it early would leave nowhere to hide for that raidwide. That was wrong on two counts:
// (1) a dead Garula's corpse blocks line of sight exactly as well as a living one, and replay
// evidence shows its corpse isn't destroyed until ~15-20s after death, comfortably longer than
// the ~7s window between the mechanic's setup and its resolution - so an early kill never
// actually breaks the safe spot; (2) the blacklist did nothing to prevent Garula dying anyway
// (it still takes incidental cleave/AOE damage), it just stopped anyone from focusing it down
// on purpose, so it was left to wander/kite for however long incidental damage took to kill it.
// Prioritizing it instead makes it die faster and closer to where the party already is, which
// only makes the eventual safe-zone geometry easier to reach, never harder.
// Soft dependency, same pattern as WrathComboBridge.cs: silently no-op if RSR isn't installed
// or the IPC call fails, never let this break the module itself.
static class RSRPriority
{
    private static ICallGateSubscriber<uint, object>? Gate(string method)
        => Service.PluginInterface.GetIpcSubscriber<uint, object>($"RotationSolverReborn.{method}");

    private static bool _loggedUnavailable;

    public static void Add(uint nameId)
    {
        try
        {
            Gate("AddPriorityNameID")?.InvokeAction(nameId);
        }
        catch (IpcError)
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                Service.Logger.Information("[Ex5Rathalos] RotationSolverReborn IPC not available, can't prioritize Garula - focus it manually.");
            }
        }
        catch (Exception ex)
        {
            Service.Logger.Information($"[Ex5Rathalos] AddPriorityNameID failed (ignored): {ex}");
        }
    }

    public static void Remove(uint nameId)
    {
        try
        {
            Gate("RemovePriorityNameID")?.InvokeAction(nameId);
        }
        catch (IpcError)
        {
            // RSR not installed - nothing was ever added, nothing to undo
        }
        catch (Exception ex)
        {
            Service.Logger.Information($"[Ex5Rathalos] RemovePriorityNameID failed (ignored): {ex}");
        }
    }

    // AddPriorityNameID alone only keeps a target from being filtered out as ineligible (see
    // RotationSolverReborn's own IsAttackable, which is what actually consumes the flag) - it
    // does NOT force RSR to switch away from whatever it's already engaged with. That's fine for
    // Garula (a fresh, separate enemy that naturally gets picked when target selection runs), but
    // not for the tail - a Part attached to the SAME boss body the party is already locked onto,
    // so priority alone never gets it any attention. Real replay evidence: the tail became
    // targetable ~17s before the boss died and the priority flag was set, but the party kept
    // hitting the boss the whole time regardless. Blacklisting the boss itself while the tail is
    // up removes it as an eligible target entirely, which actually forces a switch - matching
    // what the original reference module intended (it also set the boss to PriorityForbidden
    // here, just through the BossMod-AI-only hint that does nothing for RSR).
    public static void AddBlacklist(uint nameId)
    {
        try
        {
            Gate("AddBlacklistNameID")?.InvokeAction(nameId);
        }
        catch (IpcError)
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                Service.Logger.Information("[Ex5Rathalos] RotationSolverReborn IPC not available, can't redirect off the boss for the tail - do it manually.");
            }
        }
        catch (Exception ex)
        {
            Service.Logger.Information($"[Ex5Rathalos] AddBlacklistNameID failed (ignored): {ex}");
        }
    }

    public static void RemoveBlacklist(uint nameId)
    {
        try
        {
            Gate("RemoveBlacklistNameID")?.InvokeAction(nameId);
        }
        catch (IpcError)
        {
            // RSR not installed - nothing was ever added, nothing to undo
        }
        catch (Exception ex)
        {
            Service.Logger.Information($"[Ex5Rathalos] RemoveBlacklistNameID failed (ignored): {ex}");
        }
    }

    // ForceAction (RSR's ActionCommand IPC, used to force-spam a single weak GCD to throttle
    // output) was removed 2026-09-06 - see the comment in TargetHints.Update() for why.
}

class Mangle(BossModule module) : Components.SimpleAOEs(module, (uint)AID.MangleVisual, new AOEShapeCone(10f, 60.Degrees()));
class GarulaRush(BossModule module) : Components.ChargeAOEs(module, (uint)AID.GarulaRush, 4f);
class Lullaby(BossModule module) : Components.SimpleAOEs(module, (uint)AID.Lullaby, 3.7f);
class HeadButt(BossModule module) : Components.SimpleAOEs(module, (uint)AID.HeadButt, new AOEShapeRect(4.92f, 1.5f));

class Rush(BossModule module) : Components.GenericAOEs(module)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == (uint)AID.RushVisual1 || spell.Action.ID == (uint)AID.RushVisual2)
        {
            var delay = spell.Action.ID == (uint)AID.RushVisual1 ? 0.6f : 1.3f;

            var chargeDir = spell.LocXZ - caster.Position;
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeRect(chargeDir.Length() + 6, 4.5f), caster.Position, chargeDir.ToAngle(), Module.CastFinishAt(spell, delay)));
        }
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.Rush1 || spell.Action.ID == (uint)AID.Rush2)
            _aoe.Clear();
    }
}

class TailSwing(BossModule module) : Components.GenericAOEs(module, (uint)AID.TailSwingVisual)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeCone(11, 90.Degrees()), caster.Position, spell.Rotation - 90.Degrees(), WorldState.FutureTime(1.9f)));
        }

        if (spell.Action.ID == (uint)AID.TailSwing)
            _aoe.Clear();
    }
}

class SweepingFlames(BossModule module) : Components.GenericAOEs(module, (uint)AID.SweepingFlamesVisual)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeCone(11, 60.Degrees()), caster.Position, spell.Rotation, WorldState.FutureTime(1.5f)));
        }

        if (spell.Action.ID == (uint)AID.SweepingFlames)
            _aoe.Clear();
    }
}

class Mangle2(BossModule module) : Components.GenericAOEs(module, (uint)AID.Mangle2Visual)
{
    private readonly List<AOEInstance> _aoe = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => CollectionsMarshal.AsSpan(_aoe);

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _aoe.Clear();
            _aoe.Add(new(new AOEShapeCone(9, 45.Degrees()), caster.Position, spell.Rotation, Module.CastFinishAt(spell, 0.6f)));
        }
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.Mangle2)
            _aoe.Clear();
    }
}

class FireballStack1(BossModule module) : Components.StackWithCastTargets(module, (uint)AID.FireballBossFirst, 5);
class FireballStack2(BossModule module) : Components.StackWithCastTargets(module, (uint)AID.FireballBossRest, 5);
class FirePuddle(BossModule module) : Components.VoidzoneAtCastTarget(module, 5, (uint)AID.FireballFirst, m => m.Enemies(OID.Fireball).Where(e => e.EventState != 7), 0.5d)
{
    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        base.OnEventCast(caster, spell);

        if (spell.Action.ID == (uint)AID.FireballRest)
            _predictedByEvent.Add((WorldState.Actors.Find(spell.MainTargetID)?.Position ?? spell.TargetXZ, WorldState.FutureTime(CastEventToSpawn)));
    }
}

// The corpse of Garula (spawned mid-fight) is the only line-of-sight blocker for this
// raidwide - see TargetHints below, which keeps the AI from killing it before this resolves.
//
// KingOfTheSkiesVisual (10334) is a "no cast" (instant) boss action - it has no cast bar at
// all, so OnCastStarted/OnCastFinished (which only fire for actor.CastInfo?.IsSpell(), i.e.
// actions with an actual cast time) NEVER get called for it. That was the real bug behind the
// module "never dodging": diagnostic logging placed inside OnCastStarted correctly never saw
// this action - not because of some dispatch gap, but because that hook is simply the wrong
// event for an instant-execute ability. Confirmed against the reference ffxiv_bossmod
// implementation, which hooks OnEventCast here for exactly this reason, and manufactures its
// own 7s resolve timer (WorldState.FutureTime(7)) since there's no real cast time to key off.
class KingOfTheSkies(BossModule module) : Components.GenericLineOfSightAOE(module, (uint)AID.KingOfTheSkiesVisual, 100)
{
    public bool Resolved;

    // Origin = the boss's own current position, per the user's explicit description of the
    // mechanic: stand behind Garula's corpse so it blocks line of sight between yourself and the
    // dragon itself. An earlier version of this file assumed the origin was a separate Helper
    // actor's spawn point (~100,82), based on 5 pulls' worth of telemetry clustering there - but
    // that theory kept failing to predict actual survival across several more test pulls (including
    // cases where a "comfortably safe by that model" player died while an "unsafe by that model"
    // player survived), and the user confirmed the real mechanic is measured against the boss's
    // body, not a Helper. Much simpler than the Helper-tracking this replaced, too - the boss is a
    // single, unambiguous actor (Module.PrimaryActor), no risk of picking an idle decoy.
    // 2026-09-06: screen recording evidence showed the boss visibly flies up and circles overhead
    // during the ~7s countdown between the visual cast and the actual resolve (matches "起飛施放"
    // in the user's own description of the mechanic) - and the minimap safe-zone wedge was seen
    // visibly rotating along with it, second to second, right up until the explosion itself. Reading
    // the boss's LIVE position every tick (as this used to) means the safe zone never holds still
    // long enough to actually stand in - a position that looked safe a couple of seconds into the
    // countdown can easily no longer be covered by the time the boss finishes moving and actually
    // fires. Lock the origin once, at the moment the countdown starts, and hold it for the whole
    // window instead - this doesn't require knowing the boss's exact landing spot in advance, it
    // just stops recomputing against whatever it is at literally this instant, which apparently
    // amounts to "some other, more stable, later position" being what's finally judged against
    // when the raidwide actually happens.
    private WPos? _lockedOrigin;
    private WPos GetOrigin() => _lockedOrigin ??= Module.PrimaryActor.Position;

    // Replay evidence across 5 separate deaths (2026-09-06, pad=0 then pad=3 then pad=6) kept
    // landing the dying player just past whatever threshold the current pad implied - including,
    // most tellingly, a 08:08 pull where the surviving teammate was ~4.9 units further from origin
    // than the pad=6 threshold required, while the player who died that same pull was only 0.87
    // units past it. The true required distance keeps turning out bigger than whatever we'd set,
    // and isn't cleanly reproducible from replay data alone (no single simple formula fit all 5
    // pulls' numbers). Rather than keep guessing at an exact figure, bumped hard to 10 units - this
    // is farm content, not progression content, so a safe region that's smaller/more conservative
    // than the true one costs nothing but a bit of positioning convenience.
    private const float BlockerSafetyPad = 10f;

    // 2026-09-06 08:27 pull: with the radial-only pad above (and the angle-degeneration bug from
    // the previous version fixed), BOTH players still died - and both missed on the ANGULAR check,
    // not the radial one. Garula's corpse was far enough from the origin that its true angular
    // half-width (asin(radius/distance)) was only ~12.3 degrees; one player was 13.25 degrees off
    // center (missed by under 1 degree), the other was 28.8 degrees off (missed badly). Padding
    // only the radial distance can never fix an angular miss - the two dimensions are independent.
    // Add a flat angular buffer the same way, widening HalfWidth directly rather than deriving it
    // from a fudged distance/radius (which is what caused the degeneration bug in the first place).
    //
    // 2026-09-06: user confirmed the mechanic itself (hide behind Garula's corpse from the dragon,
    // or die) - the direction is right, but replay evidence kept disagreeing with this component's
    // exact idea of "how wide is behind" no matter which point was used as origin (boss vs Helper)
    // or how the pad was tuned - including cases where the two theories flatly contradicted each
    // other on the same pull. Rather than keep chasing an exact figure that replay data hasn't
    // pinned down, widened hard to 45 degrees - a much more generous "roughly behind it" instead of
    // a narrow, easy-to-miss cone.
    //
    // 2026-09-06 (later, after the origin-locking fix): with the origin finally holding still for
    // the whole countdown (confirmed via screen recording - the wedge stopped rotating), the user
    // was still dying while hugging right up against the boundary line. Bumped further to 60
    // degrees - the boundary itself apparently isn't a safe place to stand even now that it's
    // stable, so make the region it bounds bigger rather than expecting pixel-perfect positioning
    // against a line on a small radar.
    private static readonly Angle AngularSafetyPad = 60f.Degrees();
    private static readonly Angle MaxHalfWidth = 89f.Degrees(); // stay just under a full half-plane

    private static void ApplySafetyPad(List<(float Distance, Angle Dir, Angle HalfWidth)> visibility)
    {
        for (var i = 0; i < visibility.Count; ++i)
        {
            var v = visibility[i];
            var pad = Math.Min(BlockerSafetyPad, v.Distance * 0.7f);
            var widenedHalfWidth = v.HalfWidth + AngularSafetyPad;
            visibility[i] = (Math.Max(v.Distance - pad, 0f), v.Dir, widenedHalfWidth < MaxHalfWidth ? widenedHalfWidth : MaxHalfWidth);
        }
    }

    // User-requested model: treat Garula's corpse as a shape from its head to its tail, and check
    // whether the line from you to the dragon crosses that shape - directly matching the user's own
    // description of the mechanic ("擋住自己與火龍"), rather than approximating the corpse as a
    // simple circular disc (which is what the inherited HitboxRadius-based Visibility computation
    // in Modify() does, and which never lined up with real outcomes no matter how it was padded).
    // A follow-up screenshot showed the fallen corpse isn't a thin line either - front legs splay
    // out to one side, giving it real width (the user described it as a triangle: head/tail/leg).
    // Rather than guess the exact irregular triangle, model it as an oriented rectangle (length
    // along the head-tail axis, some width perpendicular to it) and take all 4 corners into account
    // - this covers the same kind of "bulges out to the side" case without needing the leg's exact
    // position. No exact model dimensions are available, so both dimensions below are generous
    // estimates, not measured data - bigger only makes the safe wedge more forgiving, never less.
    private const float GarulaHalfBodyLength = 6f;
    private const float GarulaHalfBodyWidth = 4f;

    private static WPos[] GarulaBodyCorners(Actor garula)
    {
        var facing = garula.Rotation.ToDirection();
        var perp = new WDir(facing.Z, -facing.X); // rotate 90 degrees
        var lenOffset = GarulaHalfBodyLength * facing;
        var widthOffset = GarulaHalfBodyWidth * perp;
        // Perimeter order (head+w, tail+w, tail-w, head-w) so callers that draw the outline as
        // consecutive edges (corners[i] to corners[i+1]) get an actual rectangle, not a bowtie of
        // crossed diagonals; BodyVisibility below doesn't care about ordering, only min/max.
        return
        [
            garula.Position + lenOffset + widthOffset,
            garula.Position - lenOffset + widthOffset,
            garula.Position - lenOffset - widthOffset,
            garula.Position + lenOffset - widthOffset,
        ];
    }

    // A line from origin to some point P crosses the body shape exactly when P sits behind the
    // shape as seen from origin, within the angular span the shape's corners subtend - this reduces
    // to the same (distance, direction, half-width) shape the rest of this component already works
    // with, just computed from the widest angular spread across all 4 corners instead of a disc's
    // radius. Using the corner angles (rather than asin(radius/distance) on the body's center)
    // naturally accounts for the corpse's actual orientation and width - broadside-on to the origin,
    // it blocks a much wider angle than a simple hitbox radius would suggest; head/tail-on, narrower.
    private static (float Distance, Angle Dir, Angle HalfWidth) BodyVisibility(WPos origin, WPos[] corners)
    {
        var minDist = float.MaxValue;
        // Measure every corner's angle as a signed offset from the first corner's angle
        // (DistanceToAngle normalizes to (-180,180], so this stays well-behaved regardless of
        // where the raw angles happen to wrap around +-180 - Garula's body is small and local, so
        // the true angular spread is always far short of a full circle) - then the min/max offset
        // directly gives the widest arc that covers all 4 corners, without the wraparound bugs a
        // naive incremental min/max-angle merge would have.
        var refDir = Angle.FromDirection(corners[0] - origin);
        var minOffset = 0f;
        var maxOffset = 0f;
        for (var i = 0; i < corners.Length; ++i)
        {
            var toCorner = corners[i] - origin;
            var dist = toCorner.Length();
            if (dist < minDist)
                minDist = dist;
            var offset = refDir.DistanceToAngle(Angle.FromDirection(toCorner)).Rad;
            if (offset < minOffset)
                minOffset = offset;
            if (offset > maxOffset)
                maxOffset = offset;
        }
        var halfWidth = (maxOffset - minOffset) / 2f;
        var centerDir = refDir + ((minOffset + maxOffset) / 2f).Radians();
        return (minDist, centerDir, halfWidth.Radians());
    }

    private void RebuildSegmentVisibility(WPos origin, List<Actor> garulas)
    {
        Visibility.Clear();
        var count = garulas.Count;
        for (var i = 0; i < count; ++i)
            Visibility.Add(BodyVisibility(origin, GarulaBodyCorners(garulas[i])));
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            // Garula's corpse blocks line of sight the same as a living one - the game doesn't
            // despawn it until well after this resolves, so don't filter out the dead here.
            var origin = GetOrigin();
            var garulas = Module.Enemies(OID.Garula);
            Modify(origin, garulas.Select(e => (Position: e.Position, HitboxRadius: e.HitboxRadius)), WorldState.FutureTime(7));
            RebuildSegmentVisibility(origin, garulas);
            ApplySafetyPad(Visibility);
            Resolved = false;
            // Modify() only updates the raw origin/blocker/angle bookkeeping - it does NOT build
            // the actual donut-segment safe shape that ActiveAOEs/AI movement/dodge hints read
            // (Safezones). That shape is normally built by AddSafezone(), which the base class
            // only calls from OnCastStarted/OnCastFinished - hooks this component intentionally
            // bypasses because this action has no real cast bar. Replay evidence confirmed this
            // was the real bug: a player computed as ~2.5 degrees inside the "safe" angle by the
            // raw Visibility data still died, because Safezones was empty the whole time - there
            // was never an actual safe-zone shape for anything to avoid. Call AddSafezone() here
            // to actually register it.
            Safezones.Clear();
            AddSafezone(NextExplosion);
        }

        if (spell.Action.ID == (uint)AID.KingOfTheSkies)
        {
            Modify(null, []);
            Safezones.Clear();
            Resolved = true;
            _lockedOrigin = null; // re-lock fresh next time (if this mechanic ever repeats this pull)
        }
    }

    // Garula visibly moves during the ~7s window between the instant-cast trigger and the
    // actual resolve, so a one-time snapshot of its position (taken only in OnEventCast above)
    // goes stale by the time the raidwide resolves - replay math showed players ending up
    // 37-68 degrees off from the real safe cone at resolve time despite Garula being alive the
    // whole time. Recompute the safe zone every frame against Garula's current position instead -
    // and the origin's too (see GetOrigin), in case the Helper actor gets repositioned again for
    // some other mechanic before actually casting King of the Skies.
    public override void Update()
    {
        if (Origin != null && !Resolved)
        {
            var origin = GetOrigin();
            var garulas = Module.Enemies(OID.Garula);
            Modify(origin, garulas.Select(e => (Position: e.Position, HitboxRadius: e.HitboxRadius)), NextExplosion);
            RebuildSegmentVisibility(origin, garulas);
            ApplySafetyPad(Visibility);
            // Safezones[0] is what ActiveAOEs actually returns - it has to be rebuilt every frame
            // alongside Modify(), not just built once, or it goes stale the same way the raw data
            // used to before Update() was added.
            Safezones.Clear();
            AddSafezone(NextExplosion);
        }
    }

    // User asked to see the raw geometry this component is judging against, not just trust the
    // final safe/unsafe verdict - draws on top of the inherited safe-zone shape (which the base
    // class already renders from Safezones in Colors.SafeFromAOE). Origin (the dragon, per the
    // user's own description of the mechanic) as a marker, the 4-corner body rectangle standing in
    // for Garula's actual (wider than a line) silhouette, and rays from origin out to each corner
    // (the raw, unpadded cone this is built from, before ApplySafetyPad widens it).
    public override void DrawArenaBackground(int pcSlot, Actor pc)
    {
        base.DrawArenaBackground(pcSlot, pc);
        if (Origin is not { } origin)
            return;

        Arena.AddCircle(origin, 1f, Colors.Object, 2f);
        foreach (var garula in Module.Enemies(OID.Garula))
        {
            var corners = GarulaBodyCorners(garula);
            for (var i = 0; i < corners.Length; ++i)
            {
                Arena.AddLine(corners[i], corners[(i + 1) % corners.Length], Colors.Danger, 3f);
                Arena.AddCircle(corners[i], 0.4f, Colors.Safe, 2f);
                Arena.AddLine(origin, corners[i], Colors.Other1, 1f);
            }
        }
    }
}

class Adds(BossModule module) : Components.AddsMulti(module, [(uint)OID.SteppeYamaa, (uint)OID.SteppeYamaa1, (uint)OID.SteppeSheep, (uint)OID.SteppeCoeurl, (uint)OID.Garula]);

class TargetHints(BossModule module) : BossComponent(module)
{
    private Actor? Tail;
    private uint _prioritizedGarulaNameId;
    private uint _prioritizedTailNameId;
    private bool _blacklistedBossForTail;
    // Boss blacklisted because HP already crashed below the threshold *before* King of the Skies
    // - see the comment on that block below. Separate flag from _blacklistedBossForTail since the
    // two windows are mutually exclusive but independently tracked/removed.
    private bool _blacklistedBossPreKots;
    // Adds temporarily blacklisted while Garula is up (see the comment on that block below) - a
    // set rather than a single id/flag because several different add OIDs can be alive at once.
    private readonly HashSet<uint> _blacklistedAddNameIds = [];
    private static readonly uint[] AddOIDs = [(uint)OID.SteppeYamaa, (uint)OID.SteppeYamaa1, (uint)OID.SteppeSheep, (uint)OID.SteppeCoeurl];
    // Everything blacklisted during the King of the Skies countdown (see the comment on that block
    // below) - includes the boss itself, not just adds, since there's no reason to keep attacking
    // anything at all during this window.
    private readonly HashSet<uint> _blacklistedForKotsCountdown = [];
    private static readonly uint[] KotsCountdownBlacklistOIDs = [(uint)OID.SteppeYamaa, (uint)OID.SteppeYamaa1, (uint)OID.SteppeSheep, (uint)OID.SteppeCoeurl, (uint)OID.Garula, (uint)OID.Boss];
    // Sticky once observed - don't want to revert back to "leave the tail alone" just because the
    // gauge (or the stun that used to gate this) isn't showing 100 anymore by the time we check.
    // See the comment below on why this gates the tail-switch instead of switching the instant
    // the tail becomes targetable.
    private bool _toppled;
    private int _toppleGaugeValue = -1; // -1 = not currently readable; for the status readout only
    private readonly Ex5RathalosConfig _config = Service.Config.Get<Ex5RathalosConfig>();
    // Sticky once observed, same reasoning as _toppled - KingOfTheSkies.Origin is only non-null
    // during the brief ~7s pre-resolve window and goes back to null the instant it resolves, so
    // checking it directly would wrongly turn the throttle back OFF right as the gauge-building
    // window begins. This instead latches on once .Resolved has been seen true at least once.
    private bool _kotsHasResolvedOnce;

    // The "墜地" (topple) gauge is FFXIV's generic, content-reused "_ContentGauge" HUD element -
    // it has no FFXIVClientStructs struct of its own, so this reads its node tree directly. Node
    // layout confirmed via Dalamud's addon inspector (/xldata ui) on a live pull: NodeList[6] is
    // the numeric value text ("12"), NodeList[7] is the label text ("墜地"). Deliberately reading
    // the raw gauge instead of keying off the SID.Toppled stun status that fires once it hits
    // 100: the stun is a downstream *consequence* of the gauge completing, one more step removed
    // and one tick later than reading the number directly - reading the gauge itself is the more
    // direct, immediate signal. Guards on the label text matching "墜地" before trusting the
    // value, in case this shared addon is ever showing something unrelated when read.
    private static unsafe bool TryReadToppleGauge(out int value)
    {
        value = 0;
        var addon = (AtkUnitBase*)Service.GameGui.GetAddonByName("_ContentGauge").Address;
        if (addon == null || !addon->IsVisible)
            return false;

        var nodes = addon->UldManager.NodeList;
        if (nodes == null || addon->UldManager.NodeListCount < 8)
            return false;

        var labelNode = (AtkTextNode*)nodes[7];
        var valueNode = (AtkTextNode*)nodes[6];
        if (labelNode == null || valueNode == null || labelNode->NodeText.ToString() != "墜地")
            return false;

        return int.TryParse(valueNode->NodeText.ToString(), out value);
    }

    public override void Update()
    {
        if (TryReadToppleGauge(out var gaugeValue))
        {
            _toppleGaugeValue = gaugeValue;
            if (gaugeValue >= 100)
                _toppled = true;
        }
        else
        {
            _toppleGaugeValue = -1;
        }

        // Belt-and-suspenders: user reported the boss visibly toppled/stunned (tail should already
        // be priority) but the tail switch didn't happen. Root cause is almost certainly that the
        // "_ContentGauge" addon can hide itself the instant the topple triggers, racing against our
        // poll - it's entirely possible the last value we manage to read is 97-99 and we never see
        // a literal 100 before IsVisible flips false, so _toppled never gets set from the gauge
        // alone. SID.Toppled (the ~20s stun) is the actual, guaranteed-present consequence of the
        // gauge completing, so check for it directly as a second, independent way to reach the same
        // conclusion - whichever signal arrives first wins.
        if (Module.PrimaryActor.FindStatus(SID.Toppled) != null)
            _toppled = true;

        if (!_kotsHasResolvedOnce && (Module.FindComponent<KingOfTheSkies>()?.Resolved ?? false))
            _kotsHasResolvedOnce = true;

        // Output-throttling via RSR's ActionCommand (forcing a single weak GCD every tick) was
        // removed 2026-09-06: it never reliably suppressed RSR's own rotation (replay evidence
        // showed anywhere from ~50% to ~90% of GCDs still going out as full-strength combos despite
        // the constant re-arming), and the continuous IPC calls needed to even attempt it flooded
        // the Dalamud log ("IPC ActionCommand was called..." every tick) for no real benefit. What's
        // kept below - blacklisting the boss outright before King of the Skies when HP is already
        // critical, and the tail-priority/topple-gauge tracking elsewhere in this class - don't
        // depend on this at all.
        var bossLowHp = Module.PrimaryActor.HPRatio < _config.StopAttackHpPercent / 100f;

        // If HP already crashed below the threshold *before* King of the Skies has even happened,
        // the whole gauge/tail mechanic hasn't even started yet, so there's nothing to gain from
        // hitting the boss at all right now. Blacklist the boss outright so RSR is forced off it
        // entirely; it'll naturally redirect onto whatever else is up (Garula once it appears - the
        // adds-blacklist-while-Garula-is-up block below already makes sure that lands on Garula
        // specifically instead of leftover trash). Lifted the moment either KotS resolves or HP
        // recovers back above the threshold.
        if (_config.FarmScales && !_kotsHasResolvedOnce && bossLowHp && !_blacklistedBossPreKots)
        {
            RSRPriority.AddBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossPreKots = true;
        }
        else if ((!_config.FarmScales || _kotsHasResolvedOnce || !bossLowHp) && _blacklistedBossPreKots)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossPreKots = false;
        }

        // User-reported: even with AIHints priority forbidding every enemy during the King of the
        // Skies countdown (see AddAIHints below), the character kept fighting adds right up until
        // it resolved and never moved to safety afterward - because AIHints.SetPriority only steers
        // BossMod's own AI-mode targeting, and this fight is normally driven by RSR for combat,
        // which doesn't read it at all (same root cause as the Garula/tail-focus fixes above:
        // priority hints are invisible to RSR, only its own blacklist IPC actually stops it).
        // Blacklist everything attackable for real via RSR's IPC for the whole countdown window
        // (from the visual cast to the actual resolve) so RSR has nothing to swing at and the AI
        // movement module actually gets a chance to reposition instead of finishing off trash.
        // Always on regardless of FarmScales - this is a basic survival fix, not a farming option.
        var kotsCountdownActive = Module.FindComponent<KingOfTheSkies>() is { Origin: not null, Resolved: false };
        if (kotsCountdownActive)
        {
            foreach (var enemy in Module.Enemies(KotsCountdownBlacklistOIDs))
                if (!enemy.IsDead && _blacklistedForKotsCountdown.Add(enemy.NameID))
                    RSRPriority.AddBlacklist(enemy.NameID);
        }
        else if (_blacklistedForKotsCountdown.Count > 0)
        {
            foreach (var nameId in _blacklistedForKotsCountdown)
                RSRPriority.RemoveBlacklist(nameId);
            _blacklistedForKotsCountdown.Clear();
        }

        // rush Garula down as soon as it's up and alive - see the comment on RSRPriority for why
        // this is a priority-add, not the old blacklist-until-resolved approach.
        //
        // Gated on IsTargetable, not just "exists and isn't dead": the actor entity can be
        // allocated (and show up in Module.Enemies) briefly before it's actually targetable/visible
        // to the player, which was making both the RSR priority-add and the "加魯拉優先擊殺中" hint
        // fire before Garula had actually appeared (user-reported).
        var garula = Module.Enemies(OID.Garula).FirstOrDefault(e => !e.IsDead && e.IsTargetable);
        if (garula != null && _prioritizedGarulaNameId != garula.NameID)
        {
            if (_prioritizedGarulaNameId != default)
                RSRPriority.Remove(_prioritizedGarulaNameId);
            _prioritizedGarulaNameId = garula.NameID;
            RSRPriority.Add(_prioritizedGarulaNameId);
        }
        else if (garula == null && _prioritizedGarulaNameId != default)
        {
            RSRPriority.Remove(_prioritizedGarulaNameId);
            _prioritizedGarulaNameId = default;
        }

        // Priority-add alone doesn't stop RSR from clearing the trash adds first if that looks
        // like the more efficient AoE play (user-reported: adds got cleared with a big AoE cast
        // while Garula ran out of time and escaped). Same fix as the tail/boss below: blacklist the
        // adds while Garula is up so RSR has nothing else worth switching to and stays on Garula.
        // Lifted the instant Garula is gone (dead or fled) so the adds can be mopped up normally.
        if (garula != null)
        {
            foreach (var add in Module.Enemies(AddOIDs))
                if (!add.IsDead && _blacklistedAddNameIds.Add(add.NameID))
                    RSRPriority.AddBlacklist(add.NameID);
        }
        else if (_blacklistedAddNameIds.Count > 0)
        {
            foreach (var nameId in _blacklistedAddNameIds)
                RSRPriority.RemoveBlacklist(nameId);
            _blacklistedAddNameIds.Clear();
        }

        // Same fix as Garula, same reason: AddAIHints' hints.SetPriority(Tail, 1) below only
        // steers BossMod's own AI-mode targeting - it does nothing for RSR, which is what
        // actually drives combat in the AutoDuty+RSR setup this fight is normally run under.
        //
        // Gated on _toppled (SID.Toppled, the ~20s stun) rather than just "tail is targetable":
        // replay evidence shows the tail can become targetable up to ~17s before the boss is
        // actually staggered, and the stagger itself doesn't happen until ~162s after King of the
        // Skies applies its own status to the boss (SID.KingOfTheSkiesMode) - presumably an
        // accumulating gauge fed by damage dealt to the boss body. Switching off the boss the
        // moment the tail appears would pull DPS away from whatever's filling that gauge, slowing
        // down reaching the stagger in the first place. Wait for the actual stagger, then rush
        // the tail - matching the real Monster Hunter convention this fight is modeled on
        // (parts break easiest, or only, during a topple).
        var tailUp = Tail != null && !Tail.IsDead;
        if (_toppled && tailUp && _prioritizedTailNameId != Tail!.NameID)
        {
            if (_prioritizedTailNameId != default)
                RSRPriority.Remove(_prioritizedTailNameId);
            _prioritizedTailNameId = Tail.NameID;
            RSRPriority.Add(_prioritizedTailNameId);
        }
        else if ((!_toppled || !tailUp) && _prioritizedTailNameId != default)
        {
            RSRPriority.Remove(_prioritizedTailNameId);
            _prioritizedTailNameId = default;
        }

        // Priority alone doesn't force RSR off the boss it's already engaged with (see the long
        // comment on RSRPriority.AddBlacklist) - actually blacklist the boss body itself for the
        // short window the tail is up, so RSR has nothing else eligible to hit and switches.
        // Same _toppled gate as above, same reason.
        if (_toppled && tailUp && !_blacklistedBossForTail)
        {
            RSRPriority.AddBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossForTail = true;
        }
        else if ((!_toppled || !tailUp) && _blacklistedBossForTail)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossForTail = false;
        }
    }

    // live status readout so this can be confirmed at a glance in-game instead of having to open
    // the Dalamud log every time - shows for as long as the RSR priority flag is actually set,
    // and disappears the instant Garula is confirmed dead (see Update() above) or never spawned.
    public override void AddGlobalHints(GlobalHints hints)
    {
        if (_prioritizedGarulaNameId != default)
            hints.Add("加魯拉優先擊殺中");
        if (_blacklistedBossPreKots)
            hints.Add("血量偏低,暫停攻擊本體等待天空王者/加魯拉");
        // Informational only - no throttling happens based on this anymore (see Update()), just
        // shows the raw gauge progress once King of the Skies has resolved and it isn't full yet.
        if (_kotsHasResolvedOnce && !_toppled && _toppleGaugeValue >= 0)
            hints.Add($"墜地量表 {_toppleGaugeValue}/100");
        if (_prioritizedTailNameId != default)
            hints.Add("尾巴優先擊殺中");
    }

    public override void OnActorTargetable(Actor actor)
    {
        if (actor.OID == (uint)OID.WyvernsTail)
            Tail = actor;
    }

    public override void OnActorUntargetable(Actor actor)
    {
        if (actor.OID == (uint)OID.WyvernsTail)
            Tail = null;
    }

    // Safety net: if the boss dies while the tail is still up (blacklist still active), the
    // module deactivates and Update() may never get another tick to clean it up itself - which
    // would leave the boss's NameID blacklisted in RSR forever, breaking every future pull of
    // this same fight. Force the cleanup directly off the boss's own death event instead of
    // relying solely on Update() noticing next frame.
    public override void OnActorDeath(Actor actor)
    {
        if (actor == Module.PrimaryActor && _blacklistedBossForTail)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossForTail = false;
        }

        // Same leak concern, for the pre-KotS low-HP boss blacklist.
        if (actor == Module.PrimaryActor && _blacklistedBossPreKots)
        {
            RSRPriority.RemoveBlacklist(Module.PrimaryActor.NameID);
            _blacklistedBossPreKots = false;
        }

        // Same leak concern as above, for the adds-blacklist-while-chasing-Garula fix: if the pull
        // ends (boss dies) while adds are still blacklisted, don't leave that stuck for next pull.
        if (actor == Module.PrimaryActor && _blacklistedAddNameIds.Count > 0)
        {
            foreach (var nameId in _blacklistedAddNameIds)
                RSRPriority.RemoveBlacklist(nameId);
            _blacklistedAddNameIds.Clear();
        }

        // Same leak concern, for the King of the Skies countdown blacklist.
        if (actor == Module.PrimaryActor && _blacklistedForKotsCountdown.Count > 0)
        {
            foreach (var nameId in _blacklistedForKotsCountdown)
                RSRPriority.RemoveBlacklist(nameId);
            _blacklistedForKotsCountdown.Clear();
        }
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        // User-reported: once Garula is dead and King of the Skies is about to resolve, the AI
        // sometimes keeps fighting nearby trash instead of immediately repositioning, and dies to
        // it. The countdown is running the whole time Origin is set and not yet Resolved (7s window
        // between the visual cast and the actual explosion) - forbid every enemy for that entire
        // window so the AI has nothing worth attacking and only cares about getting to safety.
        if (Module.FindComponent<KingOfTheSkies>() is { Origin: not null, Resolved: false })
        {
            foreach (var enemy in hints.PotentialTargets)
                hints.SetPriority(enemy.Actor, AIHints.Enemy.PriorityForbidden);
        }

        if (actor.HPRatio < 0.25f)
            hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.MegaPotion), null, ActionQueue.Priority.VeryHigh);

        if (_toppled && Tail != null && !Tail.IsDead)
        {
            hints.SetPriority(Tail, 1);
            hints.SetPriority(Module.PrimaryActor, AIHints.Enemy.PriorityForbidden);
        }

        // rush Garula down - its corpse blocks line of sight for King of the Skies just as well
        // as a living one, and killing it fast keeps the eventual safe spot close to the party
        foreach (var garula in Module.Enemies(OID.Garula))
            if (!garula.IsDead)
                hints.SetPriority(garula, 1);
    }
}

class Ex5RathalosStates : StateMachineBuilder
{
    public Ex5RathalosStates(BossModule module) : base(module)
    {
        TrivialPhase()
            .ActivateOnEnter<Mangle>()
            .ActivateOnEnter<GarulaRush>()
            .ActivateOnEnter<Lullaby>()
            .ActivateOnEnter<HeadButt>()
            .ActivateOnEnter<Rush>()
            .ActivateOnEnter<TailSwing>()
            .ActivateOnEnter<Mangle2>()
            .ActivateOnEnter<FireballStack1>()
            .ActivateOnEnter<FireballStack2>()
            .ActivateOnEnter<FirePuddle>()
            .ActivateOnEnter<KingOfTheSkies>()
            .ActivateOnEnter<SweepingFlames>()
            .ActivateOnEnter<Adds>()
            .ActivateOnEnter<TargetHints>();
    }
}

// Maturity.Contributed, not WIP: the default BossModuleConfig.MinMaturity threshold is
// Contributed, and a module below that threshold is registered (shows up in the "supported
// battles" list) but never actually activated during combat - which is exactly why every
// diagnostic log added while chasing this bug came back silent. Confirmed by reproducing the
// exact registry gate in BossModuleRegistry.CreateModuleForActor (info.Maturity >= minMaturity).
[ModuleInfo(BossModuleInfo.Maturity.Contributed, Contributors = "ported from awgil/ffxiv_bossmod", GroupType = BossModuleInfo.GroupType.CFC, GroupID = 475, NameID = 7221)]
public class Ex5Rathalos(WorldState ws, Actor primary) : BossModule(ws, primary, new(100, 100), new ArenaBoundsCircle(24.5f));
