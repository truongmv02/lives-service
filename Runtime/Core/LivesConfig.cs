using System;

namespace TMV.Lives
{
    /// <summary>
    /// Tuning parameters for the lives system. Back with a ScriptableObject at the call site
    /// if Inspector editing is needed — the service has no knowledge of ScriptableObject.
    /// </summary>
    [Serializable]
    public sealed class LivesConfig
    {
        /// <summary>Lives count on first launch and the default cap.</summary>
        public int DefaultMaxLives = 5;

        /// <summary>Seconds between each life recovery.</summary>
        public int SecondsToRecover = 1800;

        /// <summary>Title of the push notification sent when lives are full.</summary>
        public string NotificationTitle = "Lives";

        /// <summary>Body of the push notification sent when lives are full.</summary>
        public string NotificationBody = "Your lives are full!";
    }
}
