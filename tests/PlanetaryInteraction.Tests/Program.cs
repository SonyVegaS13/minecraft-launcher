using SolarisLauncher;

// Execute the same state class used by PlanetaryHome, without requiring WPF or Windows.
// Delayed callbacks are delivered explicitly so race conditions are deterministic.
var cases = new (string Name, Action Run)[]
{
    ("Initial overview contains no information card", () =>
    {
        var state = new PlanetaryInteractionState();
        Card(state, WorldSelection.None, pinned: false);
        Equal(WorldSelection.None, state.HoveredWorld);
        Equal(true, state.IsActive);
    }),
    ("Hover opens a transient card and delayed exit closes it", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        Card(state, WorldSelection.Vanilla, pinned: false);
        var leave = state.LeaveArea(WorldSelection.Vanilla);
        Card(state, WorldSelection.Vanilla, pinned: false);
        Equal(true, state.CompleteLeave(leave));
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Planet to card handoff keeps the card visible without flicker", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        var leave = state.LeaveArea(WorldSelection.Vanilla);
        state.EnterCard(WorldSelection.Vanilla);
        Equal(false, state.CompleteLeave(leave));
        Card(state, WorldSelection.Vanilla, pinned: false);
        Equal(true, state.CompleteLeave(state.LeaveArea(WorldSelection.Vanilla)));
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Card to planet handoff also survives an old exit callback", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Modded);
        state.EnterCard(WorldSelection.Modded);
        var leave = state.LeaveArea(WorldSelection.Modded);
        state.EnterWorld(WorldSelection.Modded);
        Equal(false, state.CompleteLeave(leave));
        Card(state, WorldSelection.Modded, pinned: false);
    }),
    ("An old planet exit cannot erase a newer planet hover", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        var oldLeave = state.LeaveArea(WorldSelection.Vanilla);
        state.EnterWorld(WorldSelection.Modded);
        Equal(false, state.CompleteLeave(oldLeave));
        Equal(0L, state.LeaveArea(WorldSelection.Vanilla));
        Card(state, WorldSelection.Modded, pinned: false);
        Equal(true, state.CompleteLeave(state.LeaveArea(WorldSelection.Modded)));
    }),
    ("Only the most recent leave callback can close a transient card", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Modded);
        var firstLeave = state.LeaveArea(WorldSelection.Modded);
        state.EnterWorld(WorldSelection.Modded);
        var secondLeave = state.LeaveArea(WorldSelection.Modded);
        Equal(false, state.CompleteLeave(firstLeave));
        Card(state, WorldSelection.Modded, pinned: false);
        Equal(true, state.CompleteLeave(secondLeave));
        Equal(false, state.CompleteLeave(secondLeave));
        Equal(false, state.CompleteLeave(0));
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Pinning invalidates a pending transient close", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        var leave = state.LeaveArea(WorldSelection.Vanilla);
        state.PinWorld(WorldSelection.Vanilla);
        Equal(false, state.CompleteLeave(leave));
        Card(state, WorldSelection.Vanilla, pinned: true);
    }),
    ("Pinned card persists after pointer leaves both planet and card", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Modded);
        state.PinWorld(WorldSelection.Modded);
        Equal(true, state.CompleteLeave(state.LeaveArea(WorldSelection.Modded)));
        Equal(WorldSelection.None, state.HoveredWorld);
        Card(state, WorldSelection.Modded, pinned: true);
        state.EnterCard(WorldSelection.Modded);
        Equal(true, state.CompleteLeave(state.LeaveArea(WorldSelection.Modded)));
        Card(state, WorldSelection.Modded, pinned: true);
    }),
    ("Hovering the other planet highlights it but keeps the pinned content", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        state.PinWorld(WorldSelection.Vanilla);
        state.EnterWorld(WorldSelection.Modded);
        Equal(WorldSelection.Modded, state.HoveredWorld);
        Card(state, WorldSelection.Vanilla, pinned: true);
        Equal(true, state.CompleteLeave(state.LeaveArea(WorldSelection.Modded)));
        Equal(WorldSelection.None, state.HoveredWorld);
        Card(state, WorldSelection.Vanilla, pinned: true);
    }),
    ("Clicking the other planet switches the pinned card immediately", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        state.PinWorld(WorldSelection.Vanilla);
        var leave = state.LeaveArea(WorldSelection.Vanilla);
        state.EnterWorld(WorldSelection.Modded);
        state.PinWorld(WorldSelection.Modded);
        Equal(false, state.CompleteLeave(leave));
        Equal(WorldSelection.Modded, state.PinnedWorld);
        Card(state, WorldSelection.Modded, pinned: true);
    }),
    ("Escape or close clears hover and cannot reopen from a stale callback", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        state.PinWorld(WorldSelection.Vanilla);
        var leave = state.LeaveArea(WorldSelection.Vanilla);
        state.Close();
        Equal(false, state.CompleteLeave(leave));
        Equal(0L, state.LeaveArea(WorldSelection.Vanilla));
        Card(state, WorldSelection.None, pinned: false);
        state.EnterWorld(WorldSelection.Vanilla);
        Card(state, WorldSelection.Vanilla, pinned: false);
    }),
    ("Closing a pin also clears hover remembered on the other planet", () =>
    {
        var state = new PlanetaryInteractionState();
        state.PinWorld(WorldSelection.Vanilla);
        state.EnterWorld(WorldSelection.Modded);
        state.Close();
        Equal(WorldSelection.None, state.HoveredWorld);
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Backgrounding the view clears transient input and rejects stale events", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Vanilla);
        var leave = state.LeaveArea(WorldSelection.Vanilla);
        state.Deactivate();
        Equal(false, state.IsActive);
        state.EnterWorld(WorldSelection.Modded);
        state.EnterCard(WorldSelection.Vanilla);
        state.PinWorld(WorldSelection.Modded);
        Equal(false, state.CompleteLeave(leave));
        Equal(0L, state.LeaveArea(WorldSelection.Modded));
        Card(state, WorldSelection.None, pinned: false);
        state.Resume();
        Equal(true, state.IsActive);
        Card(state, WorldSelection.None, pinned: false);
        state.EnterWorld(WorldSelection.Modded);
        Card(state, WorldSelection.Modded, pinned: false);
    }),
    ("Backgrounding and resuming preserve a pinned card without a hover", () =>
    {
        var state = new PlanetaryInteractionState();
        state.EnterWorld(WorldSelection.Modded);
        state.PinWorld(WorldSelection.Modded);
        var leave = state.LeaveArea(WorldSelection.Modded);
        state.Deactivate();
        Equal(WorldSelection.None, state.HoveredWorld);
        Card(state, WorldSelection.Modded, pinned: true);
        state.Resume();
        Equal(false, state.CompleteLeave(leave));
        Equal(WorldSelection.None, state.HoveredWorld);
        Card(state, WorldSelection.Modded, pinned: true);
    }),
    ("Closing an inactive view still dismisses the pinned card", () =>
    {
        var state = new PlanetaryInteractionState();
        state.PinWorld(WorldSelection.Vanilla);
        state.Deactivate();
        state.Close();
        state.Resume();
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Keyboard pinning works without requiring pointer hover", () =>
    {
        var state = new PlanetaryInteractionState();
        state.PinWorld(WorldSelection.Modded);
        Equal(WorldSelection.None, state.HoveredWorld);
        Card(state, WorldSelection.Modded, pinned: true);
        state.Close();
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Invalid world input is rejected rather than opening a phantom card", () =>
    {
        var state = new PlanetaryInteractionState();
        Throws<ArgumentOutOfRangeException>(() => state.EnterWorld(WorldSelection.None));
        Throws<ArgumentOutOfRangeException>(() => state.EnterCard((WorldSelection)99));
        Throws<ArgumentOutOfRangeException>(() => state.PinWorld(WorldSelection.None));
        Throws<ArgumentOutOfRangeException>(() => state.LeaveArea((WorldSelection)99));
        Card(state, WorldSelection.None, pinned: false);
    }),
    ("Poll in progress shows checking status and unknown players", () =>
    {
        Equal("Проверяем сервер…", PlanetaryServerSummary.Presence(" Проверяем сервер..."));
        Equal("Игроки: — / 200", PlanetaryServerSummary.Players(" Проверяем сервер..."));
    }),
    ("An online server with a reported zero retains that real zero", () =>
    {
        Equal("Сервер онлайн", PlanetaryServerSummary.Presence(" Сервер онлайн · игроков: 0/200"));
        Equal("Игроки: 0 / 200", PlanetaryServerSummary.Players(" Сервер онлайн · игроков: 0/200"));
    }),
    ("Reported online counts come from the existing poll response", () =>
    {
        foreach (var count in new[] { 1, 37, 128, 200 })
            Equal($"Игроки: {count} / 200",
                PlanetaryServerSummary.Players($" Сервер онлайн · игроков: {count}/200"));
    }),
    ("An above-capacity reported count is kept without clamping", () =>
    {
        Equal("Игроки: 211 / 200", PlanetaryServerSummary.Players(" Сервер онлайн · игроков: 211/200"));
    }),
    ("An online server hiding counts never implies zero players", () =>
    {
        const string status = " Сервер онлайн · число игроков скрыто";
        Equal("Сервер онлайн", PlanetaryServerSummary.Presence(status));
        Equal("Игроки: — / 200", PlanetaryServerSummary.Players(status));
    }),
    ("An unavailable server shows unavailable status and unknown players", () =>
    {
        Equal("Сервер недоступен", PlanetaryServerSummary.Presence(" Сервер недоступен"));
        Equal("Игроки: — / 200", PlanetaryServerSummary.Players(" Сервер недоступен"));
    }),
    ("Malformed count payloads do not invent a zero", () =>
    {
        foreach (var payload in new[] { "", "128", "128/200/300", "many/200", "128/full", "1.5/200", "2147483648/200" })
            Equal("Игроки: — / 200", PlanetaryServerSummary.Players($" Сервер онлайн · игроков: {payload}"));
    }),
    ("Negative or explicitly signed player counts are treated as unknown", () =>
    {
        foreach (var count in new[] { "-1", "-0", "+1" })
            Equal("Игроки: — / 200", PlanetaryServerSummary.Players($" Сервер онлайн · игроков: {count}/200"));
    }),
    ("A mismatched capacity is unknown rather than pretending to be 200 slots", () =>
    {
        foreach (var capacity in new[] { "0", "100", "500", "-200" })
            Equal("Игроки: — / 200", PlanetaryServerSummary.Players($" Сервер онлайн · игроков: 18/{capacity}"));
    }),
    ("Missing status data remains checking with an unknown player count", () =>
    {
        Equal("Проверяем сервер…", PlanetaryServerSummary.Presence(null));
        Equal("Игроки: — / 200", PlanetaryServerSummary.Players(null));
        Equal("Игроки: — / 200", PlanetaryServerSummary.Players(string.Empty));
    }),
    ("A player fragment without online status is never presented as live data", () =>
    {
        Equal("Игроки: — / 200", PlanetaryServerSummary.Players(" Сервер недоступен · игроков: 99/200"));
    }),
    ("Poll formatting spaces and case do not alter a real count", () =>
    {
        Equal("Сервер онлайн", PlanetaryServerSummary.Presence(" СЕРВЕР ОНЛАЙН · ИГРОКОВ:  18 / 200 "));
        Equal("Игроки: 18 / 200", PlanetaryServerSummary.Players(" СЕРВЕР ОНЛАЙН · ИГРОКОВ:  18 / 200 "));
    })
};

var failures = 0;
foreach (var (name, run) in cases)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        ++failures;
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}

Console.WriteLine($"{cases.Length - failures}/{cases.Length} planetary interaction checks passed.");
return failures == 0 ? 0 : 1;

static void Card(PlanetaryInteractionState state, WorldSelection world, bool pinned)
{
    Equal(world, state.VisibleCardWorld);
    Equal(pinned, state.IsPinned);
    Equal(pinned ? world : WorldSelection.None, state.PinnedWorld);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
