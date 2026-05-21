using UnityEngine;

namespace TMV.Lives.Samples
{
    /// <summary>
    /// ScriptableObject wrapper for LivesConfig. Lets designers tune parameters in the Inspector.
    /// Create via Assets → Create → TMV → Lives → Config.
    /// </summary>
    [CreateAssetMenu(fileName = "LivesConfig", menuName = "TMV/Lives/Config")]
    public sealed class LivesConfigSO : ScriptableObject
    {
        #region Properties

        /// <summary>The serialized config data tuned in the Inspector.</summary>
        [field: SerializeField] public LivesConfig Config { get; private set; } = new LivesConfig();

        #endregion

        #region Public Methods

        /// <summary>Returns a <see cref="LivesConfigProvider"/> that serves the SerializedField config.</summary>
        public LivesConfigProvider ToProvider() => new InlineProvider(Config);

        #endregion

        #region Private/Protected Methods

        private sealed class InlineProvider : LivesConfigProvider
        {
            private readonly LivesConfig _config;
            public InlineProvider(LivesConfig config) => _config = config;
            public override LivesConfig Get() => _config;
        }

        #endregion
    }
}
