using System;
using NUnit.Framework;

namespace TMV.Lives.Tests
{
    [TestFixture]
    public class LivesServiceTests
    {
        private DateTime _fakeNow;
        private FakeLivesStorage _storage;
        private FakeNotificationScheduler _notifications;
        private LivesConfig _config;

        private long TimestampNow => (long)(_fakeNow - DateTime.UnixEpoch).TotalSeconds;

        [SetUp]
        public void SetUp()
        {
            _fakeNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _storage = new FakeLivesStorage();
            _notifications = new FakeNotificationScheduler();
            _config = new LivesConfig
            {
                DefaultMaxLives   = 5,
                SecondsToRecover  = 1800,
                NotificationTitle = "Lives",
                NotificationBody  = "Full!"
            };
        }

        private LivesService Build()
        {
            var provider = new ConstConfigProvider(_config);
            return new LivesService(_storage, provider, () => _fakeNow, _notifications);
        }

        private void Advance(int seconds) => _fakeNow = _fakeNow.AddSeconds(seconds);

        // ── Initialize ────────────────────────────────────────────────────

        [Test]
        public void Initialize_FirstLaunch_SetsMaxLivesAndFillsToFull()
        {
            // Scenario: fresh install — storage is empty (MaxLives=0).
            // Expected: Initialize applies DefaultMaxLives=5 and starts the player full (Lives=5/5).
            var sut = Build();
            sut.Initialize();
            Assert.That(sut.MaxLives, Is.EqualTo(_config.DefaultMaxLives));
            Assert.That(sut.Lives, Is.EqualTo(_config.DefaultMaxLives));
        }

        [Test]
        public void Initialize_WhenLivesExceedMax_ClampsToMax()
        {
            // Scenario: stored Lives=99 but MaxLives=5 (e.g., MaxLives was reduced after a VIP boost lapsed).
            // Expected: Initialize clamps Lives down to 5.
            _storage.MaxLives = 5;
            _storage.Lives    = 99;
            var sut = Build();
            sut.Initialize();
            Assert.That(sut.Lives, Is.EqualTo(5));
        }

        [Test]
        public void Initialize_SavesState()
        {
            // Scenario: Initialize is called on a fresh install.
            // Expected: state is persisted at least once so subsequent launches see the defaults.
            var sut = Build();
            sut.Initialize();
            Assert.That(_storage.SaveCallCount, Is.GreaterThan(0));
        }

        [Test]
        public void Initialize_WithStaleRecoveryStart_AppliesOfflineRecovery()
        {
            // Scenario: app was closed for 2 full cycles while Lives=1; cold-launch now.
            // Expected: Initialize credits 2 recovered lives → Lives=3.
            _storage.MaxLives = 5; _storage.Lives = 1;
            _storage.RecoveryStartUtc = TimestampNow - _config.SecondsToRecover * 2;
            var sut = Build();
            sut.Initialize();
            Assert.That(sut.Lives, Is.EqualTo(3));
        }

        [Test]
        public void Initialize_AfterOfflineMultipleCyclesPlusPartial_RecoversLivesAndShowsRemainingTime()
        {
            // Scenario: Lives=2, recovery=30 min, app offline 70 min → 2 full cycles + 10 min partial.
            // Expected on reopen: Lives=4 (2 + 2 recovered), TimeUntilNextLife = 20 min (30 - 10).
            _storage.MaxLives = 5; _storage.Lives = 2;
            _storage.RecoveryStartUtc = TimestampNow - (_config.SecondsToRecover * 2 + 600);
            var sut = Build();
            sut.Initialize();
            Assert.That(sut.Lives, Is.EqualTo(4));
            Assert.That(sut.TimeUntilNextLife,
                Is.EqualTo(TimeSpan.FromSeconds(_config.SecondsToRecover - 600)));
        }

        // ── ConsumeLife ───────────────────────────────────────────────────

        [Test]
        public void ConsumeLife_WithLives_ReturnsTrueAndDecrements()
        {
            // Scenario: Lives=3, player starts a level.
            // Expected: ConsumeLife returns true, Lives=2.
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            Assert.That(sut.ConsumeLife(), Is.True);
            Assert.That(sut.Lives, Is.EqualTo(2));
        }

        [Test]
        public void ConsumeLife_NoLives_ReturnsFalse()
        {
            // Scenario: Lives=0, player tries to start a level.
            // Expected: ConsumeLife returns false, Lives stays at 0 (never goes negative).
            _storage.Lives = 0; _storage.MaxLives = 5;
            var sut = Build();
            Assert.That(sut.ConsumeLife(), Is.False);
            Assert.That(sut.Lives, Is.EqualTo(0));
        }

        [Test]
        public void ConsumeLife_WithInfiniteLives_ReturnsFalseWithoutDecrementing()
        {
            // Scenario: infinite active (1h remaining), Lives=2, player starts a level.
            // Expected: returns false (no charge), Lives unchanged at 2.
            _storage.Lives = 2; _storage.MaxLives = 5;
            _storage.InfiniteLivesEndUtc = TimestampNow + 3600;
            var sut = Build();
            Assert.That(sut.ConsumeLife(), Is.False);
            Assert.That(sut.Lives, Is.EqualTo(2));
        }

        [Test]
        public void ConsumeLife_FromFull_StartsRecoveryTimer()
        {
            // Scenario: Lives=5 (full), player consumes 1.
            // Expected: recovery timer starts at "now".
            _storage.Lives = 5; _storage.MaxLives = 5;
            var sut = Build();
            sut.ConsumeLife();
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(TimestampNow));
        }

        [Test]
        public void ConsumeLife_AlreadyRecovering_DoesNotResetTimer()
        {
            // Scenario: Lives=3, already recovering for 10 min, player consumes another life.
            // Expected: existing timer is preserved (NOT reset to now) so the player keeps their 10 min of progress.
            long originalStart = TimestampNow - 600;
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = originalStart;
            var sut = Build();
            sut.ConsumeLife();
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(originalStart));
        }

        [Test]
        public void ConsumeLife_FiresOnLivesChangedWithNewCount()
        {
            // Scenario: Lives=3, subscribe to OnLivesChanged, then consume.
            // Expected: event fires with the post-decrement value 2.
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            int received = -1;
            sut.OnLivesChanged += v => received = v;
            sut.ConsumeLife();
            Assert.That(received, Is.EqualTo(2));
        }

        [Test]
        public void ConsumeLife_DuringInfinite_DoesNotScheduleNotification()
        {
            // Scenario: infinite granted, then player tries to consume a life.
            // Expected: no "lives full" notification is scheduled — infinite players don't need it.
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            sut.GrantInfinite(3600);
            int countAfterGrant = _notifications.ScheduleCallCount;
            sut.ConsumeLife();
            Assert.That(_notifications.ScheduleCallCount, Is.EqualTo(countAfterGrant));
        }

        // ── Tick / Recovery ───────────────────────────────────────────────

        [Test]
        public void Tick_BeforeRecoveryPeriod_DoesNotAddLife()
        {
            // Scenario: Lives=3, recovery just started, advance 1 second short of one full cycle.
            // Expected: no life added (boundary is strictly "elapsed >= SecondsToRecover").
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(_config.SecondsToRecover - 1);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(3));
        }

        [Test]
        public void Tick_AfterRecoveryPeriod_AddsOneLife()
        {
            // Scenario: Lives=3, advance exactly one cycle (30 min).
            // Expected: 1 life added → Lives=4.
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(_config.SecondsToRecover);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(4));
        }

        [Test]
        public void Tick_FromZeroLives_RecoversFirstLife()
        {
            // Scenario: Lives=0 (empty pool), advance one cycle.
            // Expected: first life recovers cleanly → Lives=1.
            _storage.Lives = 0; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(_config.SecondsToRecover);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(1));
        }

        [Test]
        public void Tick_PartialCycleAfterRecovery_TimerSlidesForward()
        {
            // Scenario: Lives=3, advance 1 cycle + 10 min into the next.
            // Expected: 1 life added → Lives=4; timer slides to start+30min (NOT reset to now), so the 10 min of partial progress is preserved.
            long startTs = TimestampNow;
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            var sut = Build();
            Advance(_config.SecondsToRecover + 600);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(4));
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(startTs + _config.SecondsToRecover));
        }

        [Test]
        public void Tick_TwoCyclesPlusPartial_AddsTwoLivesAndPreservesPartialProgress()
        {
            // Scenario: Lives=1, advance 2 cycles + 500 seconds.
            // Expected: 2 lives added → Lives=3; timer slides forward by 2 cycles; TimeUntilNextLife = SecondsToRecover - 500 (the leftover modulo).
            long startTs = TimestampNow;
            _storage.Lives = 1; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            var sut = Build();
            Advance(_config.SecondsToRecover * 2 + 500);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(3));
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(startTs + _config.SecondsToRecover * 2));
            Assert.That(sut.TimeUntilNextLife,
                Is.EqualTo(TimeSpan.FromSeconds(_config.SecondsToRecover - 500)));
        }

        [Test]
        public void Tick_LongOffline_FillsToMaxAndStopsTimer()
        {
            // Scenario: Lives=1, advance 10 cycles (way past max).
            // Expected: Lives caps at 5; timer is cleared (RecoveryStartUtc=0) since the player is full.
            _storage.Lives = 1; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(_config.SecondsToRecover * 10);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(5));
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(0));
        }

        [Test]
        public void Tick_WhenLivesFullAfterRecovery_StopsTimer()
        {
            // Scenario: Lives=4, advance one cycle (recovers the final life to reach max).
            // Expected: IsFull=true, timer cleared.
            _storage.Lives = 4; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(_config.SecondsToRecover);
            sut.Tick(0f);
            Assert.That(sut.IsFull, Is.True);
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(0));
        }

        [Test]
        public void Tick_WhenAtMax_DoesNotFireEvent()
        {
            // Scenario: Lives=5 (already full), advance 10 cycles.
            // Expected: OnLivesChanged never fires (nothing to report when nothing changes).
            _storage.Lives = 5; _storage.MaxLives = 5;
            var sut = Build();
            int callCount = 0;
            sut.OnLivesChanged += _ => callCount++;
            Advance(_config.SecondsToRecover * 10);
            sut.Tick(0f);
            Assert.That(callCount, Is.EqualTo(0));
        }

        [Test]
        public void Tick_NoTimePassed_DoesNothingAndDoesNotFireEvent()
        {
            // Scenario: Lives=3, recovery active, call Tick twice in a row with no elapsed time.
            // Expected: pure no-op — Lives unchanged, timer unchanged, no event fired.
            long startTs = TimestampNow;
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            var sut = Build();
            int callCount = 0;
            sut.OnLivesChanged += _ => callCount++;
            sut.Tick(0f);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(3));
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(startTs));
            Assert.That(callCount, Is.EqualTo(0));
        }

        [Test]
        public void Tick_LongOffline_FiresOnLivesChangedOnceWithFinalCount()
        {
            // Scenario: Lives=1, advance 3 cycles, subscribe to OnLivesChanged, then Tick.
            // Expected: event fires exactly ONCE with the final value (4), not once per recovered life.
            _storage.Lives = 1; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            int callCount = 0;
            int lastValue = -1;
            sut.OnLivesChanged += v => { callCount++; lastValue = v; };
            Advance(_config.SecondsToRecover * 3);
            sut.Tick(0f);
            Assert.That(callCount, Is.EqualTo(1));
            Assert.That(lastValue, Is.EqualTo(4));
        }

        [Test]
        public void Tick_DuringInfiniteLives_DoesNotRecover()
        {
            // Scenario: Lives=3, infinite active (2h remaining), advance 3 cycles.
            // Expected: Lives unchanged at 3 — recovery is suspended while infinite is active.
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            _storage.InfiniteLivesEndUtc = TimestampNow + 7200;
            var sut = Build();
            Advance(_config.SecondsToRecover * 3);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(3));
        }

        [Test]
        public void Tick_InfiniteExpired_ClearsInfiniteLivesEndUtcAndFiresOnInfiniteLivesChanged()
        {
            // Scenario: infinite ends at now+10s, advance 20s, Tick.
            // Expected: HasInfiniteLives=false, stored end timestamp cleared, OnInfiniteLivesChanged fires once.
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.InfiniteLivesEndUtc = TimestampNow + 10;
            var sut = Build();
            Advance(20);
            int callCount = 0;
            sut.OnInfiniteLivesChanged += () => callCount++;
            sut.Tick(0f);
            Assert.That(sut.HasInfiniteLives, Is.False);
            Assert.That(_storage.InfiniteLivesEndUtc, Is.EqualTo(0));
            Assert.That(callCount, Is.EqualTo(1));
        }

        [Test]
        public void Tick_InfiniteExpired_PreservesRecoveryProgress()
        {
            // Scenario: Lives=3, recovery started 10 min before infinite kicked in; infinite then expires
            // shortly after, with total elapsed = 620s (< 1 cycle).
            // Expected: infinite cleared, no life added yet, RecoveryStartUtc preserved so the 10 min of progress is not lost.
            long originalStart = TimestampNow - 600;
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = originalStart;
            _storage.InfiniteLivesEndUtc = TimestampNow + 10;
            var sut = Build();
            Advance(20);
            sut.Tick(0f);
            Assert.That(sut.HasInfiniteLives, Is.False);
            Assert.That(sut.Lives, Is.EqualTo(3));
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(originalStart));
        }

        [Test]
        public void Tick_InfiniteExpired_ElapsedDuringInfiniteCountsTowardRecovery()
        {
            // Scenario: RecoveryStart=now-600s, then infinite runs for 1 cycle (1800s), then 700s more pass.
            // Total elapsed since RecoveryStart = 600 + 1800 + 700 = 3100s.
            // Expected (locks in current behavior): 3100/1800 = 1 life credited → Lives=4. The recovery timer
            // KEEPS counting during infinite — if we ever decide to "pause" it, this test must change.
            long startTs = TimestampNow - 600;
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            _storage.InfiniteLivesEndUtc = TimestampNow + _config.SecondsToRecover;
            var sut = Build();
            Advance(_config.SecondsToRecover + 700);
            sut.Tick(0f);
            Assert.That(sut.HasInfiniteLives, Is.False);
            Assert.That(sut.Lives, Is.EqualTo(4));
        }

        // ── AddLives ──────────────────────────────────────────────────────

        [Test]
        public void AddLives_CapsAtMaxLives()
        {
            // Scenario: Lives=4, AddLives(10) — surplus rewards from an IAP or daily reward.
            // Expected: Lives caps at MaxLives=5; the extra 9 is silently discarded.
            _storage.Lives = 4; _storage.MaxLives = 5;
            var sut = Build();
            sut.AddLives(10);
            Assert.That(sut.Lives, Is.EqualTo(5));
        }

        [Test]
        public void AddLives_ToMax_StopsRecoveryTimerAndCancelsNotification()
        {
            // Scenario: Lives=4 with recovery active, player gets +1 life as reward → reaches max.
            // Expected: timer cleared, pending "lives full" notification cancelled (player is already full).
            _storage.Lives = 4; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            sut.AddLives(1);
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(0));
            Assert.That(_notifications.CancelAllCallCount, Is.EqualTo(1));
        }

        [Test]
        public void AddLives_Zero_DoesNothing()
        {
            // Scenario: AddLives(0) — invalid/degenerate input.
            // Expected: no state change, no event fired (we don't notify subscribers of nothing).
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            int callCount = 0;
            sut.OnLivesChanged += _ => callCount++;
            sut.AddLives(0);
            Assert.That(sut.Lives, Is.EqualTo(3));
            Assert.That(callCount, Is.EqualTo(0));
        }

        // ── SetMaxLives ───────────────────────────────────────────────────

        [Test]
        public void SetMaxLives_Higher_DoesNotChangeLives()
        {
            // Scenario: Lives=3/5, SetMaxLives(7) — e.g., VIP boost increases the cap.
            // Expected: MaxLives=7, but current Lives stays at 3 (no auto-fill).
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            sut.SetMaxLives(7);
            Assert.That(sut.Lives, Is.EqualTo(3));
            Assert.That(sut.MaxLives, Is.EqualTo(7));
        }

        [Test]
        public void SetMaxLives_LowerThanCurrentLives_ClampsLives()
        {
            // Scenario: Lives=5/5, SetMaxLives(3) — e.g., VIP boost lapses.
            // Expected: Lives clamped down to the new max.
            _storage.Lives = 5; _storage.MaxLives = 5;
            var sut = Build();
            sut.SetMaxLives(3);
            Assert.That(sut.Lives, Is.EqualTo(3));
        }

        [Test]
        public void SetMaxLives_WhenLivesReachNewMax_StopsRecovery()
        {
            // Scenario: Lives=3, recovery active, SetMaxLives(3) — Lives is now at max because the cap dropped.
            // Expected: timer cleared (player is full, nothing to recover toward).
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            sut.SetMaxLives(3);
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(0));
        }

        [Test]
        public void SetMaxLives_Zero_DoesNothing()
        {
            // Scenario: SetMaxLives(0) — invalid input.
            // Expected: silently rejected, MaxLives unchanged.
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            sut.SetMaxLives(0);
            Assert.That(sut.MaxLives, Is.EqualTo(5));
        }

        // ── GrantInfinite ─────────────────────────────────────────────────

        [Test]
        public void GrantInfinite_SetsHasInfiniteLivesTrue()
        {
            // Scenario: GrantInfinite(1h).
            // Expected: HasInfiniteLives becomes true immediately.
            _storage.MaxLives = 5; _storage.Lives = 5;
            var sut = Build();
            sut.GrantInfinite(3600);
            Assert.That(sut.HasInfiniteLives, Is.True);
        }

        [Test]
        public void GrantInfinite_OneSecondBeforeExpiry_HasInfiniteLivesTrue()
        {
            // Scenario: grant 100s of infinite, advance 99s (1 second before end).
            // Expected: still active. (Boundary check for the `<` semantic — lower side.)
            _storage.MaxLives = 5; _storage.Lives = 5;
            var sut = Build();
            sut.GrantInfinite(100);
            Advance(99);
            Assert.That(sut.HasInfiniteLives, Is.True);
        }

        [Test]
        public void GrantInfinite_AtExactExpiryBoundary_HasInfiniteLivesFalse()
        {
            // Scenario: grant 100s of infinite, advance exactly 100s.
            // Expected: already expired — the check is `now < end`, so now==end means expired.
            _storage.MaxLives = 5; _storage.Lives = 5;
            var sut = Build();
            sut.GrantInfinite(100);
            Advance(100);
            Assert.That(sut.HasInfiniteLives, Is.False);
        }

        [Test]
        public void GrantInfinite_WhileActive_ReplacesEndTime()
        {
            // Scenario: grant 30 min of infinite, 10 min later grant another 1h.
            // Expected: the second grant REPLACES (not stacks): new end = now + 1h. So end = (start+600) + 3600.
            _storage.MaxLives = 5; _storage.Lives = 5;
            var sut = Build();
            sut.GrantInfinite(1800);
            Advance(600);
            long expectedEnd = TimestampNow + 3600;
            sut.GrantInfinite(3600);
            Assert.That(_storage.InfiniteLivesEndUtc, Is.EqualTo(expectedEnd));
        }

        [Test]
        public void GrantInfinite_CancelsScheduledNotification()
        {
            // Scenario: Lives=3 with a pending "lives full" notification, then GrantInfinite.
            // Expected: notifications cancelled — infinite players don't need to be told they're full.
            _storage.Lives = 3; _storage.MaxLives = 5;
            var sut = Build();
            sut.GrantInfinite(3600);
            Assert.That(_notifications.CancelAllCallCount, Is.GreaterThan(0));
        }

        [Test]
        public void GrantInfinite_FiresOnInfiniteLivesChanged()
        {
            // Scenario: subscribe to OnInfiniteLivesChanged, then GrantInfinite.
            // Expected: event fires exactly once so the UI can switch into the "infinite" visual.
            _storage.MaxLives = 5; _storage.Lives = 5;
            var sut = Build();
            int callCount = 0;
            sut.OnInfiniteLivesChanged += () => callCount++;
            sut.GrantInfinite(3600);
            Assert.That(callCount, Is.EqualTo(1));
        }

        [Test]
        public void GrantInfinite_Zero_DoesNothing()
        {
            // Scenario: GrantInfinite(0) — invalid input.
            // Expected: silently rejected, HasInfiniteLives stays false.
            _storage.MaxLives = 5; _storage.Lives = 5;
            var sut = Build();
            sut.GrantInfinite(0);
            Assert.That(sut.HasInfiniteLives, Is.False);
        }

        // ── RefillLives ───────────────────────────────────────────────────

        [Test]
        public void RefillLives_FillsToMaxAndStopsTimer()
        {
            // Scenario: Lives=2 with recovery active, RefillLives (e.g., from a rewarded ad).
            // Expected: Lives=5, timer cleared, pending notification cancelled.
            _storage.Lives = 2; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            sut.RefillLives();
            Assert.That(sut.Lives, Is.EqualTo(5));
            Assert.That(_storage.RecoveryStartUtc, Is.EqualTo(0));
            Assert.That(_notifications.CancelAllCallCount, Is.EqualTo(1));
        }

        [Test]
        public void RefillLives_FiresOnLivesChangedWithMaxCount()
        {
            // Scenario: Lives=2, subscribe to OnLivesChanged, then RefillLives.
            // Expected: event fires with the max value (5) so the UI updates in one shot.
            _storage.Lives = 2; _storage.MaxLives = 5;
            var sut = Build();
            int received = -1;
            sut.OnLivesChanged += v => received = v;
            sut.RefillLives();
            Assert.That(received, Is.EqualTo(5));
        }

        // ── TimeUntilNextLife ─────────────────────────────────────────────

        [Test]
        public void TimeUntilNextLife_WhenFull_ReturnsZero()
        {
            // Scenario: Lives=5 (full, no recovery active).
            // Expected: countdown is Zero (nothing to wait for).
            _storage.Lives = 5; _storage.MaxLives = 5;
            var sut = Build();
            Assert.That(sut.TimeUntilNextLife, Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void TimeUntilNextLife_WhenRecovering_ReturnsRemainingTime()
        {
            // Scenario: Lives=3, recovery started, advance 10 min into the cycle.
            // Expected: countdown = SecondsToRecover - 10 min (= 20 min remaining).
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(600);
            var expected = TimeSpan.FromSeconds(_config.SecondsToRecover - 600);
            Assert.That(sut.TimeUntilNextLife, Is.EqualTo(expected));
        }

        [Test]
        public void TimeUntilNextLife_PastBoundaryWithoutTick_ReturnsZero()
        {
            // Scenario: elapsed already crossed the cycle (1 cycle + 200s) but no Tick has been called yet,
            // so the recovery has not been applied. Reading the countdown right now.
            // Expected: clamps to Zero — the property must never return a negative span.
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = TimestampNow;
            var sut = Build();
            Advance(_config.SecondsToRecover + 200);
            Assert.That(sut.TimeUntilNextLife, Is.EqualTo(TimeSpan.Zero));
        }

        // ── Notifications ─────────────────────────────────────────────────

        [Test]
        public void ConsumeLife_SchedulesNotificationAtFullRecoveryTime()
        {
            // Scenario: Lives=5 → ConsumeLife (now 4/5, needs 1 cycle to refill).
            // Expected: exactly one notification is scheduled, firing at now + SecondsToRecover (the moment Lives reaches max again).
            _storage.Lives = 5; _storage.MaxLives = 5;
            var sut = Build();
            sut.ConsumeLife();
            Assert.That(_notifications.ScheduleCallCount, Is.EqualTo(1));
            var expectedFireAt = DateTime.UnixEpoch.AddSeconds(
                TimestampNow + _config.SecondsToRecover);
            Assert.That(_notifications.LastScheduledFireAt, Is.EqualTo(expectedFireAt));
        }

        [Test]
        public void AddLives_PartialFill_ReschedulesNotificationForShortenedDeficit()
        {
            // Scenario: Lives=1/5 recovering, then AddLives(2) brings Lives to 3/5.
            // Expected: the pending "lives full" notification is rescheduled for the new (shorter) deficit
            // — fire-at = RecoveryStartUtc + SecondsToRecover * (5 - 3) = startTs + 2 cycles.
            long startTs = TimestampNow;
            _storage.Lives = 1; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            var sut = Build();
            int initialScheduleCount = _notifications.ScheduleCallCount;
            sut.AddLives(2);
            Assert.That(_notifications.ScheduleCallCount, Is.GreaterThan(initialScheduleCount));
            var expectedFireAt = DateTime.UnixEpoch.AddSeconds(startTs + _config.SecondsToRecover * 2);
            Assert.That(_notifications.LastScheduledFireAt, Is.EqualTo(expectedFireAt));
        }

        [Test]
        public void SetMaxLives_LowerWhileRecovering_ReschedulesNotificationForShortenedDeficit()
        {
            // Scenario: Lives=2/5 recovering, SetMaxLives(3) — cap shrank, deficit drops from 3 to 1.
            // Expected: notification rescheduled for the new deficit: startTs + 1 cycle.
            long startTs = TimestampNow;
            _storage.Lives = 2; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            var sut = Build();
            int initialScheduleCount = _notifications.ScheduleCallCount;
            sut.SetMaxLives(3);
            Assert.That(sut.IsFull, Is.False);
            Assert.That(_notifications.ScheduleCallCount, Is.GreaterThan(initialScheduleCount));
            var expectedFireAt = DateTime.UnixEpoch.AddSeconds(startTs + _config.SecondsToRecover);
            Assert.That(_notifications.LastScheduledFireAt, Is.EqualTo(expectedFireAt));
        }

        [Test]
        public void Tick_AfterPartialRecovery_ReschedulesNotificationForRemainingLives()
        {
            // Scenario: Lives=3 recovering, advance 1 cycle, Tick (recovers 1 life → 4/5, 1 life still missing).
            // Expected: a new notification is scheduled for when the LAST missing life would refill —
            // i.e., at startTs + 2 cycles, so the player gets pinged exactly when they hit max.
            long startTs = TimestampNow;
            _storage.Lives = 3; _storage.MaxLives = 5;
            _storage.RecoveryStartUtc = startTs;
            var sut = Build();
            int initialScheduleCount = _notifications.ScheduleCallCount;
            Advance(_config.SecondsToRecover);
            sut.Tick(0f);
            Assert.That(sut.Lives, Is.EqualTo(4));
            Assert.That(_notifications.ScheduleCallCount, Is.GreaterThan(initialScheduleCount));
            var expectedFireAt = DateTime.UnixEpoch.AddSeconds(startTs + _config.SecondsToRecover * 2);
            Assert.That(_notifications.LastScheduledFireAt, Is.EqualTo(expectedFireAt));
        }

        // ── Helper ────────────────────────────────────────────────────────

        private sealed class ConstConfigProvider : LivesConfigProvider
        {
            private readonly LivesConfig _config;
            public ConstConfigProvider(LivesConfig config) => _config = config;
            public override LivesConfig Get() => _config;
        }
    }
}
