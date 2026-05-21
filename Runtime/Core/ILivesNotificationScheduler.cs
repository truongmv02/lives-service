using System;

namespace TMV.Lives
{
    /// <summary>
    /// Schedules and cancels the push notification for when lives are fully recovered.
    /// Only one notification is tracked at a time — Schedule replaces the previous one.
    /// </summary>
    public interface ILivesNotificationScheduler
    {
        /// <summary>Schedule (or replace) the full-lives notification.</summary>
        void Schedule(string title, string body, DateTime fireAtUtc);

        /// <summary>Cancel any pending notification.</summary>
        void CancelAll();
    }
}
