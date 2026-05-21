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
