# TMV.Lives — Unit Test Reference

`Tests/EditMode/LivesServiceTests.cs` · EditMode · **49 tests** · No Unity runtime required

**Suite defaults:** `_fakeNow = 2026-01-01 00:00:00 UTC` · `DefaultMaxLives = 5` · `SecondsToRecover = 1800`  
Time is advanced by mutating `_fakeNow`; calling `Tick(0f)` forces a recovery pass without a `dt` contribution.

---

## Overview

| Group | Count |
|:------|------:|
| [Initialize](#initialize) | 5 |
| [ConsumeLife](#consumelife) | 7 |
| [Tick / Recovery](#tick--recovery) | 14 |
| [AddLives](#addlives) | 3 |
| [SetMaxLives](#setmaxlives) | 4 |
| [GrantInfinite](#grantinfinite) | 7 |
| [RefillLives](#refilllives) | 2 |
| [TimeUntilNextLife](#timeuntilnextlife) | 3 |
| [Notifications](#notifications) | 4 |
| **Total** | **49** |

---

## Initialize

**1. First launch fills to full**
`Initialize_FirstLaunch_SetsMaxLivesAndFillsToFull`
- **Given** Fresh install — storage empty (`MaxLives = 0`)
- **Expected** `MaxLives = 5`, `Lives = 5`

**2. Stale Lives value clamped on init**
`Initialize_WhenLivesExceedMax_ClampsToMax`
- **Given** `Lives = 99`, `MaxLives = 5` in storage (e.g. VIP boost lapsed)
- **Expected** `Lives` clamped to `5`

**3. State persisted after init**
`Initialize_SavesState`
- **Given** `Initialize` called on a fresh install
- **Expected** `Save` called at least once so next launch sees the defaults

**4. Cold-launch offline recovery — 2 full cycles**
`Initialize_WithStaleRecoveryStart_AppliesOfflineRecovery`
- **Given** `Lives = 1`, app offline for 2 full cycles (60 min)
- **Expected** `Lives = 3` credited before the first `Tick`

**5. Cold-launch offline recovery — 2 cycles + partial**
`Initialize_AfterOfflineMultipleCyclesPlusPartial_RecoversLivesAndShowsRemainingTime`
- **Given** `Lives = 2`, app offline 70 min (2 cycles + 10 min partial)
- **Expected** `Lives = 4`, `TimeUntilNextLife = 20 min` (partial progress preserved)

---

## ConsumeLife

**1. Decrements when lives available**
`ConsumeLife_WithLives_ReturnsTrueAndDecrements`
- **Given** `Lives = 3`
- **Expected** Returns `true`, `Lives = 2`

**2. Returns false when empty**
`ConsumeLife_NoLives_ReturnsFalse`
- **Given** `Lives = 0`
- **Expected** Returns `false`, `Lives` stays `0` (never goes negative)

**3. Returns false without decrement during infinite**
`ConsumeLife_WithInfiniteLives_ReturnsFalseWithoutDecrementing`
- **Given** Infinite active (1h remaining), `Lives = 2`
- **Expected** Returns `false`, `Lives` unchanged at `2`

**4. Starts recovery timer when consuming from full**
`ConsumeLife_FromFull_StartsRecoveryTimer`
- **Given** `Lives = 5` (full)
- **Expected** `RecoveryStartUtc` set to current timestamp

**5. Preserves existing timer when already recovering**
`ConsumeLife_AlreadyRecovering_DoesNotResetTimer`
- **Given** `Lives = 3`, recovery running for 10 min, player consumes another life
- **Expected** `RecoveryStartUtc` unchanged (10 min of partial progress not lost)

**6. Fires OnLivesChanged with post-decrement value**
`ConsumeLife_FiresOnLivesChangedWithNewCount`
- **Given** `Lives = 3`, subscribed to `OnLivesChanged`
- **Expected** Event fires with `2`

**7. No notification scheduled during infinite**
`ConsumeLife_DuringInfinite_DoesNotScheduleNotification`
- **Given** Infinite granted, then `ConsumeLife`
- **Expected** `ScheduleCallCount` unchanged (infinite players don't need a "lives full" push)

---

## Tick / Recovery

**1. No life added before full cycle**
`Tick_BeforeRecoveryPeriod_DoesNotAddLife`
- **Given** `Lives = 3`, advance `SecondsToRecover - 1` seconds
- **Expected** `Lives = 3` (boundary is `elapsed >= SecondsToRecover`)

**2. One life added after exactly one cycle**
`Tick_AfterRecoveryPeriod_AddsOneLife`
- **Given** `Lives = 3`, advance exactly 1 cycle (1800 s)
- **Expected** `Lives = 4`

**3. First life recovers from empty pool**
`Tick_FromZeroLives_RecoversFirstLife`
- **Given** `Lives = 0`, advance 1 cycle
- **Expected** `Lives = 1`

**4. Timer slides forward to preserve partial progress**
`Tick_PartialCycleAfterRecovery_TimerSlidesForward`
- **Given** `Lives = 3`, advance 1 cycle + 10 min
- **Expected** `Lives = 4`, `RecoveryStartUtc = start + 1800 s` (not reset to now; 10 min preserved)

**5. Two cycles plus partial**
`Tick_TwoCyclesPlusPartial_AddsTwoLivesAndPreservesPartialProgress`
- **Given** `Lives = 1`, advance 2 cycles + 500 s
- **Expected** `Lives = 3`, timer slides 2 cycles, `TimeUntilNextLife = 1300 s`

**6. Long offline fills to max and clears timer**
`Tick_LongOffline_FillsToMaxAndStopsTimer`
- **Given** `Lives = 1`, advance 10 cycles (way past max)
- **Expected** `Lives = 5` (capped), `RecoveryStartUtc = 0`

**7. Timer cleared when last life recovers**
`Tick_WhenLivesFullAfterRecovery_StopsTimer`
- **Given** `Lives = 4`, advance 1 cycle (reaches max)
- **Expected** `IsFull = true`, `RecoveryStartUtc = 0`

**8. No event fired when already at max**
`Tick_WhenAtMax_DoesNotFireEvent`
- **Given** `Lives = 5` (full), advance 10 cycles
- **Expected** `OnLivesChanged` never fires

**9. Idempotent with no elapsed time**
`Tick_NoTimePassed_DoesNothingAndDoesNotFireEvent`
- **Given** `Lives = 3`, recovery active, `Tick(0f)` called twice with no time advance
- **Expected** Pure no-op — `Lives`, timer, and event call count all unchanged

**10. Multi-life recovery fires one event with final count**
`Tick_LongOffline_FiresOnLivesChangedOnceWithFinalCount`
- **Given** `Lives = 1`, advance 3 cycles, then `Tick`
- **Expected** `OnLivesChanged` fires **once** with `4` (not once per recovered life)

**11. Recovery suspended during infinite**
`Tick_DuringInfiniteLives_DoesNotRecover`
- **Given** `Lives = 3`, infinite active (2h remaining), advance 3 cycles
- **Expected** `Lives = 3` (recovery suspended while infinite is active)

**12. Infinite expiry clears state and fires event**
`Tick_InfiniteExpired_ClearsInfiniteLivesEndUtcAndFiresOnInfiniteLivesChanged`
- **Given** Infinite ends at `now + 10 s`, advance 20 s
- **Expected** `HasInfiniteLives = false`, `InfiniteLivesEndUtc = 0`, `OnInfiniteLivesChanged` fires once

**13. Recovery progress preserved when infinite expires**
`Tick_InfiniteExpired_PreservesRecoveryProgress`
- **Given** Recovery started 10 min ago; infinite then expires; total elapsed < 1 cycle
- **Expected** Infinite cleared, no life added, `RecoveryStartUtc` unchanged (10 min kept)

**14. Elapsed during infinite counts toward recovery (behavior lock)**
`Tick_InfiniteExpired_ElapsedDuringInfiniteCountsTowardRecovery`
- **Given** `RecoveryStart = now - 600 s`; infinite runs 1800 s; then 700 s more. Total elapsed = 3100 s.
- **Expected** `floor(3100 / 1800) = 1` life credited → `Lives = 4` *(if "pause during infinite" is ever added, update this test)*

---

## AddLives

**1. Capped at MaxLives**
`AddLives_CapsAtMaxLives`
- **Given** `Lives = 4`, `AddLives(10)` (surplus IAP/daily reward)
- **Expected** `Lives = 5` (surplus silently discarded)

**2. Reaching max stops timer and cancels notification**
`AddLives_ToMax_StopsRecoveryTimerAndCancelsNotification`
- **Given** `Lives = 4`, recovery active, `AddLives(1)` → reaches max
- **Expected** `RecoveryStartUtc = 0`, `CancelAllCallCount = 1`

**3. Zero amount is ignored**
`AddLives_Zero_DoesNothing`
- **Given** `AddLives(0)`
- **Expected** No state change, `OnLivesChanged` not fired

---

## SetMaxLives

**1. Higher cap does not auto-fill current lives**
`SetMaxLives_Higher_DoesNotChangeLives`
- **Given** `Lives = 3/5`, `SetMaxLives(7)` (VIP boost)
- **Expected** `MaxLives = 7`, `Lives = 3` unchanged (no auto-fill)

**2. Lower cap clamps current lives down**
`SetMaxLives_LowerThanCurrentLives_ClampsLives`
- **Given** `Lives = 5/5`, `SetMaxLives(3)` (VIP boost lapses)
- **Expected** `Lives = 3`

**3. Lives reach new max — recovery stops**
`SetMaxLives_WhenLivesReachNewMax_StopsRecovery`
- **Given** `Lives = 3`, recovery active, `SetMaxLives(3)` (cap drops to current count)
- **Expected** `RecoveryStartUtc = 0`

**4. Zero is rejected**
`SetMaxLives_Zero_DoesNothing`
- **Given** `SetMaxLives(0)`
- **Expected** Silently rejected, `MaxLives` unchanged

---

## GrantInfinite

**1. Activates infinite lives**
`GrantInfinite_SetsHasInfiniteLivesTrue`
- **Given** `GrantInfinite(3600)`
- **Expected** `HasInfiniteLives = true`

**2. Still active one second before expiry**
`GrantInfinite_OneSecondBeforeExpiry_HasInfiniteLivesTrue`
- **Given** Grant 100 s, advance 99 s
- **Expected** `HasInfiniteLives = true` (check is `now < end`)

**3. Expired at exact boundary**
`GrantInfinite_AtExactExpiryBoundary_HasInfiniteLivesFalse`
- **Given** Grant 100 s, advance exactly 100 s
- **Expected** `HasInfiniteLives = false` (`now == end` counts as expired)

**4. Second grant replaces, not stacks**
`GrantInfinite_WhileActive_ReplacesEndTime`
- **Given** Grant 30 min; 10 min later grant 1h
- **Expected** `InfiniteLivesEndUtc = now + 3600` (not `original_end + 3600`)

**5. Cancels any pending notification**
`GrantInfinite_CancelsScheduledNotification`
- **Given** Pending "lives full" notification exists, then `GrantInfinite`
- **Expected** `CancelAllCallCount > 0`

**6. Fires OnInfiniteLivesChanged**
`GrantInfinite_FiresOnInfiniteLivesChanged`
- **Given** Subscribed to `OnInfiniteLivesChanged`, then `GrantInfinite(3600)`
- **Expected** Event fires exactly once

**7. Zero duration is rejected**
`GrantInfinite_Zero_DoesNothing`
- **Given** `GrantInfinite(0)`
- **Expected** Silently rejected, `HasInfiniteLives = false`

---

## RefillLives

**1. Fills to max, stops timer, cancels notification**
`RefillLives_FillsToMaxAndStopsTimer`
- **Given** `Lives = 2`, recovery active (e.g. rewarded ad)
- **Expected** `Lives = 5`, `RecoveryStartUtc = 0`, `CancelAllCallCount = 1`

**2. Fires OnLivesChanged with max count**
`RefillLives_FiresOnLivesChangedWithMaxCount`
- **Given** `Lives = 2`, subscribed to `OnLivesChanged`
- **Expected** Event fires with `5`

---

## TimeUntilNextLife

**1. Zero when full**
`TimeUntilNextLife_WhenFull_ReturnsZero`
- **Given** `Lives = 5` (full, no recovery active)
- **Expected** `TimeSpan.Zero`

**2. Remaining time while recovering**
`TimeUntilNextLife_WhenRecovering_ReturnsRemainingTime`
- **Given** `Lives = 3`, recovery active, advance 10 min (600 s)
- **Expected** `TimeSpan = 20 min` (1800 − 600 s)

**3. Clamped to zero past boundary (without Tick)**
`TimeUntilNextLife_PastBoundaryWithoutTick_ReturnsZero`
- **Given** Elapsed past 1 cycle but `Tick` not yet called
- **Expected** `TimeSpan.Zero` (property never returns a negative span)

---

## Notifications

**1. Scheduled at recovery time + grace when consuming**
`ConsumeLife_SchedulesNotificationAtFullRecoveryTime`
- **Given** `Lives = 5 → ConsumeLife` (now 4/5, needs 1 cycle)
- **Expected** 1 notification at `now + 1800 s + 3 s grace`

**2. Rescheduled for shorter deficit after AddLives**
`AddLives_PartialFill_ReschedulesNotificationForShortenedDeficit`
- **Given** `Lives = 1/5` recovering; `AddLives(2)` → `Lives = 3/5`
- **Expected** New notification at `startTs + 2 cycles + 3 s grace`

**3. Rescheduled when max cap is lowered**
`SetMaxLives_LowerWhileRecovering_ReschedulesNotificationForShortenedDeficit`
- **Given** `Lives = 2/5` recovering; `SetMaxLives(3)` → deficit drops from 3 to 1
- **Expected** New notification at `startTs + 1 cycle + 3 s grace`

**4. Rescheduled after partial Tick recovery**
`Tick_AfterPartialRecovery_ReschedulesNotificationForRemainingLives`
- **Given** `Lives = 3/5`, advance 1 cycle → recovers to `4/5` (1 still missing)
- **Expected** New notification at `startTs + 2 cycles + 3 s grace`

---

## Test Infrastructure

| Helper | Interface / Type | Notes |
|:-------|:-----------------|:------|
| `FakeLivesStorage` | `ILivesStorage` | In-memory. Tracks `SaveCallCount`. |
| `FakeNotificationScheduler` | `ILivesNotificationScheduler` | Tracks `ScheduleCallCount`, `CancelAllCallCount`, `LastScheduledFireAt`. |
| `ConstConfigProvider` | `LivesConfigProvider` | Returns a fixed `LivesConfig`. Defined inline in the fixture. |
| `Advance(int seconds)` | Method | Shifts `_fakeNow` forward. Does **not** implicitly call `Tick`. |
| `TimestampNow` | Property (`long`) | Converts `_fakeNow` to Unix seconds. |
