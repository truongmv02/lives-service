# TMV.Lives — UPM Package Design

> Design document for the lives system — a time-gated resource used in puzzle games.
> Lives are spent on level attempts. They recover over time, and players can gain temporary
> infinite lives through IAP or rewarded ads.

---

## Overview

The lives system manages three orthogonal states:

| State | Description |
|-------|-------------|
| `Lives` | Current count (0 – MaxLives). Spent on each attempt. |
| Recovery | When Lives < MaxLives, one life recovers every `SecondsToRecover` seconds. |
| Infinite lives | A timed override: `ConsumeLife` does not decrement lives and returns `false` — check `HasInfiniteLives` at the call site to distinguish from empty. |

Timestamps are stored as Unix seconds (`long`) for compact persistence and clean JSON
serialization. The service is a plain C# class with zero Unity or engine imports — all
engine-specific adapters are injected through interfaces. A `Func<DateTime>` delegate
replaces a clock interface, keeping the dependency surface minimal.

---

## Class Diagram

```
┌──────────────────────────────────────────────────────┐
│                    «interface»                       │
│                    ILivesService                     │
├──────────────────────────────────────────────────────┤
│ + Lives                  : int                       │
│ + MaxLives               : int                       │
│ + HasInfiniteLives       : bool                      │
│ + IsFull                 : bool                      │
│ + TimeUntilNextLife      : TimeSpan                  │
├──────────────────────────────────────────────────────┤
│ «event» OnLivesChanged           : Action<int>       │
│ «event» OnInfiniteLivesChanged   : Action            │
├──────────────────────────────────────────────────────┤
│ + Initialize()                                       │
│ + ConsumeLife()                  : bool              │
│ + AddLives(amount : int)                             │
│ + SetMaxLives(newMax : int)                          │
│ + GrantInfinite(durationSeconds : int)               │
│ + RefillLives()                                      │
│ + Tick(dt : float)                                   │
│ + UpdateRecovery()                                   │
└─────────────────────────┬────────────────────────────┘
                          △
                          │ implements
              ┌───────────┴────────────┐
              │      LivesService      │
              ├────────────────────────┤
              │ - _storage             │
              │ - _notifications       │
              │ - _utcClock            │
              │ - _configProvider      │
              │ - _config              │
              └───────────┬────────────┘
                          │ depends on
        ┌─────────────────┴──────────────────┐
        ▼                                    ▼
┌──────────────────────────────────┐  ┌──────────────────────────────────────┐
│           «interface»            │  │             «interface»              │
│          ILivesStorage           │  │     ILivesNotificationScheduler      │
├──────────────────────────────────┤  ├──────────────────────────────────────┤
│ + Lives                : int     │  │ + Schedule(title, body, fireAtUtc)   │
│ + MaxLives             : int     │  │ + CancelAll()                        │
│ + RecoveryStartUtc     : long    │  └──────────────────────────────────────┘
│ + InfiniteLivesEndUtc  : long    │
│ + Save()                         │
└──────────────────────────────────┘

┌──────────────────────────────────┐  ┌──────────────────────────────────────┐
│       LivesConfigProvider        │  │           Func<DateTime>             │
│          (base class)            │  │            (utc clock)               │
├──────────────────────────────────┤  └──────────────────────────────────────┘
│ + Get() : LivesConfig            │
└────────────────┬─────────────────┘
                 △
                 │ extends
        (user subclasses, e.g
         RemoteConfigProvider)
```

**Adapter implementations**

| Interface                     | Runtime (Unity)              | Tests/Shared             |
|-------------------------------|------------------------------|--------------------------|
| `ILivesStorage`               | `PlayerPrefsLivesStorage`    | `FakeLivesStorage`       |
| `ILivesNotificationScheduler` | `UnityNotificationScheduler` | `FakeNotificationScheduler` |

**Relationships:**
- `LivesService` depends only on interfaces and `Func<DateTime>` — no Unity imports, no static state.
- `LivesConfigProvider` is a base class with a virtual `Get()`. Subclass it to source config
  from a ScriptableObject, remote config, or A/B test provider.
- `Func<DateTime>` eliminates a dedicated clock interface. Pass `() => DateTime.UtcNow` in
  production; capture a local variable in tests to control time without extra fakes.
- `ILivesNotificationScheduler` is optional. Pass `null` on platforms without push support —
  the service guards every call site.

---

## Recovery Logic

### High-level flow

`Initialize()`, `Tick(dt)` and `UpdateRecovery()` all funnel into the same pipeline:

```
              ┌──────────────────────────────────┐
              │   Tick / Initialize / Update     │
              └────────────────┬─────────────────┘
                               ▼
              ┌──────────────────────────────────┐
              │     ApplyPendingTransitions      │
              └────────────────┬─────────────────┘
                               ▼
          ┌────────────────────────────────────────┐
          │  InfiniteLivesEndUtc > 0               │
          │  AND now >= InfiniteLivesEndUtc ?      │
          └─────────┬──────────────────────┬───────┘
                yes │                      │ no
                    ▼                      │
        ┌─────────────────────────┐        │
        │ InfiniteLivesEndUtc = 0 │        │
        │ infiniteExpired = true  │        │
        │ (RecoveryStartUtc kept) │        │
        └─────────────┬───────────┘        │
                      └──────────┬─────────┘
                                 ▼
          ┌────────────────────────────────────────┐
          │  !IsFull AND !HasInfiniteLives         │
          │  AND RecoveryStartUtc != 0 ?           │
          └─────────┬──────────────────────┬───────┘
                yes │                      │ no
                    ▼                      │
       ┌──────────────────────────┐        │
       │  ApplyOfflineRecovery    │        │
       │  livesRecovered = result │        │
       └─────────────┬────────────┘        │
                     └──────────┬──────────┘
                                ▼
          ┌────────────────────────────────────────┐
          │     Any state change?                  │
          └─────────┬──────────────────────┬───────┘
                yes │                      │ no
                    ▼                      ▼
       ┌──────────────────────────┐    ┌────────┐
       │ Save                     │    │ return │
       │ Fire events              │    └────────┘
       │ Schedule/cancel notif    │
       └──────────────────────────┘
```

### Per-method behaviour

#### `Initialize()`
Runs once before any mutation. Safe to call again after a server sync.

- **First launch** (`MaxLives == 0`) → set `MaxLives = DefaultMaxLives`, `Lives = DefaultMaxLives`.
- **Cap shrank** (`Lives > MaxLives`) → clamp `Lives = MaxLives`.
- **Always** → run the pipeline above, so a `RecoveryStartUtc` persisted from a previous session credits lives at boot, before the first `Tick`.

#### `ConsumeLife() → bool`
Tries to spend one life.

- `HasInfiniteLives` → return `false`, count unchanged. *Caller checks `HasInfiniteLives` to distinguish this from "empty".*
- `Lives == 0` → return `false`.
- Otherwise → `Lives--`, then:
  - was full → `RecoveryStartUtc = nowTs` (start the timer).
  - already recovering → timer keeps running, untouched.
  - save, schedule notification, fire `OnLivesChanged`, return `true`.

#### `Tick(dt)`
Thin wrapper, delegates to `UpdateRecovery()`. Call every frame.

#### `UpdateRecovery()`
Drives the pipeline above. Idempotent — safe to call every frame, on app resume, or after any mutation.

### `ApplyOfflineRecovery(now)` — the math

```
elapsed    = max(0, nowTs - RecoveryStartUtc)
livesToAdd = elapsed / SecondsToRecover           // integer division
if livesToAdd <= 0 → return false                 // sub-cycle, no-op

Lives = min(Lives + livesToAdd, MaxLives)

if IsFull → RecoveryStartUtc = 0                  // stop the clock
else      → RecoveryStartUtc = nowTs - (elapsed % SecondsToRecover)  // keep partial progress
```

One integer division handles arbitrary multi-cycle catch-up in O(1) — no while-loop. Partial progress is preserved by rewinding the start to the beginning of the current incomplete interval.

### Edge cases worth knowing

- **Recovery during infinite lives** — `ApplyOfflineRecovery` is skipped while `HasInfiniteLives` is true, so no life is credited from the recovery timer. But `RecoveryStartUtc` is *not* reset when infinite expires — elapsed real time from before/during the grant still counts toward the next life. Locked in by `Tick_InfiniteExpired_ElapsedDuringInfiniteCountsTowardRecovery`.
- **Cold-launch catch-up** — `Initialize` runs the pipeline, so a `RecoveryStartUtc` persisted from a previous session credits lives at boot before the first `Tick(dt)`.
- **Multi-life recovery fires one event** — `ApplyOfflineRecovery` mutates `Lives` once then fires `OnLivesChanged` once, even if several lives were credited.

---

## Directory Structure

```
com.tmv.lives/                                    ← UPM package root
├── package.json
├── CHANGELOG.md
├── README.md
│
├── Runtime/
│   ├── TMV.Lives.asmdef
│   ├── Core/
│   │   ├── ILivesStorage.cs
│   │   ├── ILivesNotificationScheduler.cs
│   │   ├── ILivesService.cs
│   │   ├── LivesConfig.cs
│   │   ├── LivesConfigProvider.cs
│   │   ├── LivesData.cs                          ← DTO for server sync
│   │   └── LivesService.cs
│   └── Unity/
│       ├── PlayerPrefsLivesStorage.cs
│       └── UnityNotificationScheduler.cs
│
├── Tests/
│   ├── Shared/
│   │   ├── TMV.Lives.Tests.Shared.asmdef
│   │   ├── FakeLivesStorage.cs
│   │   └── FakeNotificationScheduler.cs
│   ├── EditMode/
│   │   ├── TMV.Lives.Tests.EditMode.asmdef
│   │   └── LivesServiceTests.cs
│   └── PlayMode/
│       ├── TMV.Lives.Tests.PlayMode.asmdef
│       └── LivesServicePlayModeTests.cs
│
└── Samples~/
    └── LivesServiceDemo/
        ├── TMV.Lives.Samples.asmdef
        ├── Configs/LivesConfig.asset
        ├── Scenes/LivesDemo.unity
        └── Scripts/
            ├── LivesConfigSO.cs
            └── LivesDemoController.cs
```

The shared `Tests/Shared/` assembly hosts the fakes once so both EditMode and PlayMode
test assemblies can reference them without duplication.

---

## File Details

---

### `package.json`

```json
{
  "name": "com.tmv.lives",
  "version": "1.0.0",
  "displayName": "TMV Lives",
  "description": "Time-gated lives system for Unity puzzle games. Supports recovery, infinite lives, push notifications, and offline catch-up. No engine dependencies in the core.",
  "unity": "2022.3",
  "author": { "name": "truongmv" },
  "license": "MIT",
  "repository": {
    "type": "git",
    "url": "https://github.com/truongmv/lives-service.git"
  },
  "samples": [
    {
      "displayName": "Lives Service Demo",
      "description": "Interactive UI demo: displays lives, infinite lives status, and buttons to add/consume/refill/grant infinite/reset lives.",
      "path": "Samples~/LivesServiceDemo"
    }
  ]
}
```

---

### `Runtime/TMV.Lives.asmdef`

```json
{
  "name": "TMV.Lives",
  "rootNamespace": "TMV.Lives",
  "references": [],
  "autoReferenced": true
}
```

---

### `Runtime/Core/ILivesStorage.cs`

```csharp
namespace TMV.Lives
{
    /// <summary>
    /// Persistence layer for the lives system. Timestamps are Unix seconds (long).
    /// Zero means "not set" for both RecoveryStartUtc and InfiniteLivesEndUtc.
    /// </summary>
    public interface ILivesStorage
    {
        /// <summary>Current lives count.</summary>
        int Lives { get; set; }

        /// <summary>Upper bound for lives count.</summary>
        int MaxLives { get; set; }

        /// <summary>Unix seconds when the current recovery cycle started. 0 = not recovering.</summary>
        long RecoveryStartUtc { get; set; }

        /// <summary>Unix seconds when infinite lives expire. 0 = not active.</summary>
        long InfiniteLivesEndUtc { get; set; }

        /// <summary>Persist all values to the underlying store.</summary>
        void Save();
    }
}
```

---

### `Runtime/Core/ILivesNotificationScheduler.cs`

```csharp
using System;

namespace TMV.Lives
{
    /// <summary>
    /// Schedules and cancels the push notification for when lives are fully recovered.
    /// Only one notification is tracked at a time — Schedule replaces the previous one.
    /// </summary>
    public interface ILivesNotificationScheduler
    {
        /// <summary>Schedule (or replace) the full-lives notification.</summary>
        void Schedule(string title, string body, DateTime fireAtUtc);

        /// <summary>Cancel any pending notification.</summary>
        void CancelAll();
    }
}
```

---

### `Runtime/Core/ILivesService.cs`

```csharp
using System;

namespace TMV.Lives
{
    /// <summary>
    /// Main contract for the lives system. Consume via this interface in game code
    /// so the implementation can be swapped or faked in tests.
    /// </summary>
    public interface ILivesService
    {
        /// <summary>Current lives count (0 – MaxLives).</summary>
        int Lives { get; }

        /// <summary>Upper bound for lives count.</summary>
        int MaxLives { get; }

        /// <summary>True while an infinite-lives grant has not yet expired.</summary>
        bool HasInfiniteLives { get; }

        /// <summary>True when Lives == MaxLives.</summary>
        bool IsFull { get; }

        /// <summary>
        /// Remaining time until the next life is recovered.
        /// Returns TimeSpan.Zero when at max or recovery has not started.
        /// </summary>
        TimeSpan TimeUntilNextLife { get; }

        /// <summary>Fired with the new lives count whenever Lives or recovery state changes.</summary>
        event Action<int> OnLivesChanged;

        /// <summary>Fired when an infinite-lives grant is applied or expires.</summary>
        event Action OnInfiniteLivesChanged;

        /// <summary>
        /// Load config and sanitize persisted state. Must be called once before any
        /// mutation methods. Safe to call again after a server sync to refresh state.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Attempt to spend one life. Returns false only when Lives == 0 and infinite
        /// lives are not active. When infinite lives are active, count is unchanged.
        /// </summary>
        bool ConsumeLife();

        /// <summary>Add lives externally (IAP refill, rewarded ad). Capped at MaxLives.</summary>
        void AddLives(int amount);

        /// <summary>
        /// Update the lives cap. Clamps current Lives down if needed.
        /// Use to apply VIP bonuses or config changes at runtime.
        /// </summary>
        void SetMaxLives(int newMax);

        /// <summary>Grant infinite lives for the given number of seconds.</summary>
        void GrantInfinite(int durationSeconds);

        /// <summary>Immediately fill lives to MaxLives and stop recovery.</summary>
        void RefillLives();

        /// <summary>
        /// Advance the recovery state to the current clock time. Call every frame,
        /// or at minimum on app resume, to handle real-time and offline catch-up.
        /// </summary>
        /// <param name="dt">Delta time in seconds since the last frame (Time.deltaTime).</param>
        void Tick(float dt);

        /// <summary>
        /// Check for elapsed recovery and expired infinite lives, and apply any pending
        /// state transitions. Called by Tick; also safe to call directly on app resume.
        /// </summary>
        void UpdateRecovery();
    }
}
```

---

### `Runtime/Core/LivesConfig.cs`

```csharp
using System;

namespace TMV.Lives
{
    /// <summary>
    /// Tuning parameters for the lives system. Back with a ScriptableObject at the call site
    /// if Inspector editing is needed — the service has no knowledge of ScriptableObject.
    /// </summary>
    [Serializable]
    public sealed class LivesConfig
    {
        /// <summary>Lives count on first launch and the default cap.</summary>
        public int DefaultMaxLives = 5;

        /// <summary>Seconds between each life recovery.</summary>
        public int SecondsToRecover = 1800;

        /// <summary>Title of the push notification sent when lives are full.</summary>
        public string NotificationTitle = "Lives";

        /// <summary>Body of the push notification sent when lives are full.</summary>
        public string NotificationBody = "Your lives are full!";
    }
}
```

---

### `Runtime/Core/LivesConfigProvider.cs`

```csharp
namespace TMV.Lives
{
    /// <summary>
    /// Base config provider. Override Get() to source config from a ScriptableObject,
    /// remote config system, or A/B test provider without changing LivesService.
    /// </summary>
    public class LivesConfigProvider
    {
        /// <summary>Returns the active LivesConfig. Called once inside Initialize().</summary>
        public virtual LivesConfig Get() => new LivesConfig();
    }
}
```

---

### `Runtime/Core/LivesData.cs`

```csharp
using System;

namespace TMV.Lives
{
    /// <summary>
    /// DTO for serializing/deserializing lives state to and from a remote server.
    /// Mirrors ILivesStorage field for field so snapshots can be pushed or pulled directly.
    /// </summary>
    [Serializable]
    public class LivesData
    {
        public int  Lives;
        public int  MaxLives;
        public long RecoveryStartUtc;
        public long InfiniteLivesEndUtc;
    }
}
```

---

### `Runtime/Core/LivesService.cs`

```csharp
using System;

namespace TMV.Lives
{
    /// <summary>
    /// Core lives logic. No Unity imports — fully testable in EditMode.
    /// Call Initialize() once before any mutation methods.
    /// </summary>
    public sealed class LivesService : ILivesService
    {
        #region Fields

        private readonly ILivesStorage _storage;
        private readonly ILivesNotificationScheduler _notifications;
        private readonly Func<DateTime> _utcClock;
        private readonly LivesConfigProvider _configProvider;
        private LivesConfig _config;

        #endregion

        #region Events

        /// <inheritdoc/>
        public event Action<int> OnLivesChanged;

        /// <inheritdoc/>
        public event Action OnInfiniteLivesChanged;

        #endregion

        #region Properties

        /// <inheritdoc/>
        public int Lives => _storage.Lives;

        /// <inheritdoc/>
        public int MaxLives => _storage.MaxLives;

        /// <inheritdoc/>
        public bool HasInfiniteLives
            => _storage.InfiniteLivesEndUtc > 0
               && NowUnixSeconds() < _storage.InfiniteLivesEndUtc;

        /// <inheritdoc/>
        public bool IsFull => _storage.Lives >= _storage.MaxLives;

        /// <inheritdoc/>
        public TimeSpan TimeUntilNextLife
        {
            get
            {
                if (IsFull || _storage.RecoveryStartUtc == 0) return TimeSpan.Zero;
                long remaining = _config.SecondsToRecover - (NowUnixSeconds() - _storage.RecoveryStartUtc);
                return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining);
            }
        }

        #endregion

        #region Constructor

        public LivesService(
            ILivesStorage storage,
            LivesConfigProvider configProvider,
            Func<DateTime> utcClock = null,
            ILivesNotificationScheduler notifications = null)
        {
            _storage = storage;
            _configProvider = configProvider;
            _notifications = notifications;
            _utcClock = utcClock ?? (() => DateTime.UtcNow);
            _config = configProvider.Get();
        }

        #endregion

        #region Public Methods

        /// <inheritdoc/>
        public void Initialize()
        {
            _config = _configProvider.Get();

            if (_storage.MaxLives <= 0)
            {
                // First launch: apply defaults and start full.
                _storage.MaxLives = _config.DefaultMaxLives;
                _storage.Lives = _config.DefaultMaxLives;
            }
            else if (_storage.Lives > _storage.MaxLives)
            {
                // Clamp if MaxLives was reduced (e.g., config change after VIP lapse).
                _storage.Lives = _storage.MaxLives;
            }

            // Credit any lives that recovered while the app was closed.
            var (infiniteExpired, livesRecovered) = ApplyPendingTransitions();

            _storage.Save();

            if (infiniteExpired) OnInfiniteLivesChanged?.Invoke();
            if (livesRecovered) OnLivesChanged?.Invoke(_storage.Lives);
            ScheduleOrCancelNotification();
        }

        /// <inheritdoc/>
        public bool ConsumeLife()
        {
            if (HasInfiniteLives) return false;
            if (_storage.Lives <= 0) return false;

            bool wasFull = IsFull;
            _storage.Lives--;

            // Start recovery only when transitioning from full; preserve any existing timer.
            if (wasFull)
                _storage.RecoveryStartUtc = NowUnixSeconds();

            _storage.Save();
            ScheduleFullLivesNotification();
            OnLivesChanged?.Invoke(_storage.Lives);
            return true;
        }

        /// <inheritdoc/>
        public void AddLives(int amount)
        {
            if (amount <= 0) return;

            _storage.Lives = Math.Min(_storage.Lives + amount, _storage.MaxLives);

            if (IsFull)
                _storage.RecoveryStartUtc = 0;

            _storage.Save();
            // Reschedule (or cancel) so the "lives full" notification matches the new deficit.
            ScheduleOrCancelNotification();
            OnLivesChanged?.Invoke(_storage.Lives);
        }

        /// <inheritdoc/>
        public void SetMaxLives(int newMax)
        {
            if (newMax <= 0) return;

            _storage.MaxLives = newMax;

            if (_storage.Lives > newMax)
                _storage.Lives = newMax;

            if (IsFull)
                _storage.RecoveryStartUtc = 0;

            _storage.Save();
            // Reschedule (or cancel) so the "lives full" notification matches the new deficit.
            ScheduleOrCancelNotification();
            OnLivesChanged?.Invoke(_storage.Lives);
        }

        /// <inheritdoc/>
        public void GrantInfinite(int durationSeconds)
        {
            if (durationSeconds <= 0) return;

            _storage.InfiniteLivesEndUtc = NowUnixSeconds() + durationSeconds;
            _storage.Save();
            _notifications?.CancelAll();
            OnInfiniteLivesChanged?.Invoke();
        }

        /// <inheritdoc/>
        public void RefillLives()
        {
            _storage.Lives = _storage.MaxLives;
            _storage.RecoveryStartUtc = 0;
            _notifications?.CancelAll();
            _storage.Save();
            OnLivesChanged?.Invoke(_storage.Lives);
        }

        /// <inheritdoc/>
        public void Tick(float dt)
        {
            // dt mirrors the Unity Update(Time.deltaTime) calling pattern but recovery is
            // timestamp-based, so the value is intentionally ignored.
            UpdateRecovery();
        }

        /// <inheritdoc/>
        public void UpdateRecovery()
        {
            var (infiniteExpired, livesRecovered) = ApplyPendingTransitions();
            if (!infiniteExpired && !livesRecovered) return;

            _storage.Save();
            if (infiniteExpired) OnInfiniteLivesChanged?.Invoke();
            if (livesRecovered) OnLivesChanged?.Invoke(_storage.Lives);
            ScheduleOrCancelNotification();
        }

        #endregion

        #region Private/Protected Methods

        /// <summary>Current UTC time as Unix seconds, sourced from the injected clock.</summary>
        private long NowUnixSeconds()
            => (long)(_utcClock() - DateTime.UnixEpoch).TotalSeconds;

        /// Detects and applies state transitions: infinite-lives expiry and life recovery.
        /// RecoveryStartUtc is intentionally NOT touched on infinite expiry — elapsed time
        /// during the infinite grant still counts toward the next life.
        private (bool infiniteExpired, bool livesRecovered) ApplyPendingTransitions()
        {
            bool infiniteExpired = false;
            bool livesRecovered = false;

            if (_storage.InfiniteLivesEndUtc > 0 && !HasInfiniteLives)
            {
                _storage.InfiniteLivesEndUtc = 0;
                infiniteExpired = true;
            }

            if (!IsFull && !HasInfiniteLives && _storage.RecoveryStartUtc != 0)
                livesRecovered = ApplyOfflineRecovery(_utcClock());

            return (infiniteExpired, livesRecovered);
        }

        /// Computes how many lives should have recovered since RecoveryStartUtc and applies them.
        /// Uses integer division to handle multiple missed intervals in one call (offline catch-up).
        private bool ApplyOfflineRecovery(DateTime now)
        {
            long nowTs = (long)(now - DateTime.UnixEpoch).TotalSeconds;
            long elapsed = Math.Max(0, nowTs - _storage.RecoveryStartUtc);
            int intervalSec = _config.SecondsToRecover;
            int livesToAdd = (int)(elapsed / intervalSec);
            if (livesToAdd <= 0) return false;

            int oldLives = _storage.Lives;
            _storage.Lives = Math.Min(_storage.Lives + livesToAdd, _storage.MaxLives);

            if (_storage.Lives == oldLives) return false;

            // Preserve partial progress: rewind start to the beginning of the current interval.
            _storage.RecoveryStartUtc = IsFull ? 0 : nowTs - (elapsed % intervalSec);

            return true;
        }

        private void ScheduleOrCancelNotification()
        {
            if (IsFull || HasInfiniteLives)
                _notifications?.CancelAll();
            else if (_storage.RecoveryStartUtc != 0)
                ScheduleFullLivesNotification();
        }

        private void ScheduleFullLivesNotification()
        {
            if (IsFull || HasInfiniteLives || _notifications == null) return;

            long livesToRecover = _storage.MaxLives - _storage.Lives;
            long fullRecoveryAt = _storage.RecoveryStartUtc + _config.SecondsToRecover * livesToRecover;

            _notifications.Schedule(
                _config.NotificationTitle,
                _config.NotificationBody,
                DateTime.UnixEpoch.AddSeconds(fullRecoveryAt));
        }

        #endregion
    }
}
```

---

### `Runtime/Unity/PlayerPrefsLivesStorage.cs`

```csharp
using UnityEngine;

namespace TMV.Lives.Unity
{
    /// <summary>
    /// ILivesStorage backed by PlayerPrefs. Unix-second timestamps are stored as strings
    /// because PlayerPrefs has no native Int64 support.
    /// </summary>
    public sealed class PlayerPrefsLivesStorage : ILivesStorage
    {
        #region Fields

        private const string KeyLives            = "tmv.lives.count";
        private const string KeyMaxLives         = "tmv.lives.max";
        private const string KeyRecoveryStart    = "tmv.lives.recovery_start";
        private const string KeyInfiniteLivesEnd = "tmv.lives.infinite_end";

        #endregion

        #region Properties

        public int Lives    { get => PlayerPrefs.GetInt(KeyLives, 0);    set => PlayerPrefs.SetInt(KeyLives, value); }
        public int MaxLives { get => PlayerPrefs.GetInt(KeyMaxLives, 0); set => PlayerPrefs.SetInt(KeyMaxLives, value); }

        public long RecoveryStartUtc
        {
            get => ReadLong(KeyRecoveryStart);
            set => WriteLong(KeyRecoveryStart, value);
        }

        public long InfiniteLivesEndUtc
        {
            get => ReadLong(KeyInfiniteLivesEnd);
            set => WriteLong(KeyInfiniteLivesEnd, value);
        }

        #endregion

        #region Public Methods

        public void Save() => PlayerPrefs.Save();

        #endregion

        #region Private/Protected Methods

        private static long ReadLong(string key)
        {
            var raw = PlayerPrefs.GetString(key, "0");
            return long.TryParse(raw, out var v) ? v : 0;
        }

        private static void WriteLong(string key, long value)
            => PlayerPrefs.SetString(key, value.ToString());

        #endregion
    }
}
```

---

### `Runtime/Unity/UnityNotificationScheduler.cs`

```csharp
using System;

#if UNITY_ANDROID
using Unity.Notifications.Android;
#elif UNITY_IOS
using Unity.Notifications.iOS;
#endif

namespace TMV.Lives.Unity
{
    /// <summary>
    /// ILivesNotificationScheduler backed by Unity Mobile Notifications.
    /// Requires the com.unity.mobile.notifications package.
    /// Supports Android and iOS via platform compile guards; a no-op on other platforms.
    /// </summary>
    public sealed class UnityNotificationScheduler : ILivesNotificationScheduler
    {
        #region Fields

        private const string ChannelId      = "tmv_lives";
        private const int    NotificationId = 0x4C495645; // "LIVE"

        #endregion

        #region Public Methods

        public void Schedule(string title, string body, DateTime fireAtUtc)
        {
            CancelAll();

#if UNITY_ANDROID
            ScheduleAndroid(title, body, fireAtUtc);
#elif UNITY_IOS
            ScheduleIos(title, body, fireAtUtc);
#endif
        }

        public void CancelAll()
        {
#if UNITY_ANDROID
            AndroidNotificationCenter.CancelScheduledNotification(NotificationId);
#elif UNITY_IOS
            iOSNotificationCenter.RemoveScheduledNotification(NotificationId.ToString());
#endif
        }

        #endregion

        #region Private/Protected Methods

#if UNITY_ANDROID
        private static bool _androidChannelRegistered;

        private static void EnsureAndroidChannel()
        {
            if (_androidChannelRegistered) return;

            var channel = new AndroidNotificationChannel
            {
                Id          = ChannelId,
                Name        = "Lives",
                Importance  = Importance.Default,
                Description = "Notifications for the lives system."
            };
            AndroidNotificationCenter.RegisterNotificationChannel(channel);
            _androidChannelRegistered = true;
        }

        private static void ScheduleAndroid(string title, string body, DateTime fireAtUtc)
        {
            // Android 8+ requires a registered channel before any notification can be posted.
            EnsureAndroidChannel();

            var notification = new AndroidNotification
            {
                Title     = title,
                Text      = body,
                FireTime  = fireAtUtc.ToLocalTime(),
                SmallIcon = "icon_small",
                LargeIcon = "icon_large"
            };

            AndroidNotificationCenter.SendNotificationWithExplicitID(
                notification, ChannelId, NotificationId);
        }
#elif UNITY_IOS
        private static void ScheduleIos(string title, string body, DateTime fireAtUtc)
        {
            var trigger = new iOSNotificationCalendarTrigger
            {
                Year    = fireAtUtc.ToLocalTime().Year,
                Month   = fireAtUtc.ToLocalTime().Month,
                Day     = fireAtUtc.ToLocalTime().Day,
                Hour    = fireAtUtc.ToLocalTime().Hour,
                Minute  = fireAtUtc.ToLocalTime().Minute,
                Second  = fireAtUtc.ToLocalTime().Second,
                Repeats = false
            };

            var notification = new iOSNotification
            {
                Identifier                   = NotificationId.ToString(),
                Title                        = title,
                Body                         = body,
                ShowInForeground             = false,
                ForegroundPresentationOption = PresentationOption.Alert | PresentationOption.Sound,
                Trigger                      = trigger
            };

            iOSNotificationCenter.ScheduleNotification(notification);
        }
#endif

        #endregion
    }
}
```

---

### `Tests/Shared/TMV.Lives.Tests.Shared.asmdef`

```json
{
  "name": "TMV.Lives.Tests.Shared",
  "rootNamespace": "TMV.Lives.Tests",
  "references": ["TMV.Lives"],
  "includePlatforms": [],
  "overrideReferences": false,
  "allowUnsafeCode": false,
  "autoReferenced": false,
  "noEngineReferences": false
}
```

---

### `Tests/Shared/FakeLivesStorage.cs`

```csharp
namespace TMV.Lives.Tests
{
    public sealed class FakeLivesStorage : ILivesStorage
    {
        public int  Lives               { get; set; }
        public int  MaxLives            { get; set; }
        public long RecoveryStartUtc    { get; set; }
        public long InfiniteLivesEndUtc { get; set; }

        /// <summary>Counts Save() calls — asserts that the service persists state.</summary>
        public int SaveCallCount { get; private set; }

        public void Save() => SaveCallCount++;
    }
}
```

---

### `Tests/Shared/FakeNotificationScheduler.cs`

```csharp
using System;

namespace TMV.Lives.Tests
{
    public sealed class FakeNotificationScheduler : ILivesNotificationScheduler
    {
        public int      ScheduleCallCount   { get; private set; }
        public int      CancelAllCallCount  { get; private set; }
        public DateTime LastScheduledFireAt { get; private set; }

        public void Schedule(string title, string body, DateTime fireAtUtc)
        {
            ScheduleCallCount++;
            LastScheduledFireAt = fireAtUtc;
        }

        public void CancelAll() => CancelAllCallCount++;
    }
}
```

---

### `Tests/EditMode/TMV.Lives.Tests.EditMode.asmdef`

```json
{
  "name": "TMV.Lives.Tests.EditMode",
  "rootNamespace": "TMV.Lives.Tests",
  "references": ["TMV.Lives", "TMV.Lives.Tests.Shared", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "includePlatforms": ["Editor"],
  "overrideReferences": true,
  "allowUnsafeCode": false,
  "autoReferenced": false,
  "precompiledReferences": ["nunit.framework.dll"],
  "noEngineReferences": false
}
```

---

### `Tests/EditMode/LivesServiceTests.cs`

EditMode tests exercise every public path of `LivesService` against `FakeLivesStorage` and
`FakeNotificationScheduler`, with a captured local `_fakeNow` standing in for the system
clock. Each test starts by setting the storage to a known state, builds the service, advances
the fake clock, and asserts the resulting state.

Coverage groups:

- **Initialize** — first launch, clamp on shrunk MaxLives, cold-launch offline recovery
  (single cycle, multi-cycle, multi-cycle + partial).
- **ConsumeLife** — happy path, empty pool, infinite-lives override, recovery timer start
  from full vs preservation while already recovering, event payload, no notification during
  infinite.
- **Tick / Recovery** — sub-cycle no-op, single cycle, partial-cycle timer slide, multi-cycle
  + partial, long-offline cap-to-max, full-cap timer clear, idempotent zero-elapsed Tick,
  single OnLivesChanged for multi-life recovery, recovery suspended during infinite, infinite
  expiry clears state and fires event, recovery progress preserved across infinite, elapsed
  time during infinite still counts toward next life
  (`Tick_InfiniteExpired_ElapsedDuringInfiniteCountsTowardRecovery`).
- **AddLives / SetMaxLives / GrantInfinite / RefillLives** — caps, timer clears, notification
  cancels, zero/invalid input ignored, replace-not-stack semantics for `GrantInfinite`,
  boundary checks on the `<` infinite expiry comparison.
- **TimeUntilNextLife** — Zero when full, remaining time while recovering, clamped to Zero
  past the cycle boundary before `Tick` is called.
- **Notifications** — schedule fires at exactly `RecoveryStart + SecondsToRecover *
  livesToRecover`; rescheduled after partial recovery.

The fixture uses a private `ConstConfigProvider : LivesConfigProvider` helper that returns
the test's `LivesConfig` from `Get()`.

---

### `Tests/PlayMode/TMV.Lives.Tests.PlayMode.asmdef`

```json
{
  "name": "TMV.Lives.Tests.PlayMode",
  "rootNamespace": "TMV.Lives.Tests",
  "references": ["TMV.Lives", "TMV.Lives.Tests.Shared", "UnityEngine.TestRunner"],
  "includePlatforms": [],
  "overrideReferences": true,
  "allowUnsafeCode": false,
  "autoReferenced": false,
  "precompiledReferences": ["nunit.framework.dll"],
  "noEngineReferences": false
}
```

---

### `Tests/PlayMode/LivesServicePlayModeTests.cs`

```csharp
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace TMV.Lives.Tests
{
    /// <summary>
    /// PlayMode tests for LivesService — use when Unity runtime behaviour (coroutines,
    /// MonoBehaviour lifecycle, frame timing) must be validated alongside the service.
    /// </summary>
    public class LivesServicePlayModeTests
    {
        [UnityTest]
        public IEnumerator Tick_CalledEachFrame_RecoveryAdvancesInRealTime()
        {
            var storage = new FakeLivesStorage { Lives = 4, MaxLives = 5 };
            var notifications = new FakeNotificationScheduler();
            var config = new LivesConfig { DefaultMaxLives = 5, SecondsToRecover = 1 };

            // Freeze time just before recovery completes.
            var fakeNow = DateTime.UtcNow;
            storage.RecoveryStartUtc = (long)(fakeNow - DateTime.UnixEpoch).TotalSeconds;

            var sut = new LivesService(storage, new ConstConfigProvider(config), () => fakeNow, notifications);
            sut.Initialize();

            // Advance clock past the recovery threshold.
            fakeNow = fakeNow.AddSeconds(2);
            sut.Tick(0f);

            Assert.That(sut.Lives, Is.EqualTo(5));
            yield return null;
        }

        private sealed class ConstConfigProvider : LivesConfigProvider
        {
            private readonly LivesConfig _config;
            public ConstConfigProvider(LivesConfig config) => _config = config;
            public override LivesConfig Get() => _config;
        }
    }
}
```

---

### `Samples~/LivesServiceDemo/`

The package ships a Unity sample (importable via the Package Manager's Samples tab) with:

- `Configs/LivesConfig.asset` — a `LivesConfigSO` tuned for demo timings.
- `Scenes/LivesDemo.unity` — a sandbox scene with UI buttons (consume, add, refill,
  grant infinite, reset) and labels for Lives, MaxLives, TimeUntilNextLife.
- `Scripts/LivesConfigSO.cs` — ScriptableObject wrapper for `LivesConfig` that exposes a
  `ToProvider()` method returning a `LivesConfigProvider`.
- `Scripts/LivesDemoController.cs` — `MonoBehaviour` that bootstraps `LivesService` from
  the asset, ticks it every frame, and wires UI events.
- `TMV.Lives.Samples.asmdef` — sample assembly referencing `TMV.Lives` and UGUI.

---

## Usage Guide

### 1. Install via UPM (git URL)

In `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.tmv.lives": "https://github.com/truongmv/lives-service.git"
  }
}
```

---

### 2. Author a config asset (optional)

The simplest path — used by the bundled `Samples~/LivesServiceDemo` — is a thin
ScriptableObject wrapper around `LivesConfig`. Designers tune values in the Inspector;
the wrapper hands back a `LivesConfigProvider` via `ToProvider()`.

```csharp
// LivesConfigSO.cs  (lives in the game project, not this package)
[CreateAssetMenu(fileName = "LivesConfig", menuName = "TMV/Lives/Config")]
public sealed class LivesConfigSO : ScriptableObject
{
    /// <summary>The serialized config data tuned in the Inspector.</summary>
    [field: SerializeField] public LivesConfig Config { get; private set; } = new LivesConfig();

    /// <summary>Returns a provider that serves the inspector-authored config.</summary>
    public LivesConfigProvider ToProvider() => new InlineProvider(Config);

    private sealed class InlineProvider : LivesConfigProvider
    {
        private readonly LivesConfig _config;
        public InlineProvider(LivesConfig config) => _config = config;
        public override LivesConfig Get() => _config;
    }
}
```

Bootstrap then resolves the provider straight from the asset (with a safe fallback for
when no asset is wired):

```csharp
LivesConfigProvider provider = _configSO != null
    ? _configSO.ToProvider()
    : new LivesConfigProvider();   // defaults baked into LivesConfig
```

If you need to source config from remote config or another runtime system, subclass
`LivesConfigProvider` directly and override `Get()` — no ScriptableObject required.

---

### 3. Bootstrap

Construct and initialize the service once before any scene loads. Pass it to any object
that needs it via constructor injection or a project-level DI container.

The notification scheduler is optional — pass `null` (or simply omit the argument) on
platforms without push support. `LivesService` guards every call site.

```csharp
// GameBootstrap.cs
public sealed class GameBootstrap : MonoBehaviour
{
    [SerializeField] private LivesConfigProvider _configProvider;   // ScriptableObject asset

    public ILivesService Lives { get; private set; }

    private void Awake()
    {
        ILivesNotificationScheduler notifications =
            Application.isMobilePlatform ? new UnityNotificationScheduler() : null;

        Lives = new LivesService(
            new PlayerPrefsLivesStorage(),
            _configProvider,
            () => DateTime.UtcNow,
            notifications);

        Lives.Initialize();
    }
}
```

---

### 4. Tick every frame

```csharp
// LivesTicker.cs — on a persistent GameObject
public sealed class LivesTicker : MonoBehaviour
{
    [SerializeField] private GameBootstrap _bootstrap;

    private ILivesService _lives;

    private void Start() => _lives = _bootstrap.Lives;

    private void Update() => _lives.Tick(Time.deltaTime);

    // Force catch-up on resume from background.
    private void OnApplicationPause(bool paused) { if (!paused) _lives.Tick(0f); }
}
```

---

### 5. Spend a life before a level attempt

```csharp
public sealed class LevelStartController
{
    private readonly ILivesService _lives;

    public LevelStartController(ILivesService lives) => _lives = lives;

    public bool TryStartLevel()
    {
        // ConsumeLife returns false both when Lives == 0 and when HasInfiniteLives.
        // Check HasInfiniteLives first so the "no lives" popup is not shown unnecessarily.
        bool canStart = _lives.HasInfiniteLives || _lives.ConsumeLife();
        if (!canStart) { ShowNoLivesPopup(); return false; }
        LoadLevel();
        return true;
    }
}
```

---

### 6. Display lives and countdown in the UI

```csharp
// Attach to any MonoBehaviour that holds a reference to ILivesService.
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

---

### 7. Grant infinite lives or refill (rewarded ad / IAP)

```csharp
// Rewarded ad — 30 minutes of infinite lives
_lives.GrantInfinite(1800);

// IAP — refill lives to max instantly
_lives.RefillLives();

// IAP — add a fixed number of lives
_lives.AddLives(5);
```

---

### 8. React to state changes

```csharp
private void OnEnable()
{
    _lives.OnLivesChanged        += HandleLivesChanged;
    _lives.OnInfiniteLivesChanged += HandleInfiniteLivesChanged;
}

private void OnDisable()
{
    _lives.OnLivesChanged        -= HandleLivesChanged;
    _lives.OnInfiniteLivesChanged -= HandleInfiniteLivesChanged;
}

private void HandleLivesChanged(int newCount)    { /* refresh HUD */ }
private void HandleInfiniteLivesChanged()        { /* update infinite lives UI */ }
```

---

### 9. Apply a VIP max-lives bonus

```csharp
// Expand the cap from 5 to 8 after a VIP purchase.
_lives.SetMaxLives(8);
```

---

### 10. Unit test — no engine required

```csharp
[Test]
public void WhenNoLives_TryStartLevel_ReturnsFalse()
{
    var storage = new FakeLivesStorage { Lives = 0, MaxLives = 5 };
    var lives   = new LivesService(storage, new LivesConfigProvider(),
                                   () => DateTime.UtcNow,
                                   new FakeNotificationScheduler());
    lives.Initialize();

    var controller = new LevelStartController(lives);

    Assert.That(controller.TryStartLevel(), Is.False);
    Assert.That(storage.Lives, Is.EqualTo(0));
}
```

---

## Design Decisions

**`LivesConfigProvider` as a base class** — game code subclasses it to read from a
ScriptableObject or remote config system. `LivesService` calls `Get()` once inside the
constructor and again inside `Initialize()`, so a config change takes effect on the next
session or explicit re-initialize without any reload logic inside the service.

**`Func<DateTime>` instead of `IClock`** — removes a dedicated interface while remaining fully
testable: capture a local variable in the test and mutate it to advance time. No extra file,
no extra fake to maintain.

**`Initialize()` separated from the constructor** — keeps construction side-effect free.
Callers can build the service early (Awake) and initialize it later (Start or after a server
sync), without requiring constructor parameter order tricks. `Initialize` also runs
`ApplyPendingTransitions`, so lives accumulated while the app was closed are credited at
cold launch — the first `Tick` is not required to see the catch-up.

**`OnLivesChanged(int)` instead of `Action`** — passing the new count avoids a redundant
`Lives` property read in every listener and makes the event signature self-documenting.

**`OnInfiniteLivesChanged`** — a single event fires on both grant and expiry. The UI
subscribes once and re-reads `HasInfiniteLives` to determine which transition occurred,
avoiding a separate `OnInfiniteExpired` event.

**`SetMaxLives` as a public method** — supports runtime cap changes for VIP bonuses or A/B
tests without re-initializing the service.

**`long` timestamps in `ILivesStorage`** — Unix seconds match `SecondsToRecover`, making all
recovery arithmetic pure integer math. They also serialize cleanly to JSON for server sync via
`LivesData`.

**`Tick(float dt)` instead of a background thread** — recovery is driven by the game loop,
which avoids threading concerns and keeps recovery paused in sync with the engine. `Tick`
delegates to `UpdateRecovery`, which can also be called directly on app resume.

**Integer-division recovery** — `ApplyOfflineRecovery` uses `elapsed / SecondsToRecover` to
compute recovered lives in O(1), replacing the original while-loop. Partial progress is
preserved by rewinding `RecoveryStartUtc` to the start of the current (incomplete) interval.
Recovery is paused while `HasInfiniteLives` is true, but `RecoveryStartUtc` is not reset on
infinite expiry — elapsed real time during the grant still counts toward the next life. This
is intentional and locked in by
`LivesServiceTests.Tick_InfiniteExpired_ElapsedDuringInfiniteCountsTowardRecovery`.

**Single notification slot** — the service always cancels before scheduling. This keeps
`ILivesNotificationScheduler` simple (no list management) and avoids duplicate notifications.

**Optional notification scheduler** — `LivesService` accepts a nullable
`ILivesNotificationScheduler` and guards every call site, so non-mobile platforms simply pass
`null` (or omit the argument) without needing a dedicated null-object adapter.

**Shared fakes assembly** — `FakeLivesStorage` and `FakeNotificationScheduler` live in their
own `Tests/Shared/` assembly so EditMode and PlayMode tests can reuse them without
duplication.
