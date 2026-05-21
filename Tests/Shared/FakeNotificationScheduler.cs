using System;

namespace TMV.Lives.Tests
{
    public sealed class FakeNotificationScheduler : ILivesNotificationScheduler
    {
        public int      ScheduleCallCount   { get; private set; }
        public int      CancelAllCallCount  { get; private set; }
        public DateTime LastScheduledFireAt { get; private set; }

        public void Schedule(string title, string body, DateTime fireAtUtc)
        {
            ScheduleCallCount++;
            LastScheduledFireAt = fireAtUtc;
        }

        public void CancelAll() => CancelAllCallCount++;
    }
}
