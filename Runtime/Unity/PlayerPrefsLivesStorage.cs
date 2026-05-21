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
