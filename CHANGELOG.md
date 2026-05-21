# Changelog

All notable changes to **TMV.Lives** (`com.tmv.lives`) are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.0.0] - 2026-05-21

Initial public release of the TMV.Lives package — a time-gated lives system for Unity
puzzle games, with zero engine dependencies in the core.

### Highlights

| Area              | Summary                                                                       |
|-------------------|-------------------------------------------------------------------------------|
| Core service      | Plain C# `LivesService` — no Unity imports, fully unit-testable.              |
| Recovery          | O(1) integer-division catch-up. Handles arbitrary offline duration.           |
| Infinite lives    | Timed override via `GrantInfinite(seconds)`. Replace-not-stack semantics.     |
| Persistence       | `ILivesStorage` interface + `PlayerPrefsLivesStorage` adapter.                |
| Notifications     | Optional `ILivesNotificationScheduler` + `UnityNotificationScheduler` (Android/iOS). |
| Config            | `LivesConfigProvider` base class — subclass for ScriptableObject or remote.   |
| Server sync       | `LivesData` DTO mirrors storage field-for-field.                              |
| Tests             | EditMode + PlayMode + shared fakes assembly.                                  |
| Sample            | `Samples~/LivesServiceDemo` — interactive UI demo scene.                      |

---

### Added — Core API

- **`ILivesService`** — main contract for consuming the lives system in game code.
  - Properties: `Lives`, `MaxLives`, `HasInfiniteLives`, `IsFull`, `TimeUntilNextLife`.
  - Events: `OnLivesChanged(int newCount)`, `OnInfiniteLivesChanged`.
  - Methods: `Initialize`, `ConsumeLife`, `AddLives`, `SetMaxLives`, `GrantInfinite`,
    `RefillLives`, `Tick(float dt)`, `UpdateRecovery`.

- **`LivesService`** — default implementation. Constructor takes:
  - `ILivesStorage storage` (required)
  - `LivesConfigProvider configProvider` (required)
  - `Func<DateTime> utcClock = null` (defaults to `DateTime.UtcNow`)
  - `ILivesNotificationScheduler notifications = null` (optional)

- **`LivesConfig`** — tuning parameters:
  - `DefaultMaxLives` (default `5`)
  - `SecondsToRecover` (default `1800`)
  - `NotificationTitle` / `NotificationBody`

- **`LivesConfigProvider`** — base class with virtual `Get()`. Subclass to source config
  from a ScriptableObject, remote config, or A/B test provider.

- **`LivesData`** — DTO mirroring `ILivesStorage` for server sync.

---

### Added — Recovery Logic

- **Cold-launch catch-up** — `Initialize()` runs the full recovery pipeline so lives
  accumulated while the app was closed are credited *before* the first `Tick(dt)`.
- **O(1) offline recovery** — `elapsed / SecondsToRecover` computes recovered lives in a
  single integer division, regardless of how long the app was closed.
- **Partial progress preserved** — when not yet full, `RecoveryStartUtc` is rewound to the
  start of the current incomplete interval (`nowTs - (elapsed % SecondsToRecover)`).
- **Multi-life recovery fires one event** — `OnLivesChanged` is invoked once even if
  multiple lives are credited in a single update.
- **Idempotent** — `UpdateRecovery` is safe to call every frame, on app resume, or after
  any mutation.

---

### Added — Infinite Lives Semantics

- `GrantInfinite(seconds)` sets `InfiniteLivesEndUtc = now + seconds` and cancels any
  pending notification. Calling it again **replaces** the end time (does not stack).
- While `HasInfiniteLives` is true:
  - `ConsumeLife()` returns `false` and leaves `Lives` unchanged. *Callers should check
    `HasInfiniteLives` to distinguish this from "empty".*
  - Offline recovery is **skipped**, but `RecoveryStartUtc` is **not** reset.
- On expiry, elapsed real time from before/during the grant still counts toward the next
  life. Locked in by `Tick_InfiniteExpired_ElapsedDuringInfiniteCountsTowardRecovery`.

---

### Added — Unity Adapters

- **`PlayerPrefsLivesStorage`** — `ILivesStorage` backed by `PlayerPrefs`. Unix-second
  timestamps stored as strings (PlayerPrefs has no native `Int64`).
- **`UnityNotificationScheduler`** — `ILivesNotificationScheduler` backed by
  `com.unity.mobile.notifications`.
  - Android: registers a notification channel on first use.
  - iOS: schedules via `iOSNotificationCalendarTrigger`.
  - Other platforms: no-op.
  - Single notification slot — every `Schedule` call cancels the previous one.

---

### Added — Persistence Model

| Field                  | Type   | Meaning                                                  |
|------------------------|--------|----------------------------------------------------------|
| `Lives`                | `int`  | Current count (0 – `MaxLives`).                          |
| `MaxLives`             | `int`  | Upper bound. `0` on first launch triggers defaults.      |
| `RecoveryStartUtc`     | `long` | Unix seconds when current recovery cycle started. `0` = not recovering. |
| `InfiniteLivesEndUtc`  | `long` | Unix seconds when infinite lives expire. `0` = not active. |

---

### Added — Tests

- **Shared fakes assembly** (`Tests/Shared/`) — `FakeLivesStorage`,
  `FakeNotificationScheduler`. Reused by both EditMode and PlayMode test assemblies.
- **EditMode tests** (`Tests/EditMode/LivesServiceTests.cs`) — coverage includes:
  - `Initialize` — first launch, MaxLives clamp, cold-launch offline recovery
    (single, multi-cycle, multi-cycle + partial).
  - `ConsumeLife` — happy path, empty pool, infinite override, timer start vs preservation,
    event payload, no notification during infinite.
  - `Tick` / recovery — sub-cycle no-op, single/multi-cycle, partial-cycle slide, long
    offline cap-to-max, full-cap timer clear, idempotent zero-elapsed, single event for
    multi-life recovery, recovery suspended during infinite, infinite expiry transitions,
    elapsed during infinite still counting toward next life.
  - `AddLives` / `SetMaxLives` / `GrantInfinite` / `RefillLives` — caps, timer clears,
    notification cancels, zero/invalid input ignored, replace-not-stack semantics.
  - `TimeUntilNextLife` — zero when full, remaining while recovering, clamped past cycle.
  - Notifications — schedule fires at `RecoveryStart + SecondsToRecover * livesToRecover`;
    rescheduled after partial recovery.
- **PlayMode test** (`Tests/PlayMode/LivesServicePlayModeTests.cs`) — recovery driven by
  real frame timing via `Tick`.

---

### Added — Sample

`Samples~/LivesServiceDemo` (importable from the Package Manager's Samples tab):

- `Configs/LivesConfig.asset` — `LivesConfigSO` tuned for demo timings.
- `Scenes/LivesDemo.unity` — sandbox scene with UI buttons (consume, add, refill,
  grant infinite, reset) and labels for `Lives`, `MaxLives`, `TimeUntilNextLife`.
- `Scripts/LivesConfigSO.cs` — ScriptableObject wrapper exposing `ToProvider()`.
- `Scripts/LivesDemoController.cs` — bootstraps `LivesService` and ticks it every frame.

---

### Requirements

- Unity **2022.3** or newer.
- (Optional) `com.unity.mobile.notifications` for `UnityNotificationScheduler`.

---

[1.0.0]: https://github.com/truongmv/lives-service/releases/tag/v1.0.0
