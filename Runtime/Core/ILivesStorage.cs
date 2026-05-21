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
