namespace SolarisLauncher;

public enum WorldSelection { None, Vanilla, Modded }

/// <summary>
/// UI-independent state for the two planets and their shared information card.
/// The view owns its leave timer and passes its token back to CompleteLeave.
/// All methods are intended to run on the view's UI thread.
/// </summary>
public sealed class PlanetaryInteractionState
{
    private WorldSelection _pointerWorld;
    private long _generation;
    private long _pendingLeaveToken;
    private bool _active = true;

    public WorldSelection HoveredWorld { get; private set; }
    public WorldSelection PinnedWorld { get; private set; }
    public WorldSelection VisibleCardWorld => IsPinned ? PinnedWorld : HoveredWorld;
    public bool IsPinned => PinnedWorld != WorldSelection.None;
    public bool IsActive => _active;

    public void EnterWorld(WorldSelection world) => EnterArea(world);

    public void EnterCard(WorldSelection world) => EnterArea(world);

    private void EnterArea(WorldSelection world)
    {
        ValidateWorld(world);
        if (!_active) return;
        InvalidateLeave();
        _pointerWorld = world;
        HoveredWorld = world;
    }

    /// <summary>
    /// Starts the planet/card handoff grace period without immediately closing
    /// its card. Zero means this leave belongs to an older, inactive area.
    /// </summary>
    public long LeaveArea(WorldSelection world)
    {
        ValidateWorld(world);
        if (!_active || _pointerWorld != world) return 0;
        _pointerWorld = WorldSelection.None;
        _pendingLeaveToken = ++_generation;
        return _pendingLeaveToken;
    }

    /// <summary>
    /// Applies a delayed leave only if no entry, selection, close, or lifecycle
    /// transition has happened since it was scheduled. A pinned card stays open.
    /// </summary>
    public bool CompleteLeave(long token)
    {
        if (!_active || token == 0 || token != _pendingLeaveToken || token != _generation
            || _pointerWorld != WorldSelection.None)
            return false;

        InvalidateLeave();
        HoveredWorld = WorldSelection.None;
        return true;
    }

    public void PinWorld(WorldSelection world)
    {
        ValidateWorld(world);
        if (!_active) return;
        InvalidateLeave();
        PinnedWorld = world;
    }

    /// <summary>
    /// Used by Escape and the close button. Clearing the remembered hover stops
    /// a closed pinned card from immediately reopening beneath a stationary mouse.
    /// </summary>
    public void Close()
    {
        InvalidateLeave();
        _pointerWorld = WorldSelection.None;
        HoveredWorld = WorldSelection.None;
        PinnedWorld = WorldSelection.None;
    }

    /// <summary>Clears transient input when the view/window is inactive.</summary>
    public void Deactivate()
    {
        InvalidateLeave();
        _active = false;
        _pointerWorld = WorldSelection.None;
        HoveredWorld = WorldSelection.None;
    }

    /// <summary>Resumes interaction without resurrecting a previous hover.</summary>
    public void Resume()
    {
        InvalidateLeave();
        _active = true;
    }

    private void InvalidateLeave()
    {
        ++_generation;
        _pendingLeaveToken = 0;
    }

    private static void ValidateWorld(WorldSelection world)
    {
        if (world is not (WorldSelection.Vanilla or WorldSelection.Modded))
            throw new System.ArgumentOutOfRangeException(nameof(world), world,
                "Only an actual planet can receive pointer input or be pinned.");
    }
}
