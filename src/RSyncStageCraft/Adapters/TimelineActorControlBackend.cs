using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using RSyncStageCraft.Core.Actors;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace RSyncStageCraft.Adapters;

/// <summary>
/// Animation primitives: public typed ClientStructs members only,
/// resolved by Dalamud's ClientStructs resolver. No hooks, no RSC-owned
/// signatures and no raw offsets. Acts only on the address
/// <see cref="ActorLocalObjectHost"/> has just proved is its own client-only
/// puppet; nothing here touches the player, targeting, chat or the network.
/// Game thread only.
/// <list type="bullet">
/// <item>Each method is <b>one</b> native write or call, so the host proves
/// ownership immediately before every one of them.</item>
/// <item><b>Animation:</b> <c>Character.SetMode</c>,
/// <c>TimelineContainer.BaseOverride</c>,
/// <c>ActionTimelineSequencer.PlayTimeline</c> and the slot
/// <c>TimelineIds</c>.</item>
/// </list>
/// RSC has no freeze and no look-at (removed):
/// this backend never reads or writes a speed or any look-at member.
/// </summary>
internal sealed unsafe class TimelineActorControlBackend : IActorControlBackend
{
    public ActorControlAvailability Availability => new(
        GameCharacter.Addresses.SetMode.Value != 0 && ActionTimelineSequencer.Addresses.PlayTimeline.Value != 0
            ? null
            : ActorLocalObjectHost.AnimationUnavailableReason);

    public void SetAnimationLock(nint address, bool locked) =>
        Character(address)->SetMode(locked ? CharacterModes.AnimLock : CharacterModes.Normal, 0);

    public void SetBaseOverride(nint address, ushort timelineId) =>
        Character(address)->Timeline.BaseOverride = timelineId;

    public void PlayTimeline(nint address, ushort timelineId) =>
        Character(address)->Timeline.TimelineSequencer.PlayTimeline(timelineId);

    public IReadOnlyList<ushort> ReadActiveTimelines(nint address) =>
        Character(address)->Timeline.TimelineSequencer.TimelineIds.ToArray();

    /// <summary>
    /// Read-only, public typed members: a timeline slot holds a row
    /// (<c>TimelineIds</c>) and the body's main skeleton has a bound animation
    /// control (<see cref="ReadAnimationSample"/>).
    /// </summary>
    public bool IsAnimationReady(nint address)
    {
        var ids = Character(address)->Timeline.TimelineSequencer.TimelineIds;
        var running = false;
        foreach (var id in ids)
        {
            if (id != 0)
            {
                running = true;
                break;
            }
        }
        if (!running)
            return false;
        var sample = ReadAnimationSample(address);
        return sample.Readable && sample.Controls.Any(control => control.Bound);
    }

    /// <summary>
    /// Per-slot scheduler state through public typed members:
    /// <c>ActionTimelineSequencer.GetSchedulerTimeline</c>, the timeline's
    /// <c>ActionTimelineKey</c> and <c>CurrentTimestamp</c>, and its
    /// <c>SchedulerResource</c> handle's file name and load state.
    /// </summary>
    public IReadOnlyList<ActorTimelineSlotState> ReadTimelineSlots(nint address)
    {
        var sequencer = &Character(address)->Timeline.TimelineSequencer;
        var ids = sequencer->TimelineIds;
        var slots = new List<ActorTimelineSlotState>(ids.Length);
        for (var slot = 0; slot < ids.Length; slot++)
        {
            var timeline = sequencer->GetSchedulerTimeline((uint)slot);
            if (timeline == null)
            {
                slots.Add(new(slot, ids[slot], false, null, null, null, null));
                continue;
            }
            string? resource = null;
            byte? loadState = null;
            var loaded = false;
            var scheduled = timeline->SchedulerResource;
            if (scheduled != null && scheduled->Resource != null)
            {
                var handle = scheduled->Resource;
                resource = handle->FileName.ToString();
                loadState = handle->LoadState;
                // Loaded = the handle's data is present (public virtual
                // functions; the raw load-state byte has no typed meaning).
                loaded = handle->GetData() != null && handle->GetLength() > 0;
            }
            slots.Add(new(slot, ids[slot], true, timeline->ActionTimelineKey.ToString(),
                ((TimelineController*)timeline)->CurrentTimestamp, resource, loadState, loaded));
        }
        return slots;
    }

    /// <summary>
    /// The body's main skeleton (partial skeleton 0, animated skeleton 0)
    /// Havok animation controls through public typed members: local time,
    /// weight, playback speed and the bound animation's duration.
    /// </summary>
    public ActorAnimationSample ReadAnimationSample(nint address)
    {
        var draw = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)Character(address))->DrawObject;
        if (draw == null || ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)draw)->GetObjectType() != ObjectType.CharacterBase)
            return ActorAnimationSample.Unreadable;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletons == null)
            return ActorAnimationSample.Unreadable;
        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null)
            return ActorAnimationSample.Unreadable;
        var list = animated->AnimationControls;
        var controls = new List<ActorAnimationControlState>();
        for (var index = 0; index < list.Length && index < 32; index++)
        {
            var control = list[index].Value;
            if (control == null)
                continue;
            var common = (hkaAnimationControl*)control;
            var binding = common->Binding.ptr;
            var animation = binding == null ? null : binding->Animation.ptr;
            // The identity is an in-memory key only: never shown or logged.
            controls.Add(new((ulong)(nint)control, common->LocalTime, common->Weight, control->PlaybackSpeed,
                binding != null && animation != null, animation == null ? null : animation->Duration));
        }
        return new ActorAnimationSample(true, controls);
    }

    private static GameCharacter* Character(nint address)
    {
        var character = (GameCharacter*)address;
        if (character == null)
            throw new InvalidOperationException("No actor object.");
        return character;
    }
}
