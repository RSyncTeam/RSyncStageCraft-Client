using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Location;

/// <summary>
/// One scene per place: the scene ID is derived from the complete location,
/// so every player standing in the same place creates into the same scene
/// without asking the server, and a create can never name a scene of another
/// place (which the server would refuse as a location mismatch anyway).
/// </summary>
public static class LocationScenes
{
    private const string Namespace = "rsc-scene-location:v1:";

    public static RscSceneId? SceneFor(SceneLocation location)
    {
        if (!location.IsValid)
            return null;
        var key = Namespace + Canonical(location);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var bytes = hash.AsSpan(0, 16).ToArray();
        // RFC 9562 layout: version 8 (custom), variant 10.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new RscSceneId(new Guid(bytes, bigEndian: true));
    }

    /// <summary>Stable text form of a location; every field takes part.</summary>
    public static string Canonical(SceneLocation location)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"t={location.TerritoryId}");
        if (location.Housing is { } housing)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $";w={housing.WorldId};ward={housing.Ward};plot={housing.Plot};room={housing.Room};div={housing.Division};ws={(housing.Workshop ? 1 : 0)}");
        }
        return builder.ToString();
    }
}

/// <summary>
/// A furniture/light object is visible here when its scene location equals
/// the player's current location exactly. Fails closed: an unknown local
/// location, or an object without a location, is never visible.
/// </summary>
public sealed class CurrentLocationVisibility : ILocalObjectVisibility
{
    private readonly Func<SceneLocation?> current;

    public CurrentLocationVisibility(Func<SceneLocation?> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        this.current = current;
    }

    public bool IsVisibleHere(AuthoritativeObjectState state)
    {
        if (state?.Location is not { IsValid: true } location)
            return false;
        SceneLocation? here;
        try
        {
            here = current();
        }
        catch (Exception)
        {
            return false;
        }
        return here is { IsValid: true } place && place == location;
    }
}

/// <summary>
/// Turns raw location samples into scene changes. Only a change of the
/// normalized scene location counts: outdoors the plot under the player is
/// not part of it, so walking from a plot to the street to another plot of
/// the same ward division changes nothing, while another division, ward,
/// house, room or zone does. Raised on the caller's (game) thread.
/// </summary>
public sealed class SceneChangeWatcher
{
    private readonly object gate = new();
    private readonly Action<SceneLocation?> changed;
    private SceneLocation? current;
    private bool observed;

    public SceneChangeWatcher(Action<SceneLocation?> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        this.changed = changed;
    }

    /// <summary>The current scene location, or null while unknown.</summary>
    public SceneLocation? Current
    {
        get
        {
            lock (gate)
                return current;
        }
    }

    /// <summary>Returns true (after notifying) when the scene location changed.</summary>
    public bool Observe(ClientLocation? location)
    {
        SceneLocation? scene;
        try
        {
            scene = location?.ToSceneLocation();
        }
        catch (Exception)
        {
            scene = null;
        }
        lock (gate)
        {
            if (observed && scene == current)
                return false;
            observed = true;
            current = scene;
        }
        changed(scene);
        return true;
    }
}
