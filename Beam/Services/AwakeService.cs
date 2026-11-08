using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Beam.Services
{
    public enum AwakeMode
    {
        Off,
        Indefinite,
        Minutes30,
        Hours1,
        Hours2
    }

    /// <summary>
    /// PowerToys Awake equivalent for Beam.
    /// Prevents Windows system and display from sleeping or locking via SetThreadExecutionState.
    /// </summary>
    public static class AwakeService
    {
        [Flags]
        private enum ExecutionState : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_USER_PRESENT = 0x00000004,
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

        private static AwakeMode _currentMode = AwakeMode.Off;
        private static DateTime _expirationTime = DateTime.MinValue;
        private static DispatcherTimer? _expirationTimer;

        public static event Action<AwakeMode, string>? StateChanged;

        public static AwakeMode CurrentMode => _currentMode;
        public static bool IsAwakeActive => _currentMode != AwakeMode.Off;

        public static string StatusDescription
        {
            get
            {
                if (_currentMode == AwakeMode.Off)
                    return "Awake: Disabled (Normal Sleep)";

                if (_currentMode == AwakeMode.Indefinite)
                    return "Awake: Active (Indefinitely keeping PC & Screen awake)";

                var rem = _expirationTime - DateTime.UtcNow;
                if (rem <= TimeSpan.Zero)
                    return "Awake: Expiring...";

                return $"Awake: Active ({rem.Hours * 60 + rem.Minutes}m {rem.Seconds}s remaining)";
            }
        }

        public static void SetMode(AwakeMode mode)
        {
            _currentMode = mode;
            _expirationTimer?.Stop();

            if (mode == AwakeMode.Off)
            {
                SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
                StateChanged?.Invoke(AwakeMode.Off, "Awake disabled • Sleep settings restored");
                return;
            }

            // Keep screen and system awake continuously
            SetThreadExecutionState(
                ExecutionState.ES_CONTINUOUS |
                ExecutionState.ES_SYSTEM_REQUIRED |
                ExecutionState.ES_DISPLAY_REQUIRED);

            TimeSpan duration = mode switch
            {
                AwakeMode.Minutes30 => TimeSpan.FromMinutes(30),
                AwakeMode.Hours1 => TimeSpan.FromHours(1),
                AwakeMode.Hours2 => TimeSpan.FromHours(2),
                _ => TimeSpan.Zero
            };

            if (duration > TimeSpan.Zero)
            {
                _expirationTime = DateTime.UtcNow + duration;
                _expirationTimer = new DispatcherTimer
                {
                    Interval = duration
                };
                _expirationTimer.Tick += (_, _) =>
                {
                    SetMode(AwakeMode.Off);
                    ToastNotification.Show("⏰ Awake session has completed • Screen sleep restored");
                };
                _expirationTimer.Start();
            }

            string desc = mode == AwakeMode.Indefinite
                ? "☕ Awake is keeping screen awake indefinitely"
                : $"☕ Awake is active for {duration.TotalMinutes} minutes";

            StateChanged?.Invoke(mode, desc);
        }
    }
}
