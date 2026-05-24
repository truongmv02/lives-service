# TMV Lives
![Unity](https://img.shields.io/badge/made%20with-Unity-000000?logo=unity)
![Last Commit](https://img.shields.io/github/last-commit/truongmv/lives-service)
![Repo Size](https://img.shields.io/github/repo-size/truongmv/lives-service)
![Release](https://img.shields.io/github/v/release/truongmv/lives-service)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Time-gated lives system for Unity puzzle games. Lives recover over time, infinite-lives grants override consumption, and offline catch-up is handled in a single `Tick` call on resume.

---

## Features

- **`LivesService`** — core logic with zero Unity imports; fully testable in EditMode
- **`ILivesStorage` / `ILivesNotificationScheduler`** — injectable adapters for persistence and push notifications
- **`LivesConfigProvider`** — subclass once to source config from a ScriptableObject, remote config, or A/B test provider
- **Offline catch-up** in O(1) via integer-division recovery — no while-loop, no frame-by-frame replay
- **Infinite lives** as a timed override; recovery elapsed during the grant still counts toward the next life
- **Single notification slot** — auto-scheduled to fire when lives are full (plus a 3-second grace period so a user still in the session that spent the last life is not interrupted); cancelled on refill / grant / max change

---

## Install

**Option A — `Packages/manifest.json`**

```json
{
  "dependencies": {
    "com.tmv.lives": "https://github.com/truongmv/lives-service.git"
  }
}
```

**Option B — Package Manager UI**

*Window → Package Manager → + → Add package from git URL*

```
https://github.com/truongmv/lives-service.git
```

To pin a release, append `#1.0.0`. Unity does not auto-update git packages — run *Package Manager → Update* manually when a new version ships.

---

## Usage Guide

### 1. Bootstrap

Construct and initialize the service once before any scene loads. `LivesService` is a plain C# class — wire it up in a `MonoBehaviour`, a DI container, or a static bootstrapper. The notification scheduler is optional; pass `null` on platforms without push support and the service will guard every call site.

```csharp
// GameBootstrap.cs
public sealed class GameBootstrap : MonoBehaviour
{
    [SerializeField] private LivesConfigSO _configSO;  // ScriptableObject wrapper (see Config)

    public ILivesService Lives { get; private set; }

    private void Awake()
    {
        ILivesNotificationScheduler notifications =
            Application.isMobilePlatform ? new UnityNotificationScheduler() : null;

        Lives = new LivesService(
            storage:        new PlayerPrefsLivesStorage(),
            configProvider: _configSO.ToProvider(),
            utcClock:       () => DateTime.UtcNow,
            notifications:  notifications);

        Lives.Initialize();   // sanitize state + credit offline recovery
    }
}
```

### 2. Tick every frame and on resume

`Tick` is idempotent — calling it with `dt = 0` on resume is the canonical way to force an offline catch-up.

```csharp
private void Update() => _lives.Tick(Time.deltaTime);

private void OnApplicationPause(bool paused)
{
    if (!paused) _lives.Tick(0f);   // force catch-up on resume from background
}
```

### 3. Spend a life before a level attempt

`ConsumeLife` returns `false` in two cases: `Lives == 0` (infinite not active), and `HasInfiniteLives` active (count unchanged). Check `HasInfiniteLives` first so the "no lives" popup is not shown unnecessarily.

```csharp
public bool TryStartLevel()
{
    bool canStart = _lives.HasInfiniteLives || _lives.ConsumeLife();
    if (!canStart) { ShowNoLivesPopup(); return false; }

    LoadLevel();
    return true;
}
```

### 4. Display the count and countdown

`TimeUntilNextLife` returns `TimeSpan.Zero` when full or before recovery has started. Check `HasInfiniteLives` separately — the property does not return Zero during an active grant — render it conditionally:

```csharp
private void Update()
{
    _countLabel.text = _lives.Lives.ToString();

    if (_lives.IsFull || _lives.HasInfiniteLives)
    {
        _timerLabel.text = string.Empty;
        return;
    }

    var t = _lives.TimeUntilNextLife;
    _timerLabel.text = $"{t.Minutes:D2}:{t.Seconds:D2}";
}
```

### 5. Grant infinite lives (rewarded ad)

A second `GrantInfinite` call replaces the previous expiry rather than stacking.

```csharp
_lives.GrantInfinite(1800);   // 30 minutes
```

### 6. Refill or add lives (IAP)

```csharp
_lives.RefillLives();   // fill to MaxLives, stop recovery
_lives.AddLives(5);     // add a fixed amount, capped at MaxLives
```

### 7. Apply a VIP cap bonus

`SetMaxLives` clamps the current `Lives` down if the new cap is smaller, and clears the recovery timer if the change leaves the player full. No reinitialize required.

```csharp
_lives.SetMaxLives(8);
```

### 8. React to state changes

Always pair the subscribe/unsubscribe to the GameObject lifecycle to avoid leaks:

```csharp
private void OnEnable()
{
    _lives.OnLivesChanged         += HandleLivesChanged;
    _lives.OnInfiniteLivesChanged += HandleInfiniteLivesChanged;
}

private void OnDisable()
{
    _lives.OnLivesChanged         -= HandleLivesChanged;
    _lives.OnInfiniteLivesChanged -= HandleInfiniteLivesChanged;
}

private void HandleLivesChanged(int newCount)     { RefreshHud(newCount); }
private void HandleInfiniteLivesChanged()         { RefreshInfiniteHud(_lives.HasInfiniteLives); }
```

### 9. Server sync

`LivesData` mirrors `ILivesStorage` field for field. Push the snapshot to a remote server after every mutation, and on pull, copy values into storage then call `Initialize()` again to re-run the recovery pipeline.

```csharp
var snapshot = new LivesData {
    Lives               = _lives.Lives,
    MaxLives            = _lives.MaxLives,
    RecoveryStartUtc    = _storage.RecoveryStartUtc,
    InfiniteLivesEndUtc = _storage.InfiniteLivesEndUtc,
};
// ... POST snapshot to server ...

// On pull:
_storage.Lives               = pulled.Lives;
_storage.MaxLives            = pulled.MaxLives;
_storage.RecoveryStartUtc    = pulled.RecoveryStartUtc;
_storage.InfiniteLivesEndUtc = pulled.InfiniteLivesEndUtc;
_storage.Save();
_lives.Initialize();
```

---

## API Reference

| Member | Description |
|--------|-------------|
| `Lives` | Current count (0 – `MaxLives`). |
| `MaxLives` | Upper bound. Change at runtime via `SetMaxLives`. |
| `HasInfiniteLives` | `true` while a grant is active. |
| `IsFull` | `true` when `Lives == MaxLives`. |
| `TimeUntilNextLife` | Remaining time to the next life; `Zero` when full or not recovering. |
| `Initialize()` | Load config, sanitize state, credit offline recovery. Call once before any mutation. |
| `ConsumeLife()` | Spend one life. Returns `false` when empty or during infinite. |
| `AddLives(amount)` | Add lives (IAP refill, rewarded ad). Capped at `MaxLives`. |
| `SetMaxLives(newMax)` | Update the cap; clamps current `Lives` down if needed. |
| `GrantInfinite(seconds)` | Start (or replace) a timed infinite-lives grant. |
| `RefillLives()` | Fill to `MaxLives` and stop recovery. |
| `Tick(dt)` | Advance recovery to the current clock time. Call every frame and on resume. |
| `UpdateRecovery()` | Same pipeline as `Tick`, without the `dt` parameter. |
| `OnLivesChanged` | `Action<int>` — fires with the new count whenever lives change. |
| `OnInfiniteLivesChanged` | `Action` — fires on both grant and expiry. |

---

## Recovery Logic

Recovery is timestamp-based and runs in O(1) regardless of how long the app was closed:

```
elapsed    = max(0, nowTs - RecoveryStartUtc)
livesToAdd = elapsed / SecondsToRecover
Lives      = min(Lives + livesToAdd, MaxLives)
```

If lives reach the cap, `RecoveryStartUtc` is cleared. Otherwise it rewinds to the start of the current incomplete interval so partial progress is preserved.

While `HasInfiniteLives` is `true`, recovery is suspended — but `RecoveryStartUtc` is **not** reset when the grant expires, so elapsed real time during the grant still counts toward the next life.

---

## Infinite Lives

`GrantInfinite(seconds)` sets `InfiniteLivesEndUtc = now + seconds`. While active:

- `ConsumeLife` returns `false` and does not decrement `Lives` — check `HasInfiniteLives` at the call site to distinguish from "empty".
- Any pending full-lives notification is cancelled.
- Recovery is paused (see above).

A second `GrantInfinite` call **replaces** the previous expiry rather than stacking.

---

## Config

`LivesConfig` holds the four tuning values:

```csharp
public sealed class LivesConfig
{
    public int    DefaultMaxLives    = 5;       // initial lives + initial cap on first launch
    public int    SecondsToRecover   = 1800;    // interval between life ticks
    public string NotificationTitle  = "Lives";
    public string NotificationBody   = "Your lives are full!";
}
```

`LivesConfigProvider` is a base class with a single virtual `Get()`. `LivesService` calls `Get()` once in the constructor and again on each `Initialize()` — so changing the returned config and calling `Initialize` again is enough to apply the new values; no service rebuild required.

Pick the option that matches where your config lives:

### Option A — Defaults only (no asset)

```csharp
new LivesService(storage, new LivesConfigProvider(), ...);   // ships with the defaults above
```

### Option B — ScriptableObject (designer-tuned)

The bundled `Samples~/LivesServiceDemo` uses this pattern. Designers tune values in the Inspector; the wrapper hands back a `LivesConfigProvider`.

```csharp
[CreateAssetMenu(fileName = "LivesConfig", menuName = "TMV/Lives/Config")]
public sealed class LivesConfigSO : ScriptableObject
{
    [field: SerializeField] public LivesConfig Config { get; private set; } = new();

    public LivesConfigProvider ToProvider() => new InlineProvider(Config);

    private sealed class InlineProvider : LivesConfigProvider
    {
        private readonly LivesConfig _c;
        public InlineProvider(LivesConfig c) => _c = c;
        public override LivesConfig Get() => _c;
    }
}

// Bootstrap:
LivesConfigProvider provider = _configSO != null
    ? _configSO.ToProvider()
    : new LivesConfigProvider();
```

### Reload config at runtime

To apply new values without restarting the app, refresh the underlying source then call `Initialize()` again. The recovery pipeline re-runs against the new `SecondsToRecover` and the cap is clamped against the new `DefaultMaxLives` (or any explicit `SetMaxLives` value still in storage).

```csharp
await _remote.RefreshAsync();
_lives.Initialize();
```

---

## Testing

`LivesService` depends only on interfaces and a `Func<DateTime>` clock, so EditMode tests need no Unity runtime:

```csharp
[Test]
public void WhenNoLives_ConsumeLife_ReturnsFalse()
{
    var storage  = new FakeLivesStorage { Lives = 0, MaxLives = 5 };
    var fakeNow  = DateTime.UtcNow;
    var lives    = new LivesService(storage, new LivesConfigProvider(),
                                    () => fakeNow,
                                    new FakeNotificationScheduler());
    lives.Initialize();

    Assert.That(lives.ConsumeLife(), Is.False);
    Assert.That(storage.Lives, Is.EqualTo(0));
}
```

`FakeLivesStorage` and `FakeNotificationScheduler` ship alongside the EditMode tests in `Tests/EditMode/`.

To advance time, mutate the captured `fakeNow` variable and call `Tick(0f)` — no extra clock interface needed.

---

## Design Notes

**`Func<DateTime>` instead of `IClock`** — removes a dedicated interface while remaining fully testable. Pass `() => DateTime.UtcNow` in production; capture a local variable in tests.

**Notification requires an active recovery timer** — `ScheduleFullLivesNotification` returns early when `RecoveryStartUtc == 0`. This prevents scheduling a notification anchored to Unix epoch when `ConsumeLife` is called while not full and no timer has started (e.g., after `SetMaxLives` raised the cap above current `Lives`).

**3-second notification grace period** — the "lives full" notification fires 3 seconds after the computed full-recovery timestamp (`RecoveryStartUtc + SecondsToRecover × livesToRecover`). Without this delay, a player who consumed their last life and is still mid-session would receive an interrupting notification immediately after the cycle completes. The value is the private constant `NotificationGracePeriodSeconds` in `LivesService`.

**`Initialize()` separated from the constructor** — keeps construction side-effect free. `Initialize` also runs the recovery pipeline, so lives accumulated while the app was closed are credited at cold launch before the first `Tick`.

**`long` Unix-second timestamps** — match `SecondsToRecover`, so all recovery arithmetic is pure integer math; also serialize cleanly to JSON via `LivesData` for server sync.

**Optional notification scheduler** — non-mobile platforms pass `null` rather than implementing a null-object adapter. The service guards every call site.

**`SetMaxLives` as a public method** — supports runtime cap changes (VIP bonuses, A/B tests) without re-initializing the service.

---

## Links

- [Design document & full source listing](lives_service.md)
- [Repository](https://github.com/truongmv/lives-service)

---

## License

MIT © truongmv — see [LICENSE](LICENSE).
