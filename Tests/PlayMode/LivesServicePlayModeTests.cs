using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace TMV.Lives.Tests
{
    /// <summary>
    /// PlayMode tests for LivesService — use when Unity runtime behaviour (coroutines,
    /// MonoBehaviour lifecycle, frame timing) must be validated alongside the service.
    /// </summary>
    public class LivesServicePlayModeTests
    {
        [UnityTest]
        public IEnumerator Tick_CalledEachFrame_RecoveryAdvancesInRealTime()
        {
            var storage = new FakeLivesStorage { Lives = 4, MaxLives = 5 };
            var notifications = new FakeNotificationScheduler();
            var config = new LivesConfig { DefaultMaxLives = 5, SecondsToRecover = 1 };

            // Freeze time just before recovery completes.
            var fakeNow = DateTime.UtcNow;
            storage.RecoveryStartUtc = (long)(fakeNow - DateTime.UnixEpoch).TotalSeconds;

            var sut = new LivesService(storage, new ConstConfigProvider(config), () => fakeNow, notifications);
            sut.Initialize();

            // Advance clock past the recovery threshold.
            fakeNow = fakeNow.AddSeconds(2);
            sut.Tick(0f);

            Assert.That(sut.Lives, Is.EqualTo(5));
            yield return null;
        }

        private sealed class ConstConfigProvider : LivesConfigProvider
        {
            private readonly LivesConfig _config;
            public ConstConfigProvider(LivesConfig config) => _config = config;
            public override LivesConfig Get() => _config;
        }
    }
}
