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

        /// <summary>
        /// Extra delay after full recovery before firing the notification, 
        /// so a user mid-game is not interrupted.
        /// </summary>
        private const int NotificationGracePeriodSeconds = 3;

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
            long nowTs = NowUnixSeconds();
            bool infiniteActive = _storage.InfiniteLivesEndUtc > 0 && nowTs < _storage.InfiniteLivesEndUtc;
            bool infiniteExpired = false;
            bool livesRecovered = false;

            if (_storage.InfiniteLivesEndUtc > 0 && !infiniteActive)
            {
                _storage.InfiniteLivesEndUtc = 0;
                infiniteExpired = true;
            }

            if (!IsFull && !infiniteActive && _storage.RecoveryStartUtc != 0)
                livesRecovered = ApplyOfflineRecovery(nowTs);

            return (infiniteExpired, livesRecovered);
        }

        /// Computes how many lives should have recovered since RecoveryStartUtc and applies them.
        /// Uses integer division to handle multiple missed intervals in one call (offline catch-up).
        private bool ApplyOfflineRecovery(long nowTs)
        {
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
            if (IsFull || HasInfiniteLives || _notifications == null || _storage.RecoveryStartUtc == 0) return;

            long livesToRecover = _storage.MaxLives - _storage.Lives;
            long fullRecoveryAt = _storage.RecoveryStartUtc + _config.SecondsToRecover * livesToRecover
                                  + NotificationGracePeriodSeconds;

            _notifications.Schedule(
                _config.NotificationTitle,
                _config.NotificationBody,
                DateTime.UnixEpoch.AddSeconds(fullRecoveryAt));
        }

        #endregion
    }
}
