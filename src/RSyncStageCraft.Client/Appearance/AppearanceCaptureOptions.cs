namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// What a capture may include beyond allowlist version 1. Every option is
/// off by default, and a Release build never turns one on: the plugin
/// composes these once, from its development switch (a Debug build loaded
/// as a dev plugin).
/// </summary>
/// <param name="AllowSkeletonGroups">
/// Capture the character's redirected human skeleton resources as one
/// skeleton group (manifest format 2), all or nothing. Off: skeleton paths
/// are excluded exactly as before.
/// </param>
/// <param name="AllowDeformer">
/// Allow the bone deformer (<c>human.pbd</c>) in a group. Off: a redirected
/// deformer refuses the whole group; it is never dropped from it.
/// </param>
public sealed record AppearanceCaptureOptions(bool AllowSkeletonGroups = false, bool AllowDeformer = false)
{
    /// <summary>Both options off: today's capture.</summary>
    public static AppearanceCaptureOptions Default { get; } = new();
}
