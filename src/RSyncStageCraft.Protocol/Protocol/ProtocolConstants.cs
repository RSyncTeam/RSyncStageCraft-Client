namespace RSyncStageCraft.Core.Protocol;

public static class ProtocolConstants
{
    // Single protocol version; client and server ship together.
    // Version 2 folded the correlated response messages into the protocol.
    // Version 3 makes every create carry its complete scene location,
    // adds Division and Workshop to housing locations, and returns each
    // object's scene location to clients.
    // Version 4 adds the furniture stain: a byte on furniture state
    // and create payloads (0 = Default, undyed) and the furnitureStain patch.
    // Version 5: point lights with explicit colour, intensity and range,
    // the canonical light:point identity, a fixed unit scale, and the
    // lightPoint patch replacing lightTransform.
    // Version 6: base-game non-human actors identified only by their
    // BNpcBase row, with yaw-only rotation, a bounded uniform scale, a
    // frozen flag (reserved and always false: RSC has no freeze)
    // and a world-position look-at field (reserved and always empty: RSC has
    // no look-at); the server-owned animation sequence; the
    // placeholder actor fields (model identity, actor type, resource
    // manifest, appearance references) and the actorAppearance patch removed.
    public const int CurrentVersion = 6;

    /// <summary>
    /// Exactly two players. Used only by the
    /// production composition: at most this many active access keys may be
    /// issued, at most this many accounts are admitted per server run, and
    /// the server refuses to start with more active keys than this.
    /// </summary>
    public const int MaximumGroupMembers = 2;
}
