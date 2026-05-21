using System;
using TMV.Lives.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace TMV.Lives.Samples
{
    /// <summary>
    /// Demo MonoBehaviour. Attach to a GameObject in a test scene and wire all Inspector references.
    /// The service is constructed at Start using PlayerPrefs storage so state persists between sessions.
    /// </summary>
    public sealed class LivesDemoController : MonoBehaviour
    {
        #region Fields

        [Header("Config")]
        [Tooltip("Optional. Leave empty to use LivesConfig defaults.")]
        [SerializeField] private LivesConfigSO _configSO;

        [Tooltip("Seeds the in-scene InputField on first display.")]
        [SerializeField] private int _defaultInfiniteDurationSeconds = 30;

        [Header("Display")]
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _livesText;
        [SerializeField] private Text _statusText;
        [SerializeField] private Text _infiniteTimerText;
        [SerializeField] private Text _timerText;
        [SerializeField] private Text _configText;
        [SerializeField] private Text _feedbackText;

        [Header("Input")]
        [SerializeField] private InputField _infiniteSecondsInput;

        [Header("Buttons")]
        [SerializeField] private Button _addLifeButton;
        [SerializeField] private Button _consumeLifeButton;
        [SerializeField] private Button _refillButton;
        [SerializeField] private Button _grantInfiniteButton;
        [SerializeField] private Button _resetButton;

        private ILivesService _service;
        private ILivesStorage _storage;
        private LivesConfig _config;

        #endregion

        #region Unity Callbacks

        private void Start()
        {
            _storage = new PlayerPrefsLivesStorage();
            _config = _configSO != null ? _configSO.Config : new LivesConfig();
            LivesConfigProvider provider = _configSO != null ? _configSO.ToProvider() : new LivesConfigProvider();
            _service = new LivesService(_storage, provider);
            _service.Initialize();

            _service.OnLivesChanged += HandleLivesChanged;
            _service.OnInfiniteLivesChanged += HandleInfiniteLivesChanged;

            _addLifeButton.onClick.AddListener(OnAddLife);
            _consumeLifeButton.onClick.AddListener(OnConsumeLife);
            _refillButton.onClick.AddListener(OnRefill);
            _grantInfiniteButton.onClick.AddListener(OnGrantInfinite);
            _resetButton.onClick.AddListener(OnReset);

            if (_titleText != null) _titleText.text = "Lives Service Demo";
            if (_infiniteSecondsInput != null && string.IsNullOrEmpty(_infiniteSecondsInput.text))
                _infiniteSecondsInput.text = _defaultInfiniteDurationSeconds.ToString();

            RefreshConfigDisplay();
            RefreshDisplay();
        }

        private void Update()
        {
            _service.Tick(Time.deltaTime);
            RefreshTimer();
        }

        private void OnDestroy()
        {
            if (_service == null) return;
            _service.OnLivesChanged -= HandleLivesChanged;
            _service.OnInfiniteLivesChanged -= HandleInfiniteLivesChanged;
        }

        #endregion

        #region Private/Protected Methods

        private void HandleLivesChanged(int _) => RefreshDisplay();

        private void HandleInfiniteLivesChanged() => RefreshDisplay();

        private void OnAddLife()
        {
            _service.AddLives(1);
            ShowFeedback("+1 life added");
        }

        private void OnConsumeLife()
        {
            bool ok = _service.ConsumeLife();
            ShowFeedback(ok ? "Life consumed" : "No lives available");
        }

        private void OnRefill()
        {
            _service.RefillLives();
            ShowFeedback("Lives refilled to max");
        }

        private void OnGrantInfinite()
        {
            int seconds = ReadInfiniteSeconds();
            if (seconds <= 0)
            {
                ShowFeedback("Enter a positive number of seconds");
                return;
            }
            _service.GrantInfinite(seconds);
            ShowFeedback($"Infinite lives: {seconds}s granted");
        }

        private void OnReset()
        {
            _storage.Lives = _config.DefaultMaxLives;
            _storage.MaxLives = _config.DefaultMaxLives;
            _storage.RecoveryStartUtc = 0;
            _storage.InfiniteLivesEndUtc = 0;
            _storage.Save();
            _service.Initialize();
            RefreshDisplay();
            ShowFeedback("Reset to defaults");
        }

        private int ReadInfiniteSeconds()
        {
            if (_infiniteSecondsInput == null) return _defaultInfiniteDurationSeconds;
            return int.TryParse(_infiniteSecondsInput.text, out int parsed) ? parsed : _defaultInfiniteDurationSeconds;
        }

        private void RefreshDisplay()
        {
            if (_livesText != null)
            {
                _livesText.text = _service.HasInfiniteLives
                    ? "∞"
                    : $"{_service.Lives} / {_service.MaxLives}";
            }

            if (_statusText != null)
            {
                _statusText.text = _service.HasInfiniteLives
                    ? "Infinite Lives: ACTIVE"
                    : "Infinite Lives: --";
            }
        }

        private void RefreshConfigDisplay()
        {
            if (_configText == null) return;
            _configText.text =
                $"Config\n" +
                $"  Default Max Lives: {_config.DefaultMaxLives}\n" +
                $"  Seconds To Recover: {_config.SecondsToRecover}\n" +
                $"  Notification Title: {_config.NotificationTitle}\n" +
                $"  Notification Body: {_config.NotificationBody}";
        }

        private void RefreshTimer()
        {
            if (_infiniteTimerText != null)
            {
                if (_service.HasInfiniteLives)
                {
                    TimeSpan remaining = GetInfiniteRemaining();
                    _infiniteTimerText.text = $"Infinite ends in: {remaining:mm\\:ss}";
                }
                else
                {
                    _infiniteTimerText.text = string.Empty;
                }
            }

            if (_timerText == null) return;

            if (_service.HasInfiniteLives)
            {
                _timerText.text = string.Empty;
                return;
            }

            if (_service.IsFull)
            {
                _timerText.text = "Lives full";
                return;
            }

            TimeSpan t = _service.TimeUntilNextLife;
            _timerText.text = t == TimeSpan.Zero ? string.Empty : $"Next life: {t:mm\\:ss}";
        }

        private TimeSpan GetInfiniteRemaining()
        {
            long endUtc = _storage.InfiniteLivesEndUtc;
            long nowUtc = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            long remaining = endUtc - nowUtc;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining);
        }

        private void ShowFeedback(string message)
        {
            if (_feedbackText != null)
                _feedbackText.text = message;
        }

        #endregion
    }
}
