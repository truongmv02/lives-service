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
        public int Lives;
        public int MaxLives;
        public long RecoveryStartUtc;
        public long InfiniteLivesEndUtc;
    }
}
