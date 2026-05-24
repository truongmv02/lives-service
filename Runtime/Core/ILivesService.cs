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
        /// Attempt to spend one life. Returns false when Lives == 0 (and infinite is not active),
        /// or when infinite lives are active (count is unchanged in both cases).
        /// Check HasInfiniteLives to distinguish the two.
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
