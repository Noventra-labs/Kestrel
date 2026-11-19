using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Kestrel.Interop;
using Kestrel.Models;
using Kestrel.Services;

namespace Kestrel
{
    public enum LauncherMode
    {
        MainSearch,
        ClipboardHistory,
        ProcessKiller,
        FileSearch,
        MemoryReduct,
        PortInspector,
        TempCleanupPreview,
        HungAppsPreview
    }

    public partial class MainWindow : Window
    {
        private List<LauncherItem> _allApps = new();
        private List<LauncherItem> _builtInCommands = new();
        private List<LauncherItem> _currentItems = new();
        private List<ActionItem> _currentActions = new();

        private LauncherMode _currentMode = LauncherMode.MainSearch;
        private string _activeCategoryFilter = "All";

        private HwndSource? _source;
        private bool _isRealClose = false;
        private bool _shownTrayHintOnce = false;
        private bool _isLoadingSettingsUi = false;
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;
        private CancellationTokenSource? _aiCts;
        private readonly List<ChatMessage> _apiConversation = new();
        private string _apiConversationModel = "";
        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _memReductCts;
        private MemoryCleanResult? _lastMemResult;
        private bool _isMemOperationRunning = false;
        private int? _pendingPortKillPid;
        private string _pendingPortKillLabel = string.Empty;
        private TempCleanupPreview? _pendingTempPreview;
        private List<HungWindowInfo>? _pendingHungPreview;
        private Forms.NotifyIcon? _notifyIcon;
        private GlobalKeyboardHook? _keyboardHook;
        private DispatcherTimer? _watchdogTimer;
        private DispatcherTimer? _autoMemReductTimer;
        private KestrelSettings _settings = new();
        private readonly string? _updatedFromVersion;
        private CancellationTokenSource? _updateCts;
        private bool _isRestartingForUpdate;

        public MainWindow(string? updatedFromVersion = null)
        {
            InitializeComponent();
            _updatedFromVersion = updatedFromVersion;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            GlobalKeyboardHook.KestrelWindowHandle = helper.Handle;

            _source = HwndSource.FromHwnd(helper.Handle);
            if (_source?.CompositionTarget != null)
            {
                _source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }
            _source?.AddHook(HwndHook);

            // Enable Windows 11 DWM Acrylic blur & hardware-accelerated rounded corners (DWMWCP_ROUND)
            DwmApi.EnableAcrylic(helper.Handle);

            // Register Zero-CPU Event-Driven Win32 Clipboard Listener (WM_CLIPBOARDUPDATE)
            ClipboardService.InitializeEventDrivenListener(helper.Handle);

            _settings = KestrelSettings.Load();
            KestrelSettings.SettingsChanged += OnSettingsChanged;

            InitializeTrayIcon();

            // Install Low-Level Global Keyboard Hook for Windows Key solo tap activation
            try
            {
                _keyboardHook = new GlobalKeyboardHook(() =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(SummonOrToggle));
                })
                {
                    EnableStartKeyTrigger = _settings.TriggerOnStartKey
                };
                App.Log("Global keyboard hook (Windows Key) installed successfully.");
            }
            catch (Exception ex)
            {
                App.Log($"Keyboard hook error: {ex}");
            }

            // Start 15s Hook & System Tray Watchdog so Windows 11 never drops the daemon
            _watchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _watchdogTimer.Tick += (_, _) =>
            {
                _keyboardHook?.EnsureHookAlive();
                if (_notifyIcon != null && !_notifyIcon.Visible)
                {
                    _notifyIcon.Visible = true;
                }
            };
            _watchdogTimer.Start();

            // Sustained-pressure Auto Mem Reduct monitor (disabled by default; evaluates every 10s, requires 30s+ pressure)
            _autoMemReductTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _autoMemReductTimer.Tick += async (_, _) =>
            {
                if (_isMemOperationRunning) return;
                bool acquiredRunFlag = false;
                try
                {
                    var stats = MemoryReductService.GetMemoryStats();
                    if (MemoryReductService.ShouldTriggerAutoClean(stats, _settings, DateTime.UtcNow, out _))
                    {
                        _isMemOperationRunning = true;
                        acquiredRunFlag = true;
                        int fgPid = DwmApi.GetWindowProcessId(GlobalKeyboardHook.LastActiveWindow);
                        var res = await MemoryReductService.ReduceMemoryAsync(
                            profile: MemoryCleanProfile.SmartReclaim,
                            promptElevationIfRequired: false,
                            explicitForegroundPid: fgPid);
                        MemoryReductService.RecordAutoCleanOutcome(res, DateTime.UtcNow);
                        _lastMemResult = res;
                        SystemCommandsService.RefreshMemoryCommandSubtitles(_builtInCommands);

                        if (res.WorkingSetTrimmedBytes >= 32L * 1024 * 1024)
                        {
                            ToastNotification.Show($"✓ Auto Smart Reclaim: {res.FormatSummary()}");
                        }
                    }
                }
                catch { }
                finally
                {
                    if (acquiredRunFlag) _isMemOperationRunning = false;
                }
            };
            _autoMemReductTimer.Start();

            SystemEvents.PowerModeChanged += OnSystemPowerOrSessionChanged;
            SystemEvents.SessionSwitch += OnSystemPowerOrSessionChanged;
        }

        private void OnSystemPowerOrSessionChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _keyboardHook?.EnsureHookAlive();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
            });
        }

        private void OnSettingsChanged(KestrelSettings updated)
        {
            _settings = updated;
            ApplyAppearanceSettings(updated);
            if (_keyboardHook != null)
            {
                _keyboardHook.EnableStartKeyTrigger = updated.TriggerOnStartKey;
            }

            QuickAiBadgeBorder.Visibility = updated.EnableAiFeatures ? Visibility.Visible : Visibility.Collapsed;
            TabWindows.Visibility = updated.EnableWindowManagement ? Visibility.Visible : Visibility.Collapsed;
            TabSystem.Visibility = updated.EnableSystemCommands ? Visibility.Visible : Visibility.Collapsed;

            if (updated.EnableFileSearch)
            {
                FileSearchService.StartBackgroundIndexing();
            }

            RebuildBuiltInCommands();
            UpdateFilter();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyAppearanceSettings(_settings);
            if (_settings.EnableFileSearch)
            {
                FileSearchService.StartBackgroundIndexing();
            }

            QuickAiBadgeBorder.Visibility = _settings.EnableAiFeatures ? Visibility.Visible : Visibility.Collapsed;
            TabWindows.Visibility = _settings.EnableWindowManagement ? Visibility.Visible : Visibility.Collapsed;
            TabSystem.Visibility = _settings.EnableSystemCommands ? Visibility.Visible : Visibility.Collapsed;

            RebuildBuiltInCommands();
            UpdateFilter();

            // Scan installed Windows apps asynchronously + attach FileSystemWatcher
            RefreshInstalledAppsAsync();
            AppScanner.StartWatchingChanges(RefreshInstalledAppsAsync);

            // Check for updates in the background without affecting startup performance
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                var info = await UpdateService.CheckForUpdatesAsync();
                if (info != null && info.IsNewer)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        UpdateBadge.Visibility = Visibility.Visible;
                        UpdateVersionText.Text = info.LatestTag;
                        TxtUpdateStatus.Text = $"Update available: {info.LatestTag}";
                        BtnCheckUpdatesText.Text = "Update Now";
                    });
                }
            });

            SearchBox.Focus();
            Keyboard.Focus(SearchBox);

            if (!string.IsNullOrWhiteSpace(_updatedFromVersion))
            {
                string ver = _updatedFromVersion;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    ToastNotification.Show($"✓ Updated to Kestrel v{ver}!");
                    ShootConfetti();
                }));
            }
        }

        private void RebuildBuiltInCommands()
        {
            _builtInCommands = SystemCommandsService.GetSystemCommands(
                openClipboardView: () => SwitchMode(LauncherMode.ClipboardHistory),
                openProcessKillerView: () => SwitchMode(LauncherMode.ProcessKiller),
                openFileSearchView: () => SwitchMode(LauncherMode.FileSearch),
                openSettingsView: OpenSettingsOverlay,
                triggerConfetti: ShootConfetti,
                openGeminiChat: prompt => OpenGeminiChatOverlay(prompt),
                openMemoryStudioView: () => SwitchMode(LauncherMode.MemoryReduct),
                executeMemoryProfile: (profile, promptElevation) => _ = RunMemoryCleanAsync(profile, promptElevation),
                openPortInspectorView: () => OpenPortInspector(string.Empty),
                openTempCleanupPreview: () => SwitchMode(LauncherMode.TempCleanupPreview),
                openHungAppsPreview: () => SwitchMode(LauncherMode.HungAppsPreview)
            );

            _builtInCommands.Insert(0, new LauncherItem
            {
                Id = "cmd_manage_api_keys",
                Name = "Manage AI API Keys",
                Subtitle = string.Empty,
                Category = "Command",
                KeepOpenOnExecute = true,
                Icon = IconGenerator.GetSquircleIcon(SquircleTheme.CalcGreen, IconGenerator.PathKey),
                Action = () =>
                {
                    OpenSettingsOverlay();
                    SwitchToSettingsTab("AI");
                }
            });
        }

        private void RefreshInstalledAppsAsync()
        {
            Task.Run(() =>
            {
                var apps = AppScanner.ScanApps();
                Dispatcher.Invoke(() =>
                {
                    _allApps = apps;
                    if (_currentMode == LauncherMode.MainSearch)
                    {
                        UpdateFilter();
                    }
                });
            });
        }

        private void InitializeTrayIcon()
        {
            try
            {
                System.Drawing.Icon? icon = null;
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string icoPath = Path.Combine(exeDir, "kestrel.ico");
                if (File.Exists(icoPath))
                {
                    icon = new System.Drawing.Icon(icoPath);
                }
                else
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                }

                icon ??= System.Drawing.SystemIcons.Application;

                var menu = new Forms.ContextMenuStrip
                {
                    Renderer = new TrayMenuRenderer(),
                    Font = TrayMenuRenderer.GetMenuFont(),
                    MinimumSize = new System.Drawing.Size(260, 0)
                };

                void ApplyDwmDarkAndRound()
                {
                    try
                    {
                        if (menu.IsHandleCreated)
                        {
                            int darkMode = 1;
                            DwmApi.DwmSetWindowAttribute(menu.Handle, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                            int round = DwmApi.DWMWCP_ROUND;
                            DwmApi.DwmSetWindowAttribute(menu.Handle, DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                        }
                    }
                    catch { }
                }

                menu.HandleCreated += (s, e) => ApplyDwmDarkAndRound();
                menu.Opening += (s, e) => ApplyDwmDarkAndRound();

                var openItem = new Forms.ToolStripMenuItem("Open Kestrel")
                {
                    ShortcutKeyDisplayString = "Win",
                    Tag = "Primary"
                };
                openItem.Click += (s, e) => Dispatcher.Invoke(Summon);

                var geminiItem = new Forms.ToolStripMenuItem("AI Assistant Chat")
                {
                    ShortcutKeyDisplayString = "Tab"
                };
                geminiItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); OpenGeminiChatOverlay(""); });

                var fileItem = new Forms.ToolStripMenuItem("Search Local Files")
                {
                    ShortcutKeyDisplayString = "Ctrl+F"
                };
                fileItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); SwitchMode(LauncherMode.FileSearch); });

                var clipItem = new Forms.ToolStripMenuItem("Clipboard History")
                {
                    ShortcutKeyDisplayString = "Ctrl+Shift+V"
                };
                clipItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); SwitchMode(LauncherMode.ClipboardHistory); });

                var procItem = new Forms.ToolStripMenuItem("Process Manager");
                procItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); SwitchMode(LauncherMode.ProcessKiller); });

                var settingsItem = new Forms.ToolStripMenuItem("Preferences && Settings")
                {
                    ShortcutKeyDisplayString = "Ctrl+,"
                };
                settingsItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); OpenSettingsOverlay(); });

                var exitItem = new Forms.ToolStripMenuItem("Exit Kestrel");
                exitItem.Click += (s, e) => Dispatcher.Invoke(() =>
                {
                    CloseForReal();
                    System.Windows.Application.Current.Shutdown();
                });

                menu.Items.Add(openItem);
                menu.Items.Add(geminiItem);
                menu.Items.Add(fileItem);
                menu.Items.Add(clipItem);
                menu.Items.Add(procItem);
                menu.Items.Add(settingsItem);
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add(exitItem);

                _notifyIcon = new Forms.NotifyIcon
                {
                    Icon = icon,
                    ContextMenuStrip = menu,
                    Text = "Kestrel Launcher — Active (Press Windows Key)",
                    Visible = true
                };

                _notifyIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == Forms.MouseButtons.Left)
                    {
                        Dispatcher.Invoke(SummonOrToggle);
                    }
                };
            }
            catch (Exception ex)
            {
                App.Log($"Tray icon error: {ex}");
            }
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (_isRealClose) return;
            if (DateTime.UtcNow < _ignoreDeactivateUntil) return;

            // Do not auto-hide if focus is inside our own process or an owned child HWND
            var helper = new WindowInteropHelper(this);
            if (DwmApi.IsForegroundOwnedByKestrelOrChild(helper.Handle))
                return;

            // Keep open while Settings or Embedded Gemini Chat is open unless Esc is pressed
            if (SettingsOverlay.Visibility == Visibility.Visible || AiOverlay.Visibility == Visibility.Visible)
                return;

            if (_settings.HideOnClickOutside)
            {
                HideAndTrim();
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isRealClose)
            {
                e.Cancel = true;
                HideAndTrim();
                return;
            }

            _watchdogTimer?.Stop();
            _autoMemReductTimer?.Stop();
            _memReductCts?.Cancel();
            _memReductCts?.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            KestrelSettings.SettingsChanged -= OnSettingsChanged;
            SystemEvents.PowerModeChanged -= OnSystemPowerOrSessionChanged;
            SystemEvents.SessionSwitch -= OnSystemPowerOrSessionChanged;
            ClipboardService.ShutdownListener();
            _notifyIcon?.Dispose();
            _keyboardHook?.Dispose();
            _source?.RemoveHook(HwndHook);
        }

        public void CloseForReal()
        {
            _isRealClose = true;
            if (_isUpdating && !_isRestartingForUpdate)
            {
                try { _updateCts?.Cancel(); } catch { }
            }
            _watchdogTimer?.Stop();
            ClipboardService.ShutdownListener();
            _notifyIcon?.Dispose();
            _keyboardHook?.Dispose();
            Close();
        }

        public void HideAndTrim(bool showFirstTimeHint = true)
        {
            if (!IsVisible) return;

            AiOverlay.Visibility = Visibility.Collapsed;
            ActionsOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.Visibility = Visibility.Collapsed;
            LauncherView.Visibility = Visibility.Visible;

            Hide();

            if (_currentMode != LauncherMode.MainSearch)
            {
                SwitchMode(LauncherMode.MainSearch, focusSearch: false);
            }
            else if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
            }

            if (showFirstTimeHint && !_shownTrayHintOnce)
            {
                _shownTrayHintOnce = true;
                ToastNotification.Show("Kestrel is active in System Tray • Press Windows Key anytime");
            }

            if (_settings.TrimMemoryOnHide)
            {
                Task.Run(DwmApi.TrimMemoryWorkingSet);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WmKestrelSummon)
            {
                Summon();
                handled = true;
            }
            else if (msg == DwmApi.WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == DwmApi.SC_KEYMENU)
            {
                // Suppress Win32/WPF system menu mode so the very first keystroke after summon is never swallowed
                handled = true;
            }
            else if (msg == DwmApi.WM_CLIPBOARDUPDATE)
            {
                // Event-driven clipboard notification (0% idle CPU)
                ClipboardService.OnClipboardUpdated();
                if (_currentMode == LauncherMode.ClipboardHistory && IsVisible)
                {
                    UpdateFilter();
                }
            }
            return IntPtr.Zero;
        }

        private void Window_Activated(object? sender, EventArgs e)
        {
            if (AiOverlay.Visibility != Visibility.Visible &&
                SettingsOverlay.Visibility != Visibility.Visible &&
                ActionsOverlay.Visibility != Visibility.Visible)
            {
                FocusSearchBox();
            }
        }

        private void FocusSearchBox()
        {
            FocusManager.SetFocusedElement(this, SearchBox);
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }

        public void Summon()
        {
            _ignoreDeactivateUntil = DateTime.UtcNow.AddMilliseconds(450);

            GlobalKeyboardHook.CaptureActiveWindow();

            Show();
            WindowState = WindowState.Normal;
            Topmost = true;

            var helper = new WindowInteropHelper(this);
            DwmApi.ForceForegroundWindow(helper.Handle);
            Activate();

            if (AiOverlay.Visibility != Visibility.Visible &&
                SettingsOverlay.Visibility != Visibility.Visible &&
                ActionsOverlay.Visibility != Visibility.Visible)
            {
                SystemCommandsService.RefreshMemoryCommandSubtitles(_builtInCommands);
                if (_currentMode == LauncherMode.MainSearch || _currentMode == LauncherMode.MemoryReduct)
                {
                    UpdateFilter();
                }

                FocusSearchBox();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (IsVisible &&
                        AiOverlay.Visibility != Visibility.Visible &&
                        SettingsOverlay.Visibility != Visibility.Visible &&
                        ActionsOverlay.Visibility != Visibility.Visible &&
                        !SearchBox.IsKeyboardFocused)
                    {
                        FocusSearchBox();
                    }
                }));
            }
        }

        public void SummonOrToggle()
        {
            var helper = new WindowInteropHelper(this);
            bool hasFocus = IsActive || DwmApi.IsForegroundOwnedByKestrelOrChild(helper.Handle);
            App.Log($"SummonOrToggle: IsVisible={IsVisible}, WindowState={WindowState}, hasFocus={hasFocus}");
            if (IsVisible && WindowState != WindowState.Minimized && hasFocus)
            {
                HideAndTrim();
            }
            else
            {
                Summon();
            }
        }

        private void OpenPortInspector(string initialFilter)
        {
            _pendingPortKillPid = null;
            _pendingPortKillLabel = string.Empty;
            SwitchMode(LauncherMode.PortInspector);
            if (!string.IsNullOrWhiteSpace(initialFilter))
            {
                SearchBox.Text = initialFilter.Trim();
                SearchBox.CaretIndex = SearchBox.Text.Length;
            }
        }

        private void SwitchMode(LauncherMode mode, bool focusSearch = true)
        {
            _currentMode = mode;
            if (mode != LauncherMode.PortInspector)
            {
                _pendingPortKillPid = null;
                _pendingPortKillLabel = string.Empty;
            }
            if (mode == LauncherMode.TempCleanupPreview)
            {
                _pendingTempPreview = null;
            }
            if (mode == LauncherMode.HungAppsPreview)
            {
                _pendingHungPreview = null;
            }

            SearchBox.Text = string.Empty;

            if (mode == LauncherMode.MainSearch)
            {
                BackButtonBorder.Visibility = Visibility.Collapsed;
                BrandLogoPath.Visibility = Visibility.Visible;
                ActiveModePill.Visibility = Visibility.Collapsed;
                FilterTabsPanel.Visibility = Visibility.Visible;
                PlaceholderText.Text = "Search apps, commands, or files...";
            }
            else if (mode == LauncherMode.ClipboardHistory)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Clipboard";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Filter clipboard history...";
            }
            else if (mode == LauncherMode.ProcessKiller)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Processes";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Filter running processes...";
            }
            else if (mode == LauncherMode.FileSearch)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Files";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Search files...";
            }
            else if (mode == LauncherMode.MemoryReduct)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Memory Reduct";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Filter apps to trim or exclude (Enter on app: Trim RAM • Ctrl+K: Exclude)...";
            }
            else if (mode == LauncherMode.PortInspector)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Port Inspector";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Filter by port number, protocol (TCP/UDP), process name, or PID...";
            }
            else if (mode == LauncherMode.TempCleanupPreview)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Temp Cleanup Preview";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Review stale temporary files before confirming deletion...";
            }
            else if (mode == LauncherMode.HungAppsPreview)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Unresponsive Apps";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Review unresponsive windows before confirming termination...";
            }

            UpdateFilter();
            if (focusSearch)
            {
                FocusSearchBox();
            }
        }

        private void BackButton_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchMode(LauncherMode.MainSearch);
        }

        private void BrandLogo_Click(object sender, MouseButtonEventArgs e)
        {
            ShootConfetti();
        }

        private void FilterTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border clicked && clicked.Tag is string category)
            {
                _activeCategoryFilter = category;

                var tabs = new[] { TabAll, TabApps, TabCommands, TabWindows, TabSystem };
                foreach (var tab in tabs)
                {
                    bool isSelected = tab == clicked;
                    tab.Background = isSelected
                        ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1E, 255, 255, 255))
                        : System.Windows.Media.Brushes.Transparent;
                    tab.BorderBrush = isSelected
                        ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x32, 255, 255, 255))
                        : System.Windows.Media.Brushes.Transparent;
                    if (tab.Child is TextBlock tb)
                    {
                        tb.Foreground = isSelected
                            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252))
                            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
                        tb.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    }
                }

                UpdateFilter();
                FocusSearchBox();
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateFilter();
        }

        private void UpdateFilter()
        {
            var query = SearchBox.Text.Trim();
            var cfg = KestrelSettings.Current;

            // Sub-view 1: Clipboard History
            if (_currentMode == LauncherMode.ClipboardHistory)
            {
                SectionHeaderText.Text = "Clipboard History";
                _currentItems = ClipboardService.GetClipboardLauncherItems(query, () =>
                {
                    SwitchMode(LauncherMode.MainSearch);
                    HideAndTrim(false);
                    ToastNotification.Show("✓ Pasted from Clipboard");
                });
                BindResults();
                return;
            }

            // Sub-view 2: Process Killer
            if (_currentMode == LauncherMode.ProcessKiller)
            {
                SectionHeaderText.Text = "Active Processes";
                _currentItems = SystemCommandsService.GetProcessKillerItems(query, UpdateFilter);
                BindResults();
                return;
            }

            // Sub-view 3: Local File Search
            if (_currentMode == LauncherMode.FileSearch)
            {
                SectionHeaderText.Text = "Local Files";
                _currentItems = FileSearchService.SearchFiles(query, 45);
                BindResults();
                return;
            }

            // Sub-view 4: Memory Reduct Studio
            if (_currentMode == LauncherMode.MemoryReduct)
            {
                UpdateMemoryStudioFilter(query);
                return;
            }

            // Sub-view 5: Port Inspector (Safe TCP/UDP view + two-step kill confirmation)
            if (_currentMode == LauncherMode.PortInspector)
            {
                UpdatePortInspectorFilter(query);
                return;
            }

            // Sub-view 6: Two-Stage Temp Cleanup Preview
            if (_currentMode == LauncherMode.TempCleanupPreview)
            {
                UpdateTempCleanupPreviewFilter(query);
                return;
            }

            // Sub-view 7: Two-Stage Unresponsive Apps Preview
            if (_currentMode == LauncherMode.HungAppsPreview)
            {
                UpdateHungAppsPreviewFilter(query);
                return;
            }

            // Main Search Mode (with Fuzzy + Acronym + Frecency Engine)
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            var list = new List<LauncherItem>();

            // 1. Instant Calculator, Percentage, Unit, Currency, Timezone Evaluator (if enabled)
            var math = CalculatorService.Evaluate(cfg.EnableCalculator ? query : "");
            if (math.IsValid)
            {
                var calcIcon = IconGenerator.GetSquircleIcon(SquircleTheme.CalcGreen, IconGenerator.PathCalculator);
                list.Add(new LauncherItem
                {
                    Id = "math:" + math.Expression,
                    Name = "= " + math.Result,
                    Subtitle = math.Expression,
                    Category = "Calculator",
                    ShortcutBadge = "Copy ↵",
                    Icon = calcIcon,
                    Action = () =>
                    {
                        System.Windows.Clipboard.SetText(math.Result);
                        ToastNotification.Show($"✓ Copied Result: {math.Result}");
                    }
                });
            }

            // 2. Instant Power Developer Tools & Telemetry Evaluator
            var dev = PowerDeveloperToolsService.EvaluateInline(query);
            bool devEnabled = dev.ItemIdPrefix == "mem:" ? cfg.EnableMemReduct : cfg.EnablePowerTools;
            if (dev.IsValid && devEnabled)
            {
                var devIcon = IconGenerator.GetSquircleIcon(dev.Theme, dev.SvgPath, asStroke: dev.UseStrokeIcon);
                bool keepOpen = dev.ActionType == "memreduct_studio" || dev.ActionType == "port_inspect";
                string safeId = dev.ItemIdPrefix == "sec:"
                    ? "sec:ephemeral"
                    : dev.ItemIdPrefix + dev.Category;

                list.Add(new LauncherItem
                {
                    Id = safeId,
                    Name = dev.Title,
                    Subtitle = dev.Subtitle,
                    Category = dev.Category,
                    ShortcutBadge = dev.Badge,
                    Icon = devIcon,
                    KeepOpenOnExecute = keepOpen,
                    Action = () => ExecuteInlineDevAction(dev)
                });
            }

            var quickItems = SystemCommandsService.GetQuickOperationItems(query, OpenFileSearchWithQuery);
            if (quickItems.Count > 0)
            {
                list.AddRange(quickItems);
            }

            var pool = new List<LauncherItem>(_builtInCommands.Count + _allApps.Count);
            pool.AddRange(_builtInCommands);
            pool.AddRange(_allApps);

            if (_activeCategoryFilter != "All")
            {
                pool = pool.Where(x => x.Category.Contains(_activeCategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            SectionHeaderText.Text = string.Empty;

            if (string.IsNullOrEmpty(query))
            {
                list.AddRange(SearchEngine.RankAndFilter(pool, "", 55));
                _currentItems = list;
                BindResults();
            }
            else
            {
                bool isExplicitFileQuery = QuickOperationsService.TryParseFileSearchQuery(query, out string explicitFileTerm);

                // Offload ranking and file searching to background Task to keep UI thread completely responsive (<5ms)
                Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return;

                    var ranked = SearchEngine.RankAndFilter(pool, query, 40);
                    if (token.IsCancellationRequested) return;

                    List<LauncherItem>? fileMatches = null;
                    if (cfg.EnableFileSearch && _activeCategoryFilter == "All")
                    {
                        if (isExplicitFileQuery && !string.IsNullOrWhiteSpace(explicitFileTerm))
                        {
                            fileMatches = FileSearchService.SearchFiles(explicitFileTerm, 12);
                        }
                        else if (query.Length >= 2)
                        {
                            fileMatches = FileSearchService.SearchFiles(query, 6);
                        }
                    }
                    if (token.IsCancellationRequested) return;

                    var webItems = SystemCommandsService.GetWebFallbackItems(query, p => OpenGeminiChatOverlay(p));
                    if (token.IsCancellationRequested) return;

                    var combined = new List<LauncherItem>(list.Count + ranked.Count + (fileMatches?.Count ?? 0) + webItems.Count);
                    combined.AddRange(list);
                    if (isExplicitFileQuery && fileMatches != null)
                    {
                        combined.AddRange(fileMatches);
                        combined.AddRange(ranked);
                    }
                    else
                    {
                        combined.AddRange(ranked);
                        if (fileMatches != null) combined.AddRange(fileMatches);
                    }
                    combined.AddRange(webItems);

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        _currentItems = combined;
                        BindResults();
                    }, DispatcherPriority.Normal);
                }, token);
            }
        }

        private void OpenFileSearchWithQuery(string initialQuery)
        {
            SwitchMode(LauncherMode.FileSearch);
            if (!string.IsNullOrWhiteSpace(initialQuery))
            {
                SearchBox.Text = initialQuery;
                SearchBox.CaretIndex = initialQuery.Length;
            }
        }

        private void ExecuteInlineDevAction(InlineEvalResult dev)
        {
            switch (dev.ActionType)
            {
                case "memreduct_studio":
                    SwitchMode(LauncherMode.MemoryReduct);
                    return;

                case "port_inspect":
                    OpenPortInspector(dev.ActionArg);
                    return;

                case "timer_start":
                {
                    var split = dev.ActionArg.Split('|', 2);
                    if (split.Length >= 1 && double.TryParse(split[0], out double sec) && sec > 0)
                    {
                        string label = split.Length > 1 ? split[1] : string.Empty;
                        PowerDeveloperToolsService.StartTimer(TimeSpan.FromSeconds(sec), label);
                        ToastNotification.Show(string.IsNullOrEmpty(label)
                            ? $"⏰ Countdown Timer Started ({TimeSpan.FromSeconds(sec):g})"
                            : $"⏰ Timer Started: {label}");
                    }
                    return;
                }

                case "timer_cancel":
                    PowerDeveloperToolsService.CancelTimer();
                    ToastNotification.Show("✓ Countdown Timer Cancelled");
                    return;

                default:
                    if (!string.IsNullOrEmpty(dev.Result))
                    {
                        System.Windows.Clipboard.SetText(dev.Result);
                        ToastNotification.Show($"✓ Copied {dev.Category}");
                    }
                    return;
            }
        }

        private async Task RunMemoryCleanAsync(MemoryCleanProfile profile, bool promptElevation)
        {
            if (_isMemOperationRunning)
            {
                StatusText.Text = "A memory operation is already running (Press Esc to cancel)...";
                return;
            }

            _isMemOperationRunning = true;
            _memReductCts?.Cancel();
            var cts = new CancellationTokenSource();
            _memReductCts = cts;

            try
            {
                int fgPid = DwmApi.GetWindowProcessId(GlobalKeyboardHook.LastActiveWindow);
                StatusText.Text = $"Running {profile}... (Press Esc to cancel)";

                var res = await MemoryReductService.ReduceMemoryAsync(
                    promptElevationIfRequired: promptElevation,
                    profile: profile,
                    explicitForegroundPid: fgPid,
                    cancellationToken: cts.Token);

                _lastMemResult = res;
                SystemCommandsService.RefreshMemoryCommandSubtitles(_builtInCommands);

                if (_currentMode == LauncherMode.MemoryReduct || _currentMode == LauncherMode.MainSearch)
                {
                    UpdateFilter();
                }

                string msg = res.FormatSummary();
                StatusText.Text = msg;
                ToastNotification.Show(res.ElevationCancelled
                    ? "⚠ Administrator permission was declined (UAC cancelled)"
                    : res.WasCancelled
                        ? $"Cancelled • {msg}"
                        : (res.WorkerTimedOut || res.TelemetryUnavailable)
                            ? $"⚠ {msg}"
                            : res.IsPartialSuccess
                                ? $"ℹ {msg}"
                                : $"✓ {msg}");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Memory operation cancelled";
                ToastNotification.Show("Memory operation cancelled");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Memory operation failed: {ex.Message}";
                ToastNotification.Show($"Memory operation failed: {ex.Message}");
            }
            finally
            {
                if (_memReductCts == cts) _memReductCts = null;
                cts.Dispose();
                _isMemOperationRunning = false;
            }
        }

        private async Task TrimProcessGroupAsync(string processName)
        {
            if (_isMemOperationRunning) return;
            _isMemOperationRunning = true;
            try
            {
                StatusText.Text = $"Trimming working set for {processName}...";
                var res = await Task.Run(() => MemoryReductService.TrimProcessGroup(processName));
                _lastMemResult = res;
                SystemCommandsService.RefreshMemoryCommandSubtitles(_builtInCommands);

                if (_currentMode == LauncherMode.MemoryReduct)
                {
                    UpdateFilter();
                }

                string summary = !string.IsNullOrEmpty(res.StatusSummary) ? res.StatusSummary : res.FormatSummary();
                StatusText.Text = summary;
                ToastNotification.Show(res.ProcessesCleaned > 0
                    ? $"✓ {processName}: {res.WorkingSetTrimmedMb:N1} MB working set trimmed ({res.ProcessesCleaned} procs)"
                    : $"ℹ {summary}");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Trim failed: {ex.Message}";
            }
            finally
            {
                _isMemOperationRunning = false;
            }
        }

        private void ToggleProcessExclusion(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;
            _settings.MemReductExcludedProcesses ??= new List<string>();
            int existingIdx = _settings.MemReductExcludedProcesses
                .FindIndex(x => string.Equals(x, processName, StringComparison.OrdinalIgnoreCase));

            if (existingIdx >= 0)
            {
                _settings.MemReductExcludedProcesses.RemoveAt(existingIdx);
                ToastNotification.Show($"✓ Included '{processName}' in Smart Reclaim");
            }
            else
            {
                _settings.MemReductExcludedProcesses.Add(processName);
                ToastNotification.Show($"✓ Excluded '{processName}' from Smart Reclaim");
            }

            _settings.Save(notify: false);
            if (_currentMode == LauncherMode.MemoryReduct)
            {
                UpdateFilter();
            }
        }

        private void UpdateMemoryStudioFilter(string query)
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            SectionHeaderText.Text = "Memory Reduct Studio (Enter on App: Trim RAM • Ctrl+K: Exclude • Esc: Cancel/Back)";

            int fgPid = DwmApi.GetWindowProcessId(GlobalKeyboardHook.LastActiveWindow);
            Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;
                var stats = MemoryReductService.GetDetailedMemoryStats();
                var groups = MemoryReductService.GetProcessGroupsSnapshot(query, fgPid);
                bool isElevated = MemoryReductService.IsElevated;
                if (token.IsCancellationRequested) return;

                var iconTelemetry = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathActivity, asStroke: true);
                var iconMem = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathMemory);
                var iconShield = IconGenerator.GetSquircleIcon(SquircleTheme.AdminGold, IconGenerator.PathShield, asStroke: true);
                var iconProc = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathCpu);
                var iconFg = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathCpu);

                var items = new List<LauncherItem>();

                string lastInfo = _lastMemResult != null ? $" • Last: {_lastMemResult.FormatSummary()}" : string.Empty;
                string standbyText = stats.HasKernelListMetrics ? $" • Standby: {stats.StandbyMb:N0} MB" : string.Empty;
                items.Add(new LauncherItem
                {
                    Id = "mem:telemetry",
                    Name = $"Physical RAM: {stats.LoadPercent}% ({stats.UsedPhysGb:F1} / {stats.TotalPhysGb:F1} GB) • Avail: {stats.AvailPhysGb:F1} GB",
                    Subtitle = $"System Cache: {stats.SystemCacheMb:N0} MB{standbyText} • Commit: {stats.CommitTotalGb:F1}/{stats.CommitLimitGb:F1} GB{lastInfo}",
                    Category = "Telemetry",
                    ShortcutBadge = "Refresh ↵",
                    Icon = iconTelemetry,
                    KeepOpenOnExecute = true,
                    Action = UpdateFilter
                });

                if (string.IsNullOrWhiteSpace(query))
                {
                    items.Add(new LauncherItem
                    {
                        Id = "mem:smart",
                        Name = "Run Smart Reclaim (Recommended Default)",
                        Subtitle = "Safely trims background apps (>=25 MB) • Protects active foreground window & Windows OS processes",
                        Category = "Memory Action",
                        ShortcutBadge = "Smart ↵",
                        Icon = iconMem,
                        KeepOpenOnExecute = true,
                        Action = () => _ = RunMemoryCleanAsync(MemoryCleanProfile.SmartReclaim, false)
                    });

                    items.Add(new LauncherItem
                    {
                        Id = "mem:trim_all",
                        Name = "Trim All User Working Sets",
                        Subtitle = "Sweeps working sets across all accessible user processes (respects user exclusions, zero prompt)",
                        Category = "Memory Action",
                        ShortcutBadge = "Trim All",
                        Icon = iconMem,
                        KeepOpenOnExecute = true,
                        Action = () => _ = RunMemoryCleanAsync(MemoryCleanProfile.WorkingSetsAll, false)
                    });

                    items.Add(new LauncherItem
                    {
                        Id = "mem:standby_low",
                        Name = "Purge Low-Priority Standby List (Opt-In)",
                        Subtitle = isElevated
                            ? "Admin Active • Flushes low-priority Windows standby cache pages"
                            : "Requires Admin UAC • Flushes low-priority standby cache pages",
                        Category = "Kernel Opt-In",
                        ShortcutBadge = "Admin 🛡",
                        Icon = iconShield,
                        KeepOpenOnExecute = true,
                        Action = () => _ = RunMemoryCleanAsync(MemoryCleanProfile.LowPriorityStandbyOnly, true)
                    });

                    items.Add(new LauncherItem
                    {
                        Id = "mem:kernel_full",
                        Name = "Deep Kernel Cache Purge (Admin Opt-In)",
                        Subtitle = isElevated
                            ? "Admin Active • System File Cache + Modified List + Standby Lists"
                            : "Requires Admin UAC • Full NT kernel cache & standby purge",
                        Category = "Kernel Opt-In",
                        ShortcutBadge = "Kernel 🛡",
                        Icon = iconShield,
                        KeepOpenOnExecute = true,
                        Action = () => _ = RunMemoryCleanAsync(MemoryCleanProfile.FullKernelAndWorkingSets, true)
                    });
                }

                foreach (var g in groups.Take(45))
                {
                    double wsMb = g.TotalWorkingSetBytes / (1024.0 * 1024.0);
                    string role = g.IsProtectedSystem
                        ? "Protected System Process (Skipped by Smart Reclaim)"
                        : g.IsExcludedByUser
                            ? "Excluded by User (Ctrl+K to Re-include)"
                            : g.IsForeground
                                ? "Active Foreground App (Protected by Smart Reclaim; Enter trims manually)"
                                : $"{g.ProcessCount} process(es) • Enter trims working set without closing";

                    string badge = g.IsProtectedSystem
                        ? "Protected"
                        : g.IsExcludedByUser
                            ? "Excluded"
                            : g.IsForeground
                                ? "Active"
                                : "Trim ↵";

                    string procName = g.ProcessName;
                    bool isProt = g.IsProtectedSystem;

                    items.Add(new LauncherItem
                    {
                        Id = "memgrp:" + procName,
                        Name = $"{procName} ({g.ProcessCount})",
                        Subtitle = role,
                        Category = $"{wsMb:N0} MB",
                        ShortcutBadge = badge,
                        Icon = g.IsForeground ? iconFg : (g.IsProtectedSystem ? iconShield : iconProc),
                        KeepOpenOnExecute = true,
                        Action = () =>
                        {
                            if (isProt)
                            {
                                ToastNotification.Show($"Protected Windows system process '{procName}' cannot be trimmed");
                                return;
                            }
                            _ = TrimProcessGroupAsync(procName);
                        }
                    });
                }

                Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _currentMode != LauncherMode.MemoryReduct) return;
                    _currentItems = items;
                    BindResults();
                }, DispatcherPriority.Normal);
            }, token);
        }

        private void UpdatePortInspectorFilter(string query)
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            int? filterPort = int.TryParse(query, out int p) && p > 0 && p <= 65535 ? p : null;
            string? textFilter = filterPort.HasValue ? null : query;
            SectionHeaderText.Text = _pendingPortKillPid.HasValue
                ? $"⚠ Confirm Process Termination for PID {_pendingPortKillPid.Value} (Enter to Kill • Esc to Cancel)"
                : "Active TCP/UDP Port Bindings (Safe Read-Only View • Enter prompts 2-step confirmation)";

            Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;
                var bindings = PowerDeveloperToolsService.GetActivePortBindings(filterPort, textFilter);
                if (token.IsCancellationRequested) return;

                var iconPort = IconGenerator.GetSquircleIcon(SquircleTheme.WebCyan, IconGenerator.PathNetwork, asStroke: true);
                var iconWarn = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathShield, asStroke: true);
                var iconBack = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathRefresh);

                var items = new List<LauncherItem>();

                if (_pendingPortKillPid.HasValue)
                {
                    int targetPid = _pendingPortKillPid.Value;
                    string targetLabel = _pendingPortKillLabel;
                    items.Add(new LauncherItem
                    {
                        Id = $"port:confirm:{targetPid}",
                        Name = $"⚠ Confirm Terminate {targetLabel} (PID {targetPid})",
                        Subtitle = "Explicit confirmation required • Press Enter to terminate process now",
                        Category = "Confirm Kill",
                        ShortcutBadge = "Confirm ↵",
                        Icon = iconWarn,
                        KeepOpenOnExecute = true,
                        Action = () =>
                        {
                            try
                            {
                                using var proc = System.Diagnostics.Process.GetProcessById(targetPid);
                                string name = proc.ProcessName;
                                proc.Kill();
                                ToastNotification.Show($"✓ Terminated {name} (PID {targetPid})");
                            }
                            catch (Exception ex)
                            {
                                ToastNotification.Show($"Could not terminate PID {targetPid}: {ex.Message}");
                            }
                            finally
                            {
                                _pendingPortKillPid = null;
                                _pendingPortKillLabel = string.Empty;
                                UpdateFilter();
                            }
                        }
                    });

                    items.Add(new LauncherItem
                    {
                        Id = "port:cancel_kill",
                        Name = "Cancel Termination",
                        Subtitle = "Keep process running and return to port list",
                        Category = "Safe Action",
                        ShortcutBadge = "Esc",
                        Icon = iconBack,
                        KeepOpenOnExecute = true,
                        Action = () =>
                        {
                            _pendingPortKillPid = null;
                            _pendingPortKillLabel = string.Empty;
                            UpdateFilter();
                        }
                    });
                }

                foreach (var b in bindings.Take(60))
                {
                    var binding = b;
                    bool isProt = MemoryReductService.IsProcessProtected(binding.ProcessName, binding.Pid);
                    items.Add(new LauncherItem
                    {
                        Id = $"port:{binding.Protocol}:{binding.LocalPort}:{binding.Pid}",
                        Name = $"{binding.Protocol} :{binding.LocalPort} → {binding.ProcessName} (PID {binding.Pid})",
                        Subtitle = $"{binding.LocalAddress}:{binding.LocalPort} [{binding.State}]" +
                                   (string.IsNullOrEmpty(binding.ExecutablePath) ? "" : $" • {binding.ExecutablePath}"),
                        Category = isProt ? "System Port" : "Port Binding",
                        ShortcutBadge = isProt ? "Protected" : "Manage ↵",
                        Icon = iconPort,
                        Path = binding.ExecutablePath,
                        KeepOpenOnExecute = true,
                        Action = () =>
                        {
                            if (isProt)
                            {
                                System.Windows.Clipboard.SetText($"{binding.Protocol} {binding.LocalAddress}:{binding.LocalPort} PID {binding.Pid} ({binding.ProcessName})");
                                ToastNotification.Show($"Protected OS process ({binding.ProcessName}) • Copied endpoint info");
                                return;
                            }

                            _pendingPortKillPid = binding.Pid;
                            _pendingPortKillLabel = $"{binding.ProcessName} ({binding.Protocol} :{binding.LocalPort})";
                            UpdateFilter();
                        }
                    });
                }

                if (items.Count == 0)
                {
                    items.Add(new LauncherItem
                    {
                        Id = "port:empty",
                        Name = "No matching TCP/UDP port bindings found",
                        Subtitle = "Press Esc to return to Main Search",
                        Category = "Port Inspector",
                        Icon = iconPort,
                        KeepOpenOnExecute = true,
                        Action = () => SwitchMode(LauncherMode.MainSearch)
                    });
                }

                Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _currentMode != LauncherMode.PortInspector) return;
                    _currentItems = items;
                    BindResults();
                }, DispatcherPriority.Normal);
            }, token);
        }

        private void UpdateTempCleanupPreviewFilter(string query)
        {
            if (_pendingTempPreview == null)
            {
                SectionHeaderText.Text = "Scanning %TEMP% and CrashDumps (>24h stale, unlocked)...";
                _currentItems = new List<LauncherItem>
                {
                    new()
                    {
                        Id = "temp:scanning",
                        Name = "Scanning temporary directories (read-only preview)...",
                        Subtitle = "No files will be deleted until you review and explicitly confirm",
                        Category = "Temp Cleanup",
                        Icon = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathTrash),
                        KeepOpenOnExecute = true
                    }
                };
                BindResults();

                Task.Run(() =>
                {
                    var preview = PowerDeveloperToolsService.ScanStaleTempFilesPreview();
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_currentMode != LauncherMode.TempCleanupPreview) return;
                        _pendingTempPreview = preview;
                        UpdateTempCleanupPreviewFilter(SearchBox.Text.Trim());
                    });
                });
                return;
            }

            var previewData = _pendingTempPreview;
            var iconTrash = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathTrash);
            var iconBack = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathRefresh);
            var iconFile = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathFolderOpen);

            var items = new List<LauncherItem>();
            double totalMb = previewData.TotalBytes / (1024.0 * 1024.0);
            SectionHeaderText.Text = previewData.Files.Count > 0
                ? $"Preview: {previewData.Files.Count} stale file(s) ({totalMb:N1} MB) • Confirm before deletion"
                : "Temp Cleanup Preview: 0 eligible stale files";

            if (previewData.Files.Count == 0)
            {
                items.Add(new LauncherItem
                {
                    Id = "temp:none",
                    Name = "✓ No stale temporary files found (>24h unlocked)",
                    Subtitle = $"Skipped {previewData.SkippedRecentOrLocked} active/recent file(s) • Press Enter or Esc to return",
                    Category = "Temp Cleanup",
                    Icon = iconBack,
                    KeepOpenOnExecute = true,
                    Action = () => SwitchMode(LauncherMode.MainSearch)
                });
            }
            else
            {
                items.Add(new LauncherItem
                {
                    Id = "temp:confirm_delete",
                    Name = $"⚠ Confirm Delete {previewData.Files.Count} Stale Temp File(s) ({totalMb:N1} MB)",
                    Subtitle = $"Older than 24h & unlocked ({previewData.SkippedRecentOrLocked} recent/locked files skipped) • Enter deletes permanently",
                    Category = "Confirm Cleanup",
                    ShortcutBadge = "Confirm ↵",
                    Icon = iconTrash,
                    KeepOpenOnExecute = true,
                    Action = () =>
                    {
                        Task.Run(() =>
                        {
                            var outcome = PowerDeveloperToolsService.ExecuteTempFilesCleanup(previewData);
                            double freedMb = outcome.FreedBytes / (1024.0 * 1024.0);
                            Dispatcher.Invoke(() =>
                            {
                                _pendingTempPreview = null;
                                ToastNotification.Show($"✓ Temp Cleanup: Freed {freedMb:N1} MB ({outcome.DeletedCount} deleted, {outcome.FailedCount} skipped)");
                                SwitchMode(LauncherMode.MainSearch);
                            });
                        });
                    }
                });

                items.Add(new LauncherItem
                {
                    Id = "temp:cancel",
                    Name = "Cancel Cleanup (Keep All Files)",
                    Subtitle = "Return to Main Search without deleting any files",
                    Category = "Safe Action",
                    ShortcutBadge = "Esc",
                    Icon = iconBack,
                    KeepOpenOnExecute = true,
                    Action = () => SwitchMode(LauncherMode.MainSearch)
                });

                IEnumerable<TempFileCandidate> files = previewData.Files;
                if (!string.IsNullOrWhiteSpace(query))
                {
                    files = files.Where(f => f.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var f in files.OrderByDescending(x => x.SizeBytes).Take(50))
                {
                    double kb = f.SizeBytes / 1024.0;
                    string sizeText = kb >= 1024 ? $"{kb / 1024.0:N1} MB" : $"{kb:N0} KB";
                    items.Add(new LauncherItem
                    {
                        Id = "temp:preview:" + f.FullPath,
                        Name = Path.GetFileName(f.FullPath),
                        Subtitle = $"{f.FullPath} • Modified {f.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                        Category = sizeText,
                        Icon = iconFile,
                        Path = f.FullPath,
                        KeepOpenOnExecute = true
                    });
                }
            }

            _currentItems = items;
            BindResults();
        }

        private void UpdateHungAppsPreviewFilter(string query)
        {
            if (_pendingHungPreview == null)
            {
                SectionHeaderText.Text = "Scanning top-level windows for unresponsive apps...";
                _currentItems = new List<LauncherItem>
                {
                    new()
                    {
                        Id = "hung:scanning",
                        Name = "Scanning for frozen / Not Responding windows...",
                        Subtitle = "No processes will be terminated until you review and confirm",
                        Category = "Process Health",
                        Icon = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathCpu),
                        KeepOpenOnExecute = true
                    }
                };
                BindResults();

                Task.Run(() =>
                {
                    var hung = PowerDeveloperToolsService.ScanUnresponsiveWindows();
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_currentMode != LauncherMode.HungAppsPreview) return;
                        _pendingHungPreview = hung;
                        UpdateHungAppsPreviewFilter(SearchBox.Text.Trim());
                    });
                });
                return;
            }

            var list = _pendingHungPreview;
            var iconProc = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathCpu);
            var iconBack = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathRefresh);

            var items = new List<LauncherItem>();
            if (list.Count == 0)
            {
                SectionHeaderText.Text = "Process Health: All applications are responsive";
                items.Add(new LauncherItem
                {
                    Id = "hung:none",
                    Name = "✓ All running applications are responsive (0 hung windows)",
                    Subtitle = "Press Enter or Esc to return to Main Search",
                    Category = "Process Health",
                    Icon = iconBack,
                    KeepOpenOnExecute = true,
                    Action = () => SwitchMode(LauncherMode.MainSearch)
                });
            }
            else
            {
                SectionHeaderText.Text = $"Preview: {list.Count} unresponsive window(s) detected";
                items.Add(new LauncherItem
                {
                    Id = "hung:confirm_all",
                    Name = $"⚠ Confirm Terminate All {list.Count} Unresponsive App(s)",
                    Subtitle = "Press Enter to terminate all listed hung processes • Press Esc to cancel",
                    Category = "Confirm Kill",
                    ShortcutBadge = "Confirm ↵",
                    Icon = iconProc,
                    KeepOpenOnExecute = true,
                    Action = () =>
                    {
                        int killed = 0;
                        foreach (var h in list)
                        {
                            try
                            {
                                using var proc = System.Diagnostics.Process.GetProcessById(h.Pid);
                                proc.Kill();
                                killed++;
                            }
                            catch { }
                        }
                        ToastNotification.Show($"✓ Terminated {killed}/{list.Count} unresponsive app(s)");
                        _pendingHungPreview = null;
                        UpdateHungAppsPreviewFilter(string.Empty);
                    }
                });

                items.Add(new LauncherItem
                {
                    Id = "hung:cancel",
                    Name = "Cancel (Leave Processes Running)",
                    Subtitle = "Return to Main Search",
                    Category = "Safe Action",
                    ShortcutBadge = "Esc",
                    Icon = iconBack,
                    KeepOpenOnExecute = true,
                    Action = () => SwitchMode(LauncherMode.MainSearch)
                });

                foreach (var h in list)
                {
                    var hungItem = h;
                    if (!string.IsNullOrWhiteSpace(query) &&
                        !hungItem.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !hungItem.WindowTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    items.Add(new LauncherItem
                    {
                        Id = "hung:pid:" + hungItem.Pid,
                        Name = $"{hungItem.ProcessName} (PID {hungItem.Pid})",
                        Subtitle = $"Unresponsive window: \"{hungItem.WindowTitle}\" • Enter terminates only this app",
                        Category = "Not Responding",
                        ShortcutBadge = "Kill ↵",
                        Icon = iconProc,
                        KeepOpenOnExecute = true,
                        Action = () =>
                        {
                            try
                            {
                                using var proc = System.Diagnostics.Process.GetProcessById(hungItem.Pid);
                                proc.Kill();
                                ToastNotification.Show($"✓ Terminated {hungItem.ProcessName} (PID {hungItem.Pid})");
                            }
                            catch (Exception ex)
                            {
                                ToastNotification.Show($"Could not terminate {hungItem.ProcessName}: {ex.Message}");
                            }
                            _pendingHungPreview = null;
                            UpdateHungAppsPreviewFilter(string.Empty);
                        }
                    });
                }
            }

            _currentItems = items;
            BindResults();
        }

        private void BindResults()
        {
            ResultsList.ItemsSource = _currentItems;
            if (_currentItems.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
                UpdateSelectedItemState();
            }
            else
            {
                StatusText.Text = "No matches";
                PrimaryActionLabel.Text = "Search";
            }
        }

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedItemState();
        }

        private void UpdateSelectedItemState()
        {
            if (ResultsList.SelectedItem is LauncherItem item)
            {
                StatusText.Text = $"{item.Name} • {item.Category}";
                PrimaryActionLabel.Text = item.Category switch
                {
                    "Calculator" or "Hasher" or "Timestamp" or "Timestamp Converter" or "Base64 Encoder" or "Base64 Decoder" or "GUID Generator" or "ID Generator" or "Password Generator" or "URL Encoder" or "URL Decoder" or "Color Converter" or "JWT (Unverified)" => "Copy Result",
                    "Clipboard" or "Pinned Clip" => "Paste to App",
                    "Window Management" => "Snap Window",
                    "PowerToys" => item.Name.Contains("Color") ? "Pick Color" : (item.Name.Contains("Text") ? "Snip OCR" : "Run"),
                    "System Tool" => item.Id == "cmd:memreduct_studio" ? "Open Studio" : (item.Name.Contains("Mem") || item.Name.Contains("Reclaim") ? "Free RAM" : "Run"),
                    "Port Binding" or "Port Inspector" => "Inspect",
                    "Confirm Kill" => "Confirm Kill",
                    "Confirm Cleanup" => "Confirm Delete",
                    "AI Command" or "Gemini AI" => "Open Gemini",
                    _ when item.Id.StartsWith("proc:") => "Kill Process",
                    _ when item.Id.StartsWith("memgrp:") => "Trim App RAM",
                    _ => "Open"
                };
            }
        }

        private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != ResultsList)
            {
                // Ignore scrollbar clicks and thumb drags
                if (dep is System.Windows.Controls.Primitives.ScrollBar or Thumb or RepeatButton)
                    return;

                if (dep is ListBoxItem lbi && lbi.DataContext is LauncherItem clickedItem)
                {
                    ResultsList.SelectedItem = clickedItem;
                    ExecuteItem(clickedItem);
                    e.Handled = true;
                    return;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }
        }

        private void ActionsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != ActionsList)
            {
                if (dep is System.Windows.Controls.Primitives.ScrollBar or Thumb or RepeatButton)
                    return;

                if (dep is ListBoxItem lbi && lbi.DataContext is ActionItem actionItem)
                {
                    ActionsList.SelectedItem = actionItem;
                    ExecuteSelectedAction();
                    e.Handled = true;
                    return;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Never allow Alt key events to put WPF into MenuMode and swallow the first typed letter
            if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
            {
                e.Handled = true;
                return;
            }

            // 1. Settings Overlay Open
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseSettingsOverlay();
                }
                return;
            }

            // 2. Embedded Gemini Chat Overlay Open
            if (AiOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseAiOverlay();
                }
                return;
            }

            // 3. Actions Overlay Open
            if (ActionsOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape || (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
                {
                    e.Handled = true;
                    CloseActionsOverlay();
                }
                else if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateActionsList(1);
                }
                else if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateActionsList(-1);
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    ExecuteSelectedAction();
                }
                return;
            }

            // 4. Global Shortcuts inside Launcher
            if (e.Key == Key.OemComma && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenSettingsOverlay();
                return;
            }

            if (_settings.EnableClipboardHistory &&
                e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                e.Handled = true;
                SwitchMode(LauncherMode.ClipboardHistory);
                return;
            }

            if (_settings.EnableFileSearch &&
                e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SwitchMode(LauncherMode.FileSearch);
                return;
            }

            if (_settings.EnableMemReduct &&
                e.Key == Key.M && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SwitchMode(LauncherMode.MemoryReduct);
                return;
            }

            if (_settings.EnablePowerTools &&
                e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenPortInspector(string.Empty);
                return;
            }

            // 5. Standard Navigation
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_memReductCts != null && !_memReductCts.IsCancellationRequested)
                {
                    _memReductCts.Cancel();
                    StatusText.Text = "Cancelling memory operation...";
                    return;
                }
                if (_currentMode == LauncherMode.PortInspector && _pendingPortKillPid.HasValue)
                {
                    _pendingPortKillPid = null;
                    _pendingPortKillLabel = string.Empty;
                    UpdateFilter();
                    return;
                }
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                else if (_currentMode != LauncherMode.MainSearch)
                {
                    SwitchMode(LauncherMode.MainSearch);
                }
                else
                {
                    HideAndTrim();
                }
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                NavigateResultsList(1);
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                NavigateResultsList(-1);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    RunSelectedItemAsAdmin();
                }
                else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
                         ResultsList.SelectedItem is LauncherItem calcItem &&
                         calcItem.Category == "Calculator")
                {
                    string val = calcItem.Name.StartsWith("= ") ? calcItem.Name[2..] : calcItem.Name;
                    HideAndTrim(false);
                    DwmApi.PasteTextToPreviousWindow(val);
                    ToastNotification.Show($"✓ Pasted {val}");
                }
                else
                {
                    ExecutePrimaryAction();
                }
            }
            else if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenActionsOverlay();
            }
            else if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                RevealSelectedItemInExplorer();
            }
            else if (e.Key == Key.Tab && _settings.EnableAiFeatures)
            {
                e.Handled = true;
                OpenGeminiChatOverlay(SearchBox.Text.Trim());
            }
            else if (!SearchBox.IsKeyboardFocused)
            {
                FocusSearchBox();
            }
        }

        private void NavigateResultsList(int direction)
        {
            if (ResultsList.Items.Count == 0) return;
            int count = ResultsList.Items.Count;
            int newIndex = (ResultsList.SelectedIndex + direction + count) % count;
            ResultsList.SelectedIndex = newIndex;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            UpdateSelectedItemState();
        }

        private void NavigateActionsList(int direction)
        {
            if (ActionsList.Items.Count == 0) return;
            int count = ActionsList.Items.Count;
            int newIndex = (ActionsList.SelectedIndex + direction + count) % count;
            ActionsList.SelectedIndex = newIndex;
            ActionsList.ScrollIntoView(ActionsList.SelectedItem);
        }

        private void ExecutePrimaryAction()
        {
            if (ResultsList.SelectedItem is LauncherItem item)
            {
                ExecuteItem(item);
            }
        }

        private void ExecuteItem(LauncherItem item)
        {
            SearchEngine.RecordUsage(item.Id);
            bool keepOpen = item.KeepOpenOnExecute;

            if (!keepOpen)
            {
                // Yield focus and hide in 0ms BEFORE snapping windows, pasting, or launching external apps
                HideAndTrim(false);
            }

            item.Action?.Invoke();
        }

        private void RunSelectedItemAsAdmin()
        {
            if (ResultsList.SelectedItem is LauncherItem item && !string.IsNullOrEmpty(item.Path))
            {
                SearchEngine.RecordUsage(item.Id);
                HideAndTrim(false);
                AppScanner.LaunchAsAdmin(item.Path);
            }
        }

        private void RevealSelectedItemInExplorer()
        {
            if (ResultsList.SelectedItem is LauncherItem item && !string.IsNullOrEmpty(item.Path))
            {
                HideAndTrim(false);
                AppScanner.RevealInExplorer(item.Path);
            }
        }

        private void CopySelectedItemName()
        {
            if (ResultsList.SelectedItem is LauncherItem item)
            {
                System.Windows.Clipboard.SetText(item.Name);
                HideAndTrim(false);
                ToastNotification.Show($"✓ Copied \"{item.Name}\"");
            }
        }

        private void PrimaryAction_Click(object sender, MouseButtonEventArgs e) => ExecutePrimaryAction();
        private void ActionsButton_Click(object sender, MouseButtonEventArgs e) => OpenActionsOverlay();
        private void SettingsButton_Click(object sender, MouseButtonEventArgs e) => OpenSettingsOverlay();
        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ExecutePrimaryAction();

        #region Actions Panel (Ctrl + K)

        private void OpenActionsOverlay()
        {
            if (ResultsList.SelectedItem is not LauncherItem item) return;

            // Lucide Action Icons
            var iconPlay = IconGenerator.GetSquircleIcon(SquircleTheme.BrandCoral, IconGenerator.PathPlay);
            var iconAdmin = IconGenerator.GetSquircleIcon(SquircleTheme.AdminGold, IconGenerator.PathShield, asStroke: true);
            var iconFolder = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathFolderOpen);
            var iconCopy = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathCopy);
            var iconAi = IconGenerator.GetSquircleIcon(SquircleTheme.BrandCoral, IconGenerator.PathSparkles);
            var iconGear = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathGear);

            var actions = new List<ActionItem>
            {
                new()
                {
                    Title = $"Open \"{item.Name}\"",
                    ShortcutHint = "↵",
                    Icon = iconPlay,
                    IsPrimary = true,
                    Execute = () => ExecuteItem(item)
                }
            };

            // Administrator elevation action for any application, shortcut, or shell command
            if (!string.IsNullOrEmpty(item.Path) || item.Category == "Application" || item.IsAdminCapable)
            {
                actions.Add(new ActionItem
                {
                    Title = $"Run \"{item.Name}\" as Administrator",
                    ShortcutHint = "Ctrl+Shift+↵",
                    Icon = iconAdmin,
                    Execute = RunSelectedItemAsAdmin
                });
            }

            if (item.Category == "Calculator")
            {
                string val = item.Name.StartsWith("= ") ? item.Name[2..] : item.Name;
                actions.Add(new ActionItem
                {
                    Title = "Paste Result to Active Window",
                    ShortcutHint = "Shift+↵",
                    Icon = iconCopy,
                    Execute = () =>
                    {
                        HideAndTrim(false);
                        DwmApi.PasteTextToPreviousWindow(val);
                        ToastNotification.Show($"✓ Pasted {val}");
                    }
                });
            }

            if (item.Id.StartsWith("memgrp:", StringComparison.OrdinalIgnoreCase))
            {
                string procName = item.Id["memgrp:".Length..];
                bool isProtected = MemoryReductService.IsProcessProtected(procName, 100);
                if (!isProtected)
                {
                    bool isExcluded = MemoryReductService.IsProcessExcludedByUser(procName);
                    actions.Add(new ActionItem
                    {
                        Title = isExcluded
                            ? $"Include \"{procName}\" in Smart Reclaim"
                            : $"Exclude \"{procName}\" from Smart Reclaim",
                        ShortcutHint = "",
                        Icon = iconAdmin,
                        Execute = () => ToggleProcessExclusion(procName)
                    });
                }
            }

            if (item.Id.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new ActionItem
                {
                    Title = "Copy Port & PID Diagnostic Info",
                    ShortcutHint = "",
                    Icon = iconCopy,
                    Execute = () =>
                    {
                        System.Windows.Clipboard.SetText($"{item.Name} • {item.Subtitle}");
                        ToastNotification.Show("✓ Copied Port Info to Clipboard");
                    }
                });
            }

            if (!string.IsNullOrEmpty(item.Path))
            {
                actions.Add(new ActionItem
                {
                    Title = "Reveal in File Explorer",
                    ShortcutHint = "Ctrl+R",
                    Icon = iconFolder,
                    Execute = RevealSelectedItemInExplorer
                });
            }

            actions.Add(new ActionItem
            {
                Title = "Copy Name",
                ShortcutHint = "",
                Icon = iconCopy,
                Execute = CopySelectedItemName
            });

            if (_settings.EnableAiFeatures)
            {
                actions.Add(new ActionItem
                {
                    Title = "Open AI Assistant",
                    ShortcutHint = "Tab",
                    Icon = iconAi,
                    Execute = () => OpenGeminiChatOverlay(SearchBox.Text.Trim())
                });
            }

            if (_settings.EnableFileSearch)
            {
                actions.Add(new ActionItem
                {
                    Title = "Search Files",
                    ShortcutHint = "Ctrl+F",
                    Icon = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathSearch),
                    Execute = () => SwitchMode(LauncherMode.FileSearch)
                });
            }

            if (_settings.EnableClipboardHistory)
            {
                actions.Add(new ActionItem
                {
                    Title = "Clipboard History",
                    ShortcutHint = "Ctrl+Shift+V",
                    Icon = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathClipboard),
                    Execute = () => SwitchMode(LauncherMode.ClipboardHistory)
                });
            }

            actions.Add(new ActionItem
            {
                Title = "Kestrel Preferences",
                ShortcutHint = "Ctrl+,",
                Icon = iconGear,
                Execute = OpenSettingsOverlay
            });

            _currentActions = actions;
            ActionsList.ItemsSource = _currentActions;
            ActionsList.SelectedIndex = 0;
            ActionsOverlay.Visibility = Visibility.Visible;
            ActionsList.Focus();
        }

        private void CloseActionsOverlay()
        {
            ActionsOverlay.Visibility = Visibility.Collapsed;
            FocusSearchBox();
        }

        private void ActionsOverlayBackdrop_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == ActionsOverlay)
            {
                CloseActionsOverlay();
            }
        }

        private void ExecuteSelectedAction()
        {
            if (ActionsList.SelectedItem is ActionItem action)
            {
                CloseActionsOverlay();
                action.Execute?.Invoke();
            }
        }

        private void ActionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelectedAction();

        #endregion

        #region Redesigned 4-Tab Preferences & Settings Center

        private string _selectedSettingsProvider = "gemini";

        private void OpenSettingsOverlay()
        {
            AiOverlay.Visibility = Visibility.Collapsed;
            LauncherView.Visibility = Visibility.Collapsed;

            _isLoadingSettingsUi = true;
            _settings = KestrelSettings.Load();

            // Tab 1: General & Hotkeys (Windows Key activation only)
            ChkStartKeyTrigger.IsChecked = _settings.TriggerOnStartKey;
            ChkLaunchStartup.IsChecked = _settings.LaunchOnStartup;
            ChkHideOutside.IsChecked = _settings.HideOnClickOutside;
            ChkToastNotifications.IsChecked = _settings.EnableToastNotifications;
            ChkTrimMemory.IsChecked = _settings.TrimMemoryOnHide;

            // Tab 2: Appearance & Customization
            double opacity = ThemeAppearance.ClampOpacity(_settings.BackgroundOpacity);
            SliderBgOpacity.Value = opacity;
            TxtOpacityValue.Text = $"{Math.Round(opacity * 100):0}%";
            TxtThemeHex.Text = ThemeAppearance.NormalizeHexColor(_settings.ThemeAccentColor);
            RefreshThemeSwatchesUi();

            // Tab 2: Modules & Extensions
            ChkEnableAi.IsChecked = _settings.EnableAiFeatures;
            ChkEnableFileSearch.IsChecked = _settings.EnableFileSearch;
            ChkEnableClipboard.IsChecked = _settings.EnableClipboardHistory;
            ChkEnableWindowSnap.IsChecked = _settings.EnableWindowManagement;
            ChkEnableCalculator.IsChecked = _settings.EnableCalculator;
            ChkEnableProcessKiller.IsChecked = _settings.EnableProcessKiller;
            ChkEnableSystemCmds.IsChecked = _settings.EnableSystemCommands;
            ChkEnableWebFallbacks.IsChecked = _settings.EnableWebFallbacks;
            ChkEnableMemReduct.IsChecked = _settings.EnableMemReduct;
            ChkAutoMemReduct.IsChecked = _settings.AutoMemReductEnabled;
            ChkEnablePowerTools.IsChecked = _settings.EnablePowerTools;

            // Tab 3: AI Configuration
            TxtCloudUrl.Text = _settings.CloudBackendUrl;
            TxtAiModel.Text = _settings.DefaultAiModel;

            _selectedSettingsProvider = string.IsNullOrWhiteSpace(_settings.ActiveAiProvider)
                ? "gemini"
                : _settings.ActiveAiProvider;
            TxtApiKeyOverride.Text = _settings.GetApiKeyForProvider(_selectedSettingsProvider);
            RefreshProviderPillsUi();

            // Tab 4: Telemetry
            RefreshIndexTelemetryText();

            _isLoadingSettingsUi = false;
            SettingsSavedNotice.Text = "Kestrel v2.4.0 • Noventra-Labs";
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void RefreshProviderPillsUi()
        {
            var providers = new[]
            {
                (Id: "gemini", Border: ProviderPillGemini, Txt: ProviderPillGeminiTxt, Label: "Gemini", Title: "Gemini API Key"),
                (Id: "openai", Border: ProviderPillOpenAi, Txt: ProviderPillOpenAiTxt, Label: "OpenAI", Title: "OpenAI API Key"),
                (Id: "groq", Border: ProviderPillGroq, Txt: ProviderPillGroqTxt, Label: "Groq", Title: "Groq API Key"),
                (Id: "anthropic", Border: ProviderPillAnthropic, Txt: ProviderPillAnthropicTxt, Label: "Claude", Title: "Claude API Key")
            };

            foreach (var p in providers)
            {
                bool isSelected = string.Equals(_selectedSettingsProvider, p.Id, StringComparison.OrdinalIgnoreCase);
                bool hasKey = !string.IsNullOrWhiteSpace(_settings.GetApiKeyForProvider(p.Id));

                p.Txt.Text = hasKey ? $"{p.Label} •" : p.Label;
                p.Border.Background = isSelected
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 255, 255, 255))
                    : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x0C, 255, 255, 255));
                p.Border.BorderBrush = isSelected
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 99, 99))
                    : hasKey
                        ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 16, 185, 129))
                        : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 255, 255, 255));
                p.Txt.Foreground = isSelected
                    ? System.Windows.Media.Brushes.White
                    : hasKey
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 231, 183))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
                p.Txt.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;

                if (isSelected)
                {
                    TxtActiveProviderKeyLabel.Text = p.Title;
                }
            }
        }

        private void RefreshIndexTelemetryText()
        {
            SetTabModulesTxt.Text = "Extensions";
            TxtIndexStats.Text =
                $"Applications Indexed: {_allApps.Count}\n" +
                $"Local Files Indexed: {FileSearchService.GetIndexedCount()}\n" +
                $"Clipboard Entries: {ClipboardService.GetEntryCount()}\n" +
                $"Learned Ranking Items: {SearchEngine.GetTrackedCount()}";
        }

        private void SettingsTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border clicked || clicked.Tag is not string tabName) return;
            SwitchToSettingsTab(tabName);
        }

        private void SwitchToSettingsTab(string tabName)
        {
            var tabBorders = new[] { SetTabGeneral, SetTabAppearance, SetTabModules, SetTabAi, SetTabData };
            var tabTexts = new[] { SetTabGeneralTxt, SetTabAppearanceTxt, SetTabModulesTxt, SetTabAiTxt, SetTabDataTxt };

            for (int i = 0; i < tabBorders.Length; i++)
            {
                bool active = string.Equals(tabBorders[i].Tag as string, tabName, StringComparison.OrdinalIgnoreCase);
                tabBorders[i].Background = active
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 255, 255, 255))
                    : System.Windows.Media.Brushes.Transparent;
                tabBorders[i].BorderBrush = active
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x32, 255, 255, 255))
                    : System.Windows.Media.Brushes.Transparent;
                tabTexts[i].Foreground = active
                    ? System.Windows.Media.Brushes.White
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
                tabTexts[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }

            SettingsTabGeneralPanel.Visibility = tabName == "General" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabAppearancePanel.Visibility = tabName == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabModulesPanel.Visibility = tabName == "Modules" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabAiPanel.Visibility = tabName == "AI" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabDataPanel.Visibility = tabName == "Data" ? Visibility.Visible : Visibility.Collapsed;

            if (tabName == "Data")
            {
                RefreshIndexTelemetryText();
            }
        }

        private void ApplyAppearanceSettings(KestrelSettings settings)
        {
            string hex = ThemeAppearance.NormalizeHexColor(settings.ThemeAccentColor);
            var (r, g, b) = ThemeAppearance.ParseRgb(hex);
            var accent = System.Windows.Media.Color.FromRgb(r, g, b);
            var soft = System.Windows.Media.Color.FromRgb(
                (byte)Math.Min(255, r + 45),
                (byte)Math.Min(255, g + 45),
                (byte)Math.Min(255, b + 45));

            Resources["BrandRed"] = new SolidColorBrush(accent);
            Resources["BrandAccentTint"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, r, g, b));
            Resources["BrandAccentBorder"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, r, g, b));
            Resources["BrandAccentSelection"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x44, r, g, b));
            Resources["BrandAccentSoft"] = new SolidColorBrush(soft);

            var (a1, a2, a3) = ThemeAppearance.ComputeShellAlphas(settings.BackgroundOpacity);
            var shellBrush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1)
            };
            shellBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a1, 9, 12, 18), 0));
            shellBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a2, 7, 9, 14), 0.55));
            shellBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a3, 5, 7, 11), 1));
            MainShellBorder.Background = shellBrush;
        }

        private void RefreshThemeSwatchesUi()
        {
            if (ThemeSwatchesPanel == null) return;
            string activeHex = ThemeAppearance.NormalizeHexColor(_settings.ThemeAccentColor);
            foreach (var child in ThemeSwatchesPanel.Children)
            {
                if (child is Border swatch && swatch.Tag is string tagHex)
                {
                    bool selected = string.Equals(
                        ThemeAppearance.NormalizeHexColor(tagHex),
                        activeHex,
                        StringComparison.OrdinalIgnoreCase);
                    swatch.BorderBrush = selected
                        ? System.Windows.Media.Brushes.White
                        : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 255, 255, 255));
                }
            }
        }

        private void SliderBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _isLoadingSettingsUi || TxtOpacityValue == null || MainShellBorder == null)
                return;

            double opacity = ThemeAppearance.ClampOpacity(e.NewValue);
            TxtOpacityValue.Text = $"{Math.Round(opacity * 100):0}%";
            _settings.BackgroundOpacity = opacity;
            ApplyAppearanceSettings(_settings);
            _settings.Save(notify: false);
            SettingsSavedNotice.Text = "Saved";
        }

        private void ThemeSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border swatch || swatch.Tag is not string hex) return;
            string normalized = ThemeAppearance.NormalizeHexColor(hex);
            _settings.ThemeAccentColor = normalized;
            TxtThemeHex.Text = normalized;
            RefreshThemeSwatchesUi();
            ApplyAppearanceSettings(_settings);
            _settings.Save(notify: true);
            SettingsSavedNotice.Text = "Saved";
        }

        private void ApplyCustomThemeHex_Click(object sender, MouseButtonEventArgs e)
        {
            if (!ThemeAppearance.TryNormalizeHexColor(TxtThemeHex.Text, out string normalized))
            {
                ToastNotification.Show("Enter a valid hex color (e.g. #3B82F6)");
                return;
            }

            _settings.ThemeAccentColor = normalized;
            TxtThemeHex.Text = normalized;
            RefreshThemeSwatchesUi();
            ApplyAppearanceSettings(_settings);
            _settings.Save(notify: true);
            SettingsSavedNotice.Text = "Saved";
        }

        private void ResetAppearance_Click(object sender, MouseButtonEventArgs e)
        {
            _isLoadingSettingsUi = true;
            _settings.BackgroundOpacity = ThemeAppearance.DefaultOpacity;
            _settings.ThemeAccentColor = ThemeAppearance.DefaultAccentHex;
            SliderBgOpacity.Value = ThemeAppearance.DefaultOpacity;
            TxtOpacityValue.Text = $"{Math.Round(ThemeAppearance.DefaultOpacity * 100):0}%";
            TxtThemeHex.Text = ThemeAppearance.DefaultAccentHex;
            _isLoadingSettingsUi = false;

            RefreshThemeSwatchesUi();
            ApplyAppearanceSettings(_settings);
            _settings.Save(notify: true);
            SettingsSavedNotice.Text = "Appearance reset to default";
        }

        private void SettingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettingsUi) return;

            ApplyUiToSettings();
            _settings.Save(notify: true);
            RefreshIndexTelemetryText();
            SettingsSavedNotice.Text = "Saved";
        }

        private void ApplyUiToSettings()
        {
            _settings.TriggerOnStartKey = ChkStartKeyTrigger.IsChecked == true;
            _settings.LaunchOnStartup = ChkLaunchStartup.IsChecked == true;
            _settings.HideOnClickOutside = ChkHideOutside.IsChecked == true;
            _settings.EnableToastNotifications = ChkToastNotifications.IsChecked == true;
            _settings.TrimMemoryOnHide = ChkTrimMemory.IsChecked == true;

            _settings.BackgroundOpacity = ThemeAppearance.ClampOpacity(SliderBgOpacity.Value);
            if (ThemeAppearance.TryNormalizeHexColor(TxtThemeHex.Text, out string customHex))
            {
                _settings.ThemeAccentColor = customHex;
            }

            _settings.EnableAiFeatures = ChkEnableAi.IsChecked == true;
            _settings.EnableFileSearch = ChkEnableFileSearch.IsChecked == true;
            _settings.EnableClipboardHistory = ChkEnableClipboard.IsChecked == true;
            _settings.EnableWindowManagement = ChkEnableWindowSnap.IsChecked == true;
            _settings.EnableCalculator = ChkEnableCalculator.IsChecked == true;
            _settings.EnableProcessKiller = ChkEnableProcessKiller.IsChecked == true;
            _settings.EnableSystemCommands = ChkEnableSystemCmds.IsChecked == true;
            _settings.EnableWebFallbacks = ChkEnableWebFallbacks.IsChecked == true;
            _settings.EnableMemReduct = ChkEnableMemReduct.IsChecked == true;
            _settings.AutoMemReductEnabled = ChkAutoMemReduct.IsChecked == true;
            _settings.EnablePowerTools = ChkEnablePowerTools.IsChecked == true;

            _settings.CloudBackendUrl = string.IsNullOrWhiteSpace(TxtCloudUrl.Text) ? "http://localhost:8080" : TxtCloudUrl.Text.Trim();
            _settings.DefaultAiModel = TxtAiModel.Text.Trim();
            _settings.ActiveAiProvider = _selectedSettingsProvider;
            _settings.SetApiKeyForProvider(_selectedSettingsProvider, TxtApiKeyOverride.Text.Trim());
        }

        private void ProviderPill_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border pill || pill.Tag is not string providerId) return;

            if (!_isLoadingSettingsUi)
            {
                _settings.SetApiKeyForProvider(_selectedSettingsProvider, TxtApiKeyOverride.Text.Trim());
            }

            _isLoadingSettingsUi = true;
            _selectedSettingsProvider = providerId;
            _settings.ActiveAiProvider = providerId;

            TxtApiKeyOverride.Text = _settings.GetApiKeyForProvider(providerId);
            _settings.UserApiKeyOverride = TxtApiKeyOverride.Text;
            _isLoadingSettingsUi = false;

            _settings.Save(notify: true);
            RefreshProviderPillsUi();
            TxtSaveLocalKeyBtn.Text = "Save";
            TxtSaveVaultBtn.Text = "Sync Vault";
            SettingsSavedNotice.Text = $"Active provider: {providerId.ToUpperInvariant()}";
        }

        private void TxtApiKeyOverride_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            _settings.SetApiKeyForProvider(_selectedSettingsProvider, TxtApiKeyOverride.Text.Trim());
            RefreshProviderPillsUi();
        }

        private void SaveKeyLocally_Click(object sender, MouseButtonEventArgs e)
        {
            ApplyUiToSettings();
            _settings.Save(notify: true);
            RefreshProviderPillsUi();

            bool hasKey = !string.IsNullOrWhiteSpace(TxtApiKeyOverride.Text);
            TxtSaveLocalKeyBtn.Text = hasKey ? "Saved" : "Save";
            SettingsSavedNotice.Text = hasKey
                ? $"Saved {_selectedSettingsProvider.ToUpperInvariant()} key locally"
                : "Enter an API key first";
            if (hasKey)
            {
                ToastNotification.Show($"✓ Saved {_selectedSettingsProvider.ToUpperInvariant()} Key");
            }
        }

        private void ClearProviderKey_Click(object sender, MouseButtonEventArgs e)
        {
            _isLoadingSettingsUi = true;
            TxtApiKeyOverride.Text = string.Empty;
            _isLoadingSettingsUi = false;

            _settings.SetApiKeyForProvider(_selectedSettingsProvider, string.Empty);
            _settings.Save(notify: true);
            RefreshProviderPillsUi();
            TxtSaveLocalKeyBtn.Text = "Save";
            SettingsSavedNotice.Text = $"Cleared {_selectedSettingsProvider.ToUpperInvariant()} key";
            ToastNotification.Show($"✓ Cleared {_selectedSettingsProvider.ToUpperInvariant()} Key");
        }

        private async void TestCloudHealth_Click(object sender, MouseButtonEventArgs e)
        {
            ApplyUiToSettings();
            _settings.Save(notify: true);

            TxtTestCloudBtn.Text = "...";
            bool ok = await AiClient.CheckBackendHealthAsync(_settings.CloudBackendUrl);
            TxtTestCloudBtn.Text = ok ? "Online" : "Offline";
            SettingsSavedNotice.Text = ok
                ? "Connected to Cloud AI Backend"
                : "Cloud Backend unreachable — using local API keys";
        }

        private async void SaveKeyToSupabase_Click(object sender, MouseButtonEventArgs e)
        {
            ApplyUiToSettings();
            _settings.Save(notify: true);
            RefreshProviderPillsUi();

            var key = _settings.GetApiKeyForProvider(_selectedSettingsProvider);
            if (string.IsNullOrWhiteSpace(key))
            {
                SettingsSavedNotice.Text = "Enter an API key before syncing";
                return;
            }

            TxtSaveVaultBtn.Text = "Syncing...";
            var (ok, msg) = await AiClient.SaveKeyToSupabaseVaultAsync(
                _settings.CloudBackendUrl,
                _selectedSettingsProvider,
                key);

            TxtSaveVaultBtn.Text = ok ? "Synced" : "Sync Vault";
            SettingsSavedNotice.Text = msg;
            ToastNotification.Show(ok ? "✓ Encrypted in Supabase Vault" : $"✓ Saved {_selectedSettingsProvider.ToUpperInvariant()} Key Locally");
        }

        private void RebuildIndex_Click(object sender, MouseButtonEventArgs e)
        {
            RefreshInstalledAppsAsync();
            FileSearchService.ForceReindex();
            RebuildBuiltInCommands();
            UpdateFilter();
            RefreshIndexTelemetryText();
            SettingsSavedNotice.Text = "✓ Rebuilding application and local file index in background...";
            ToastNotification.Show("✓ Rebuilding App & File Index");
        }

        private void ClearClipboardHistory_Click(object sender, MouseButtonEventArgs e)
        {
            ClipboardService.ClearHistory();
            RefreshIndexTelemetryText();
            SettingsSavedNotice.Text = "✓ Clipboard history cleared";
            ToastNotification.Show("✓ Cleared Clipboard History");
        }

        private void ResetFrecency_Click(object sender, MouseButtonEventArgs e)
        {
            SearchEngine.ClearFrecency();
            UpdateFilter();
            RefreshIndexTelemetryText();
            SettingsSavedNotice.Text = "✓ Frecency search rankings reset to default";
            ToastNotification.Show("✓ Reset Search Frecency Rankings");
        }

        private void CloseSettingsOverlay_Click(object sender, MouseButtonEventArgs e) => CloseSettingsOverlay();

        private void CloseSettingsOverlay()
        {
            ApplyUiToSettings();
            _settings.Save(notify: true);
            SettingsOverlay.Visibility = Visibility.Collapsed;
            LauncherView.Visibility = Visibility.Visible;
            FocusSearchBox();
        }

        private void SaveSettings_Click(object sender, MouseButtonEventArgs e)
        {
            CloseSettingsOverlay();
            ToastNotification.Show("✓ Kestrel Preferences Saved");
        }

        private void NoventraLink_Click(object sender, MouseButtonEventArgs e)
        {
            AppScanner.LaunchFile("https://github.com/Noventra-labs/Kestrel");
        }

        private void UpdateBadge_Click(object sender, MouseButtonEventArgs e)
        {
            OpenSettingsOverlay();
            SwitchToSettingsTab("General");
            SettingsScrollViewer.ScrollToBottom();
            if (!_isUpdating)
            {
                CheckUpdates_Click(sender, e);
            }
        }

        private void CancelUpdate_Click(object sender, MouseButtonEventArgs e)
        {
            _updateCts?.Cancel();
        }

        private bool _isUpdating;

        private void ResetUpdateProgressUi()
        {
            UpdateProgressContainer.Visibility = Visibility.Collapsed;
            BtnCancelUpdate.Visibility = Visibility.Collapsed;
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 0;
            TxtUpdateStage.Text = "Preparing update...";
            TxtUpdateBytes.Text = string.Empty;
        }

        private async void CheckUpdates_Click(object sender, MouseButtonEventArgs e)
        {
            if (_isUpdating) return;

            BtnCheckUpdatesText.Text = "Checking...";
            var info = await UpdateService.CheckForUpdatesAsync(force: true);
            if (info != null && info.IsNewer)
            {
                TxtUpdateStatus.Text = $"Update available: {info.LatestTag}";
                UpdateBadge.Visibility = Visibility.Visible;
                UpdateVersionText.Text = info.LatestTag;

                if (UpdateService.IsStandaloneKestrelExe() && !string.IsNullOrWhiteSpace(info.ExecutableUrl))
                {
                    _isUpdating = true;
                    _updateCts = new CancellationTokenSource();
                    _ignoreDeactivateUntil = DateTime.UtcNow.AddSeconds(2);

                    UpdateProgressContainer.Visibility = Visibility.Visible;
                    BtnCancelUpdate.Visibility = Visibility.Visible;
                    UpdateProgressBar.IsIndeterminate = info.ExecutableSizeBytes <= 0;
                    UpdateProgressBar.Value = 0;

                    string sizeLabel = info.ExecutableSizeBytes > 0
                        ? $" ({info.ExecutableSizeBytes / (1024.0 * 1024.0):F1} MB)"
                        : "";
                    TxtUpdateStatus.Text = $"Downloading {info.LatestTag}{sizeLabel}...";
                    TxtUpdateStage.Text = $"Downloading {info.LatestTag}...";
                    TxtUpdateBytes.Text = info.ExecutableSizeBytes > 0
                        ? $"0.0 MB / {info.ExecutableSizeBytes / (1024.0 * 1024.0):F1} MB (0%)"
                        : "0%";
                    BtnCheckUpdatesText.Text = "0%";

                    var progress = new Progress<UpdateProgressReport>(report =>
                    {
                        switch (report.Stage)
                        {
                            case UpdateStage.Downloading:
                                UpdateProgressBar.IsIndeterminate = report.TotalBytes <= 0;
                                UpdateProgressBar.Value = report.Percentage;
                                BtnCheckUpdatesText.Text = report.TotalBytes > 0 ? $"{report.Percentage}%" : "...";
                                TxtUpdateStage.Text = $"Downloading {info.LatestTag}...";
                                TxtUpdateBytes.Text = report.TotalBytes > 0
                                    ? $"{report.BytesReceived / (1024.0 * 1024.0):F1} MB / {report.TotalBytes / (1024.0 * 1024.0):F1} MB ({report.Percentage}%)"
                                    : $"{report.BytesReceived / (1024.0 * 1024.0):F1} MB";
                                break;

                            case UpdateStage.Verifying:
                                UpdateProgressBar.IsIndeterminate = true;
                                BtnCancelUpdate.Visibility = Visibility.Collapsed;
                                BtnCheckUpdatesText.Text = "Verifying...";
                                TxtUpdateStage.Text = "Verifying SHA-256 integrity...";
                                TxtUpdateBytes.Text = "100%";
                                break;

                            case UpdateStage.Installing:
                                UpdateProgressBar.IsIndeterminate = true;
                                BtnCancelUpdate.Visibility = Visibility.Collapsed;
                                BtnCheckUpdatesText.Text = "Restarting...";
                                TxtUpdateStage.Text = "Installing & restarting Kestrel...";
                                TxtUpdateBytes.Text = "";
                                _ignoreDeactivateUntil = DateTime.UtcNow.AddSeconds(15);
                                break;
                        }
                    });

                    try
                    {
                        var result = await UpdateService.DownloadAndApplyUpdateAsync(info, progress, _updateCts.Token);

                        if (result.Started)
                        {
                            _isRestartingForUpdate = true;
                            TxtUpdateStatus.Text = $"Restarting to {info.LatestTag}...";
                            BtnCheckUpdatesText.Text = "Restarting...";
                            CloseForReal();
                            System.Windows.Application.Current.Shutdown();
                            return;
                        }

                        ResetUpdateProgressUi();
                        if (result.Cancelled)
                        {
                            TxtUpdateStatus.Text = $"Update available: {info.LatestTag}";
                            BtnCheckUpdatesText.Text = "Update Now";
                            ToastNotification.Show("Update cancelled");
                        }
                        else
                        {
                            TxtUpdateStatus.Text = $"Update failed: {result.ErrorMessage ?? "Unknown error"}";
                            BtnCheckUpdatesText.Text = "Retry Update";
                            ToastNotification.Show("Auto-update failed — opening download in browser");
                            UpdateService.OpenReleasePage(UpdateService.ResolveDownloadTargetUrl(info));
                        }
                    }
                    finally
                    {
                        _updateCts.Dispose();
                        _updateCts = null;
                        _isUpdating = false;
                    }
                }
                else
                {
                    BtnCheckUpdatesText.Text = "Update Now";
                    UpdateService.OpenReleasePage(UpdateService.ResolveDownloadTargetUrl(info));
                }
            }
            else
            {
                ResetUpdateProgressUi();
                TxtUpdateStatus.Text = $"Kestrel is up to date (v{UpdateService.CurrentVersion})";
                BtnCheckUpdatesText.Text = "Up to Date ✓";
                ToastNotification.Show("✓ Kestrel is running the latest version");
            }
        }

        #endregion

        #region Confetti Animation

        private void ShootConfetti()
        {
            ConfettiCanvas.Children.Clear();
            var rnd = new Random();
            var colors = new[]
            {
                System.Windows.Media.Color.FromRgb(255, 99, 99),
                System.Windows.Media.Color.FromRgb(59, 130, 246),
                System.Windows.Media.Color.FromRgb(16, 185, 129),
                System.Windows.Media.Color.FromRgb(251, 191, 36),
                System.Windows.Media.Color.FromRgb(168, 85, 247),
                System.Windows.Media.Color.FromRgb(236, 72, 153)
            };

            for (int i = 0; i < 65; i++)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = rnd.Next(6, 12),
                    Height = rnd.Next(6, 14),
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(colors[rnd.Next(colors.Length)]),
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(rnd.Next(0, 360))
                };

                double startX = 335 + rnd.Next(-70, 70);
                double startY = 210 + rnd.Next(-35, 35);
                double endX = startX + rnd.Next(-300, 300);
                double endY = rnd.Next(380, 460);

                Canvas.SetLeft(rect, startX);
                Canvas.SetTop(rect, startY);
                ConfettiCanvas.Children.Add(rect);

                var dur = TimeSpan.FromMilliseconds(rnd.Next(900, 1800));
                var animX = new DoubleAnimation(startX, endX, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var animY = new DoubleAnimation(startY, endY, dur) { EasingFunction = new BounceEase { Bounces = 1, Bounciness = 3 } };
                var animOp = new DoubleAnimation(1.0, 0.0, dur);

                rect.BeginAnimation(Canvas.LeftProperty, animX);
                rect.BeginAnimation(Canvas.TopProperty, animY);
                rect.BeginAnimation(OpacityProperty, animOp);
            }
        }

        #endregion

        #region AI Assistant Chat

        private void QuickAiBadge_Click(object sender, MouseButtonEventArgs e)
        {
            OpenGeminiChatOverlay(SearchBox.Text.Trim());
        }

        private void NewGeminiChat_Click(object sender, MouseButtonEventArgs e)
        {
            _aiCts?.Cancel();
            _aiCts = null;
            _apiConversation.Clear();
            ApiChatHistoryPanel.Children.Clear();
            AiUserPromptText.Text = "Ask anything...";
            AiResponseText.Text = "";
            AiPromptInputBox.Text = "";
            AiPromptInputBox.Focus();
        }

        private async void OpenGeminiChatOverlay(string promptInput)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            ActionsOverlay.Visibility = Visibility.Collapsed;
            LauncherView.Visibility = Visibility.Collapsed;
            AiOverlay.Visibility = Visibility.Visible;
            GeminiApiChatContainer.Visibility = Visibility.Visible;

            var cleanedPrompt = promptInput.Trim();
            if (cleanedPrompt.Equals("gemini", StringComparison.OrdinalIgnoreCase) ||
                cleanedPrompt.Equals("gemini chat", StringComparison.OrdinalIgnoreCase) ||
                cleanedPrompt.Equals("ai", StringComparison.OrdinalIgnoreCase))
            {
                cleanedPrompt = "";
            }
            else if (cleanedPrompt.StartsWith("ai ", StringComparison.OrdinalIgnoreCase))
            {
                cleanedPrompt = cleanedPrompt[3..].Trim();
            }
            else if (cleanedPrompt.StartsWith("gemini ", StringComparison.OrdinalIgnoreCase))
            {
                cleanedPrompt = cleanedPrompt[7..].Trim();
            }

            AiModelTag.Text = string.IsNullOrWhiteSpace(_settings.DefaultAiModel) ? "" : _settings.DefaultAiModel;
            AiPromptInputBox.Focus();
            Keyboard.Focus(AiPromptInputBox);

            if (!string.IsNullOrWhiteSpace(cleanedPrompt))
            {
                await StreamApiMessageAsync(cleanedPrompt);
            }
        }

        private async void AiPromptInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                var text = AiPromptInputBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    AiPromptInputBox.Text = "";
                    await StreamApiMessageAsync(text);
                }
            }
        }

        private async void SendApiChatPrompt_Click(object sender, MouseButtonEventArgs e)
        {
            var text = AiPromptInputBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                AiPromptInputBox.Text = "";
                await StreamApiMessageAsync(text);
            }
        }

        private async Task StreamApiMessageAsync(string prompt)
        {
            _aiCts?.Cancel();
            var requestCts = new CancellationTokenSource();
            _aiCts = requestCts;
            if (_apiConversationModel != _settings.DefaultAiModel)
            {
                _apiConversation.Clear();
                ApiChatHistoryPanel.Children.Clear();
                AiResponseText.Text = "";
                _apiConversationModel = _settings.DefaultAiModel;
            }
            var messages = _apiConversation.Append(new ChatMessage("user", prompt)).ToArray();
            var responseText = new System.Text.StringBuilder();
            // Archive previous exchange into history panel if it had content
            if (AiUserPromptText.Text != "Ask Gemini anything below..." &&
                AiUserPromptText.Text != "New Conversation Started" &&
                !string.IsNullOrWhiteSpace(AiResponseText.Text))
            {
                var prevUser = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x14, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = "You: " + AiUserPromptText.Text,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                var prevAi = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x0D, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1C, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock
                    {
                        Text = AiResponseText.Text,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
                        FontSize = 13.5,
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                ApiChatHistoryPanel.Children.Add(prevUser);
                ApiChatHistoryPanel.Children.Add(prevAi);
            }

            AiUserPromptText.Text = prompt;
            AiResponseText.Text = "Streaming from Gemini AI...";
            AiStatusText.Text = "Streaming response...";
            ApiChatScrollViewer.ScrollToBottom();

            try
            {
                bool firstChunk = true;
                await AiClient.StreamChatAsync(
                    _settings.CloudBackendUrl,
                    messages,
                    _settings.DefaultAiModel,
                    _settings.GetApiKeyForModel(_settings.DefaultAiModel),
                    delta =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_aiCts != requestCts || requestCts.IsCancellationRequested) return;
                            responseText.Append(delta);
                            if (firstChunk)
                            {
                                AiResponseText.Text = "";
                                firstChunk = false;
                            }
                            AiResponseText.Text += delta;
                            ApiChatScrollViewer.ScrollToBottom();
                        });
                    },
                    requestCts.Token);

                if (_aiCts != requestCts || requestCts.IsCancellationRequested) return;
                string answer = responseText.ToString();
                if (!string.IsNullOrWhiteSpace(answer) &&
                    !answer.StartsWith("Error:") && !answer.StartsWith("Gemini Error") &&
                    !answer.StartsWith("API Error") && !answer.StartsWith("Anthropic Error"))
                {
                    _apiConversation.Add(new ChatMessage("user", prompt));
                    _apiConversation.Add(new ChatMessage("assistant", answer));
                }
                AiStatusText.Text = "✓ Completed • Type below to continue chatting";
            }
            catch (OperationCanceledException)
            {
                if (_aiCts != requestCts) return;
                AiStatusText.Text = "Cancelled";
            }
            catch (Exception ex)
            {
                if (_aiCts != requestCts) return;
                AiResponseText.Text = $"⚠️ Fast API Connection Notice:\n{ex.Message}\n\n" +
                    "Tip: Add your API Key in Settings (Ctrl+,) → AI tab, or verify your Cloud Backend Endpoint.";
                AiStatusText.Text = "Configure API Key or Cloud Endpoint in Settings (Ctrl+,)";
            }
            finally
            {
                if (_aiCts == requestCts) _aiCts = null;
                requestCts.Dispose();
            }
        }

        private void CloseAiOverlay_Click(object sender, MouseButtonEventArgs e) => CloseAiOverlay();

        private void CloseAiOverlay()
        {
            _aiCts?.Cancel();
            AiOverlay.Visibility = Visibility.Collapsed;
            LauncherView.Visibility = Visibility.Visible;
            FocusSearchBox();
        }

        private void CopyAiResponse_Click(object sender, MouseButtonEventArgs e) => CopyAiResponse();

        private void CopyAiResponse()
        {
            if (!string.IsNullOrEmpty(AiResponseText.Text))
            {
                System.Windows.Clipboard.SetText(AiResponseText.Text);
                AiStatusText.Text = "✓ Copied response to clipboard!";
                ToastNotification.Show("✓ Copied Gemini Response to Clipboard");
            }
        }

        #endregion
    }
}
