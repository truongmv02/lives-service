using System;

#if UNITY_ANDROID
using Unity.Notifications.Android;
#elif UNITY_IOS
using Unity.Notifications.iOS;
#endif

namespace TMV.Lives.Unity
{
    /// <summary>
    /// ILivesNotificationScheduler backed by Unity Mobile Notifications.
    /// Requires the com.unity.mobile.notifications package.
    /// Supports Android and iOS via platform compile guards.
    /// </summary>
    public sealed class UnityNotificationScheduler : ILivesNotificationScheduler
    {
        #region Fields

        private const string ChannelId      = "tmv_lives";
        private const int    NotificationId = 0x4C495645; // "LIVE"

        #endregion

        #region Public Methods

        public void Schedule(string title, string body, DateTime fireAtUtc)
        {
            CancelAll();

#if UNITY_ANDROID
            ScheduleAndroid(title, body, fireAtUtc);
#elif UNITY_IOS
            ScheduleIos(title, body, fireAtUtc);
#endif
        }

        public void CancelAll()
        {
#if UNITY_ANDROID
            AndroidNotificationCenter.CancelScheduledNotification(NotificationId);
#elif UNITY_IOS
            iOSNotificationCenter.RemoveScheduledNotification(NotificationId.ToString());
#endif
        }

        #endregion

        #region Private/Protected Methods

#if UNITY_ANDROID
        private static bool _androidChannelRegistered;

        private static void EnsureAndroidChannel()
        {
            if (_androidChannelRegistered) return;

            var channel = new AndroidNotificationChannel
            {
                Id          = ChannelId,
                Name        = "Lives",
                Importance  = Importance.Default,
                Description = "Notifications for the lives system."
            };
            AndroidNotificationCenter.RegisterNotificationChannel(channel);
            _androidChannelRegistered = true;
        }

        private static void ScheduleAndroid(string title, string body, DateTime fireAtUtc)
        {
            // Android 8+ requires a registered channel before any notification can be posted.
            EnsureAndroidChannel();

            var notification = new AndroidNotification
            {
                Title     = title,
                Text      = body,
                FireTime  = fireAtUtc.ToLocalTime(),
                SmallIcon = "icon_small",
                LargeIcon = "icon_large"
            };

            AndroidNotificationCenter.SendNotificationWithExplicitID(
                notification, ChannelId, NotificationId);
        }
#elif UNITY_IOS
        private static void ScheduleIos(string title, string body, DateTime fireAtUtc)
        {
            var trigger = new iOSNotificationCalendarTrigger
            {
                Year   = fireAtUtc.ToLocalTime().Year,
                Month  = fireAtUtc.ToLocalTime().Month,
                Day    = fireAtUtc.ToLocalTime().Day,
                Hour   = fireAtUtc.ToLocalTime().Hour,
                Minute = fireAtUtc.ToLocalTime().Minute,
                Second = fireAtUtc.ToLocalTime().Second,
                Repeats = false
            };

            var notification = new iOSNotification
            {
                Identifier            = NotificationId.ToString(),
                Title                 = title,
                Body                  = body,
                ShowInForeground      = false,
                ForegroundPresentationOption = PresentationOption.Alert | PresentationOption.Sound,
                Trigger               = trigger
            };

            iOSNotificationCenter.ScheduleNotification(notification);
        }
#endif

        #endregion
    }
}
