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
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Beam.Interop;
using Beam.Models;
using Beam.Services;

namespace Beam
{
    public enum LauncherMode
    {
        MainSearch,
        ClipboardHistory,
        ProcessKiller,
        FileSearch
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
        private bool _webViewInitialized = false;
        private Microsoft.Web.WebView2.Wpf.WebView2? GeminiWebView;
        private DispatcherTimer? _webViewTeardownTimer;
        private bool _isGeminiWebModeActive = true;
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;
        private CancellationTokenSource? _aiCts;
        private CancellationTokenSource? _searchCts;
        private Forms.NotifyIcon? _notifyIcon;
        private GlobalKeyboardHook? _keyboardHook;
        private DispatcherTimer? _watchdogTimer;
        private BeamSettings _settings = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            GlobalKeyboardHook.BeamWindowHandle = helper.Handle;

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

            _settings = BeamSettings.Load();
            BeamSettings.SettingsChanged += OnSettingsChanged;

            InitializeTrayIcon();

            // Install Low-Level Global Keyboard Hook for BOTH Start Button (Win Key) & Alt + Space
            try
            {
                _keyboardHook = new GlobalKeyboardHook(() =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(SummonOrToggle));
                })
                {
                    EnableStartKeyTrigger = _settings.TriggerOnStartKey,
                    EnableAltSpaceHotkey = _settings.EnableAltSpaceHotkey
                };
                App.Log("Global keyboard hook (Start Button + Alt+Space) installed successfully.");
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

        private void OnSettingsChanged(BeamSettings updated)
        {
            _settings = updated;
            if (_keyboardHook != null)
            {
                _keyboardHook.EnableStartKeyTrigger = updated.TriggerOnStartKey;
                _keyboardHook.EnableAltSpaceHotkey = updated.EnableAltSpaceHotkey;
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
        }

        private void RebuildBuiltInCommands()
        {
            _builtInCommands = SystemCommandsService.GetSystemCommands(
                openClipboardView: () => SwitchMode(LauncherMode.ClipboardHistory),
                openProcessKillerView: () => SwitchMode(LauncherMode.ProcessKiller),
                openFileSearchView: () => SwitchMode(LauncherMode.FileSearch),
                openSettingsView: OpenSettingsOverlay,
                triggerConfetti: ShootConfetti,
                openGeminiChat: (webMode, prompt) => OpenGeminiChatOverlay(webMode, prompt)
            );

            _builtInCommands.Insert(0, new LauncherItem
            {
                Id = "cmd_manage_api_keys",
                Name = "Insert / Switch Local AI API Keys (Gemini, OpenAI, Groq, Claude)",
                Subtitle = "Preferences — Add, change, or swap local API keys anytime",
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
                string icoPath = Path.Combine(exeDir, "beam.ico");
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

                var menu = new Forms.ContextMenuStrip();

                var openItem = new Forms.ToolStripMenuItem("Open Kestrel (Start Key / Alt + Space)");
                openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
                openItem.Click += (s, e) => Dispatcher.Invoke(Summon);

                var geminiItem = new Forms.ToolStripMenuItem("Open Embedded Gemini Chat (Tab)");
                geminiItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); OpenGeminiChatOverlay(_settings.UseEmbeddedGeminiWeb, ""); });

                var fileItem = new Forms.ToolStripMenuItem("Search Local Files (Ctrl + F)");
                fileItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); SwitchMode(LauncherMode.FileSearch); });

                var clipItem = new Forms.ToolStripMenuItem("Clipboard History (Ctrl + Shift + V)");
                clipItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); SwitchMode(LauncherMode.ClipboardHistory); });

                var procItem = new Forms.ToolStripMenuItem("Kill Process Manager");
                procItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); SwitchMode(LauncherMode.ProcessKiller); });

                var settingsItem = new Forms.ToolStripMenuItem("Preferences & Settings (Ctrl + ,)");
                settingsItem.Click += (s, e) => Dispatcher.Invoke(() => { Summon(); OpenSettingsOverlay(); });

                var exitItem = new Forms.ToolStripMenuItem("Exit Kestrel Completely");
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
                    Text = "Kestrel Launcher — Active (Press Win Key or Alt+Space)",
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

            // Do not auto-hide if focus is inside our own process or child WebView2 HWND
            var helper = new WindowInteropHelper(this);
            if (DwmApi.IsForegroundOwnedByBeamOrChild(helper.Handle))
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
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            BeamSettings.SettingsChanged -= OnSettingsChanged;
            SystemEvents.PowerModeChanged -= OnSystemPowerOrSessionChanged;
            SystemEvents.SessionSwitch -= OnSystemPowerOrSessionChanged;
            ClipboardService.ShutdownListener();
            TeardownWebView2();
            _notifyIcon?.Dispose();
            _keyboardHook?.Dispose();
            _source?.RemoveHook(HwndHook);
        }

        public void CloseForReal()
        {
            _isRealClose = true;
            _watchdogTimer?.Stop();
            TeardownWebView2();
            ClipboardService.ShutdownListener();
            _notifyIcon?.Dispose();
            _keyboardHook?.Dispose();
            Close();
        }

        public void HideAndTrim(bool showFirstTimeHint = true)
        {
            if (!IsVisible) return;

            // Always collapse WebView2 HWND before hiding so it never holds GPU/hit-test resources
            if (GeminiWebView != null)
            {
                GeminiWebView.Visibility = Visibility.Collapsed;
                try { _ = GeminiWebView.CoreWebView2?.TrySuspendAsync(); } catch { }
                StartWebViewTeardownTimer();
            }
            GeminiWebViewContainer.Visibility = Visibility.Collapsed;
            AiOverlay.Visibility = Visibility.Collapsed;

            Hide();

            if (showFirstTimeHint && !_shownTrayHintOnce)
            {
                _shownTrayHintOnce = true;
                ToastNotification.Show("Kestrel is active in System Tray • Press Windows Key or Alt+Space anytime");
            }

            if (_settings.TrimMemoryOnHide)
            {
                Task.Run(DwmApi.TrimMemoryWorkingSet);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WmBeamSummon)
            {
                Summon();
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

            if (AiOverlay.Visibility != Visibility.Visible && SettingsOverlay.Visibility != Visibility.Visible)
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
            }
        }

        public void SummonOrToggle()
        {
            var helper = new WindowInteropHelper(this);
            bool hasFocus = IsActive || DwmApi.IsForegroundOwnedByBeamOrChild(helper.Handle);
            if (IsVisible && WindowState != WindowState.Minimized && hasFocus)
            {
                HideAndTrim();
            }
            else
            {
                Summon();
            }
        }

        private void SwitchMode(LauncherMode mode)
        {
            _currentMode = mode;
            SearchBox.Text = string.Empty;

            if (mode == LauncherMode.MainSearch)
            {
                BackButtonBorder.Visibility = Visibility.Collapsed;
                BrandLogoPath.Visibility = Visibility.Visible;
                ActiveModePill.Visibility = Visibility.Collapsed;
                FilterTabsPanel.Visibility = Visibility.Visible;
                PlaceholderText.Text = "Search apps, 'gemini', commands, window snap, math, or files...";
            }
            else if (mode == LauncherMode.ClipboardHistory)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Clipboard History";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Search copied clipboard items (Click or Enter to Paste)...";
            }
            else if (mode == LauncherMode.ProcessKiller)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Kill Process";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Filter running processes by name or window title...";
            }
            else if (mode == LauncherMode.FileSearch)
            {
                BackButtonBorder.Visibility = Visibility.Visible;
                BrandLogoPath.Visibility = Visibility.Collapsed;
                ActiveModePill.Visibility = Visibility.Visible;
                ActiveModeText.Text = "Search Files";
                FilterTabsPanel.Visibility = Visibility.Collapsed;
                PlaceholderText.Text = "Fuzzy search files in Desktop, Downloads, Documents & Projects...";
            }

            UpdateFilter();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
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
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 47, 61))
                        : System.Windows.Media.Brushes.Transparent;
                    if (tab.Child is TextBlock tb)
                    {
                        tb.Foreground = isSelected
                            ? System.Windows.Media.Brushes.White
                            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
                        tb.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    }
                }

                UpdateFilter();
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
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
            var cfg = BeamSettings.Current;

            // Sub-view 1: Clipboard History
            if (_currentMode == LauncherMode.ClipboardHistory)
            {
                SectionHeaderText.Text = "Recent Copied Items (Click or Press Enter to Paste into Active Window)";
                _currentItems = ClipboardService.GetClipboardLauncherItems(query, () =>
                {
                    SwitchMode(LauncherMode.MainSearch);
                    HideAndTrim(false);
                    ToastNotification.Show("✓ Pasted from Clipboard History");
                });
                BindResults();
                return;
            }

            // Sub-view 2: Process Killer
            if (_currentMode == LauncherMode.ProcessKiller)
            {
                SectionHeaderText.Text = "Active Windows Processes (Sorted by Memory Usage)";
                _currentItems = SystemCommandsService.GetProcessKillerItems(query, UpdateFilter);
                BindResults();
                return;
            }

            // Sub-view 3: Local File Search
            if (_currentMode == LauncherMode.FileSearch)
            {
                SectionHeaderText.Text = "Local Files (Click or Enter to Open • Ctrl+R to Reveal in Explorer)";
                _currentItems = FileSearchService.SearchFiles(query, 45);
                BindResults();
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
                    Subtitle = $"Calculator & Converter • {math.Expression} (Enter/Click: Copy • Shift+Enter: Paste)",
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

            // 2. Instant Power Developer Tools Evaluator (guid, sha256, md5, epoch, base64)
            var dev = PowerDeveloperToolsService.EvaluateInline(query);
            if (dev.IsValid)
            {
                var devIcon = IconGenerator.GetSquircleIcon(dev.Theme, dev.SvgPath);
                list.Add(new LauncherItem
                {
                    Id = "dev:" + dev.Result,
                    Name = dev.Title,
                    Subtitle = dev.Subtitle,
                    Category = dev.Category,
                    ShortcutBadge = dev.Badge,
                    Icon = devIcon,
                    Action = () =>
                    {
                        System.Windows.Clipboard.SetText(dev.Result);
                        ToastNotification.Show($"✓ Copied {dev.Category}: {dev.Title}");
                    }
                });
            }

            var pool = new List<LauncherItem>(_builtInCommands.Count + _allApps.Count);
            pool.AddRange(_builtInCommands);
            pool.AddRange(_allApps);

            if (_activeCategoryFilter != "All")
            {
                pool = pool.Where(x => x.Category.Contains(_activeCategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (string.IsNullOrEmpty(query))
            {
                SectionHeaderText.Text = _activeCategoryFilter == "All"
                    ? $"Frecency & Top Commands ({_builtInCommands.Count + _allApps.Count} items indexed)"
                    : $"{_activeCategoryFilter}s";

                list.AddRange(SearchEngine.RankAndFilter(pool, "", 55));
                _currentItems = list;
                BindResults();
            }
            else
            {
                SectionHeaderText.Text = (math.IsValid || dev.IsValid) ? "Instant Results & Top Matches" : "Top Fuzzy & Acronym Matches";

                // Offload ranking and file searching to background Task to keep UI thread completely responsive (<5ms)
                Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return;

                    var ranked = SearchEngine.RankAndFilter(pool, query, 40);
                    if (token.IsCancellationRequested) return;

                    var fileMatches = (cfg.EnableFileSearch && query.Length >= 2 && _activeCategoryFilter == "All")
                        ? FileSearchService.SearchFiles(query, 6)
                        : null;
                    if (token.IsCancellationRequested) return;

                    var webItems = SystemCommandsService.GetWebFallbackItems(query, (webMode, p) => OpenGeminiChatOverlay(webMode, p));
                    if (token.IsCancellationRequested) return;

                    var combined = new List<LauncherItem>(list.Count + ranked.Count + (fileMatches?.Count ?? 0) + webItems.Count);
                    combined.AddRange(list);
                    combined.AddRange(ranked);
                    if (fileMatches != null) combined.AddRange(fileMatches);
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
                StatusText.Text = "No matching items";
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
                    "Calculator" or "Hasher" or "Timestamp" or "Timestamp Converter" or "Base64 Encoder" or "Base64 Decoder" or "GUID Generator" => "Copy Result",
                    "Clipboard" or "Pinned Clip" => "Paste to App",
                    "Window Management" => "Snap Window",
                    "PowerToys" => item.Name.Contains("Color") ? "Pick Color" : (item.Name.Contains("Text") ? "Snip OCR" : "Run"),
                    "System Tool" => item.Name.Contains("Mem") ? "Free RAM" : "Run",
                    "AI Command" or "Gemini AI" => "Open Gemini",
                    _ when item.Id.StartsWith("proc:") => "Kill Process",
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

            // 5. Standard Navigation
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
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
                OpenGeminiChatOverlay(_settings.UseEmbeddedGeminiWeb, SearchBox.Text.Trim());
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

            if (!keepOpen)
            {
                SearchBox.Focus();
            }
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

        private void CopySelectedItemPath()
        {
            if (ResultsList.SelectedItem is LauncherItem item && !string.IsNullOrEmpty(item.Path))
            {
                System.Windows.Clipboard.SetText(item.Path);
                HideAndTrim(false);
                ToastNotification.Show("✓ Copied File Path to Clipboard");
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

            if (!string.IsNullOrEmpty(item.Path))
            {
                actions.Add(new ActionItem
                {
                    Title = "Reveal in File Explorer",
                    ShortcutHint = "Ctrl+R",
                    Icon = iconFolder,
                    Execute = RevealSelectedItemInExplorer
                });

                actions.Add(new ActionItem
                {
                    Title = "Copy Full Path to Clipboard",
                    ShortcutHint = "Ctrl+C",
                    Icon = iconCopy,
                    Execute = CopySelectedItemPath
                });
            }

            actions.Add(new ActionItem
            {
                Title = "Copy Title to Clipboard",
                ShortcutHint = "",
                Icon = iconCopy,
                Execute = CopySelectedItemName
            });

            if (_settings.EnableAiFeatures)
            {
                actions.Add(new ActionItem
                {
                    Title = "Open Embedded Google Gemini Chat",
                    ShortcutHint = "Tab",
                    Icon = iconAi,
                    Execute = () => OpenGeminiChatOverlay(_settings.UseEmbeddedGeminiWeb, SearchBox.Text.Trim())
                });
            }

            if (_settings.EnableFileSearch)
            {
                actions.Add(new ActionItem
                {
                    Title = "Search Local Files",
                    ShortcutHint = "Ctrl+F",
                    Icon = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathSearch),
                    Execute = () => SwitchMode(LauncherMode.FileSearch)
                });
            }

            if (_settings.EnableClipboardHistory)
            {
                actions.Add(new ActionItem
                {
                    Title = "Open Clipboard History",
                    ShortcutHint = "Ctrl+Shift+V",
                    Icon = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathClipboard),
                    Execute = () => SwitchMode(LauncherMode.ClipboardHistory)
                });
            }

            actions.Add(new ActionItem
            {
                Title = "Kestrel Preferences & Settings",
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
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
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
            // Ensure WebView2 is collapsed while viewing Settings so its native HWND doesn't overlap
            if (GeminiWebView != null)
            {
                GeminiWebView.Visibility = Visibility.Collapsed;
                try { _ = GeminiWebView.CoreWebView2?.TrySuspendAsync(); } catch { }
                StartWebViewTeardownTimer();
            }
            GeminiWebViewContainer.Visibility = Visibility.Collapsed;
            AiOverlay.Visibility = Visibility.Collapsed;

            _isLoadingSettingsUi = true;
            _settings = BeamSettings.Load();

            // Tab 1: General & Hotkeys
            ChkStartKeyTrigger.IsChecked = _settings.TriggerOnStartKey;
            ChkAltSpaceTrigger.IsChecked = _settings.EnableAltSpaceHotkey;
            ChkLaunchStartup.IsChecked = _settings.LaunchOnStartup;
            ChkHideOutside.IsChecked = _settings.HideOnClickOutside;
            ChkToastNotifications.IsChecked = _settings.EnableToastNotifications;
            ChkTrimMemory.IsChecked = _settings.TrimMemoryOnHide;

            // Tab 2: Modules & Extensions
            ChkEnableAi.IsChecked = _settings.EnableAiFeatures;
            ChkEnableFileSearch.IsChecked = _settings.EnableFileSearch;
            ChkEnableClipboard.IsChecked = _settings.EnableClipboardHistory;
            ChkEnableWindowSnap.IsChecked = _settings.EnableWindowManagement;
            ChkEnableCalculator.IsChecked = _settings.EnableCalculator;
            ChkEnableProcessKiller.IsChecked = _settings.EnableProcessKiller;
            ChkEnableSystemCmds.IsChecked = _settings.EnableSystemCommands;
            ChkEnableWebFallbacks.IsChecked = _settings.EnableWebFallbacks;

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
            SettingsSavedNotice.Text = "Kestrel v2.3.1 • Noventra Labs";
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
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 40, 54))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 21, 28));
                p.Border.BorderBrush = isSelected
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 99, 99))
                    : hasKey
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 90, 71))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 39, 53));
                p.Txt.Foreground = isSelected
                    ? System.Windows.Media.Brushes.White
                    : hasKey
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 231, 183))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
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
            var tabBorders = new[] { SetTabGeneral, SetTabModules, SetTabAi, SetTabData };
            var tabTexts = new[] { SetTabGeneralTxt, SetTabModulesTxt, SetTabAiTxt, SetTabDataTxt };

            for (int i = 0; i < tabBorders.Length; i++)
            {
                bool active = string.Equals(tabBorders[i].Tag as string, tabName, StringComparison.OrdinalIgnoreCase);
                tabBorders[i].Background = active
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 40, 54))
                    : System.Windows.Media.Brushes.Transparent;
                tabBorders[i].BorderBrush = active
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 59, 79))
                    : System.Windows.Media.Brushes.Transparent;
                tabTexts[i].Foreground = active
                    ? System.Windows.Media.Brushes.White
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
                tabTexts[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }

            SettingsTabGeneralPanel.Visibility = tabName == "General" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabModulesPanel.Visibility = tabName == "Modules" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabAiPanel.Visibility = tabName == "AI" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabDataPanel.Visibility = tabName == "Data" ? Visibility.Visible : Visibility.Collapsed;

            if (tabName == "Data")
            {
                RefreshIndexTelemetryText();
            }
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
            _settings.EnableAltSpaceHotkey = ChkAltSpaceTrigger.IsChecked == true;
            _settings.LaunchOnStartup = ChkLaunchStartup.IsChecked == true;
            _settings.HideOnClickOutside = ChkHideOutside.IsChecked == true;
            _settings.EnableToastNotifications = ChkToastNotifications.IsChecked == true;
            _settings.TrimMemoryOnHide = ChkTrimMemory.IsChecked == true;

            _settings.EnableAiFeatures = ChkEnableAi.IsChecked == true;
            _settings.EnableFileSearch = ChkEnableFileSearch.IsChecked == true;
            _settings.EnableClipboardHistory = ChkEnableClipboard.IsChecked == true;
            _settings.EnableWindowManagement = ChkEnableWindowSnap.IsChecked == true;
            _settings.EnableCalculator = ChkEnableCalculator.IsChecked == true;
            _settings.EnableProcessKiller = ChkEnableProcessKiller.IsChecked == true;
            _settings.EnableSystemCommands = ChkEnableSystemCmds.IsChecked == true;
            _settings.EnableWebFallbacks = ChkEnableWebFallbacks.IsChecked == true;

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
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
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
            UpdateService.OpenReleasePage();
        }

        private async void CheckUpdates_Click(object sender, MouseButtonEventArgs e)
        {
            BtnCheckUpdatesText.Text = "Checking...";
            var info = await UpdateService.CheckForUpdatesAsync(force: true);
            if (info != null && info.IsNewer)
            {
                TxtUpdateStatus.Text = $"Update available: {info.LatestTag}";
                BtnCheckUpdatesText.Text = "Update Now";
                UpdateBadge.Visibility = Visibility.Visible;
                UpdateVersionText.Text = info.LatestTag;
                UpdateService.OpenReleasePage(string.IsNullOrEmpty(info.InstallerUrl) ? info.HtmlUrl : info.InstallerUrl);
            }
            else
            {
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

                double startX = 380 + rnd.Next(-80, 80);
                double startY = 260 + rnd.Next(-40, 40);
                double endX = startX + rnd.Next(-360, 360);
                double endY = rnd.Next(480, 560);

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

        #region Embedded Google Gemini Chat

        private void QuickAiBadge_Click(object sender, MouseButtonEventArgs e)
        {
            OpenGeminiChatOverlay(true, SearchBox.Text.Trim());
        }

        private void NewGeminiChat_Click(object sender, MouseButtonEventArgs e)
        {
            if (_isGeminiWebModeActive)
            {
                if (_webViewInitialized && GeminiWebView?.CoreWebView2 != null)
                {
                    GeminiWebView.CoreWebView2.Navigate("https://gemini.google.com/app");
                }
            }
            else
            {
                ApiChatHistoryPanel.Children.Clear();
                AiUserPromptText.Text = "Ask anything...";
                AiResponseText.Text = "";
                AiPromptInputBox.Text = "";
                AiPromptInputBox.Focus();
            }
        }

        private void OpenGeminiChatOverlay(bool useWebMode, string promptInput)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            ActionsOverlay.Visibility = Visibility.Collapsed;
            AiOverlay.Visibility = Visibility.Visible;

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

            SetGeminiOverlayMode(useWebMode, cleanedPrompt);
        }

        private async void SetGeminiOverlayMode(bool useWebMode, string initialPrompt)
        {
            _isGeminiWebModeActive = useWebMode;

            if (useWebMode)
            {
                _webViewTeardownTimer?.Stop();
                GeminiApiChatContainer.Visibility = Visibility.Collapsed;
                GeminiWebViewContainer.Visibility = Visibility.Visible;
                AiModelTag.Text = "";
                AiStatusText.Text = "Gemini Ready";

                if (!string.IsNullOrWhiteSpace(initialPrompt))
                {
                    System.Windows.Clipboard.SetText(initialPrompt);
                    ToastNotification.Show("✓ Prompt copied — press Ctrl+V in Gemini");
                }

                await EnsureGeminiWebViewInitializedAsync();
                if (GeminiWebView != null)
                {
                    try { GeminiWebView.CoreWebView2?.Resume(); } catch { }
                    GeminiWebView.Visibility = Visibility.Visible;
                    GeminiWebView.Focus();
                }
            }
            else
            {
                if (GeminiWebView != null)
                {
                    GeminiWebView.Visibility = Visibility.Collapsed;
                    try { _ = GeminiWebView.CoreWebView2?.TrySuspendAsync(); } catch { }
                    StartWebViewTeardownTimer();
                }
                GeminiWebViewContainer.Visibility = Visibility.Collapsed;
                GeminiApiChatContainer.Visibility = Visibility.Visible;

                AiModelTag.Text = string.IsNullOrWhiteSpace(_settings.DefaultAiModel) ? "" : _settings.DefaultAiModel;
                AiPromptInputBox.Focus();
                Keyboard.Focus(AiPromptInputBox);

                if (!string.IsNullOrWhiteSpace(initialPrompt))
                {
                    await StreamApiMessageAsync(initialPrompt);
                }
            }
        }

        private void StartWebViewTeardownTimer()
        {
            _webViewTeardownTimer?.Stop();
            _webViewTeardownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _webViewTeardownTimer.Tick += (s, e) =>
            {
                _webViewTeardownTimer?.Stop();
                TeardownWebView2();
            };
            _webViewTeardownTimer.Start();
        }

        private void TeardownWebView2()
        {
            _webViewTeardownTimer?.Stop();
            _webViewTeardownTimer = null;
            if (GeminiWebView != null)
            {
                try
                {
                    GeminiWebHostGrid.Children.Remove(GeminiWebView);
                    GeminiWebView.Dispose();
                }
                catch { }
                GeminiWebView = null;
                _webViewInitialized = false;
                App.Log("WebView2 idle teardown: disposed and memory freed.");
            }
        }

        private async Task EnsureGeminiWebViewInitializedAsync()
        {
            _webViewTeardownTimer?.Stop();
            if (_webViewInitialized && GeminiWebView?.CoreWebView2 != null)
            {
                GeminiWebLoadingText.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                GeminiWebLoadingText.Visibility = Visibility.Visible;
                GeminiWebLoadingText.Text = "Initializing Embedded Google Gemini (WebView2)...";

                if (GeminiWebView == null)
                {
                    GeminiWebView = new Microsoft.Web.WebView2.Wpf.WebView2
                    {
                        Margin = new Thickness(2),
                        Visibility = Visibility.Collapsed
                    };
                    GeminiWebHostGrid.Children.Add(GeminiWebView);
                }

                string profileDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Kestrel",
                    "WebView2Profile");

                Directory.CreateDirectory(profileDir);

                var env = await CoreWebView2Environment.CreateAsync(null, profileDir);
                await GeminiWebView.EnsureCoreWebView2Async(env);

                GeminiWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

                // Intercept Esc inside WebView2 child HWND via both WPF KeyDown forwarding and DOM capture
                GeminiWebView.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        e.Handled = true;
                        CloseAiOverlay();
                    }
                };

                if (GeminiWebView.CoreWebView2 != null)
                {
                    await GeminiWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                        "window.addEventListener('keydown', function(e) { if (e.key === 'Escape') { window.chrome.webview.postMessage('BEAM_ESC'); } }, true);");

                    GeminiWebView.CoreWebView2.WebMessageReceived += (s, e) =>
                    {
                        if (e.TryGetWebMessageAsString() == "BEAM_ESC")
                        {
                            Dispatcher.BeginInvoke(CloseAiOverlay);
                        }
                    };

                    GeminiWebView.CoreWebView2.NavigationCompleted += (s, e) =>
                    {
                        GeminiWebLoadingText.Visibility = Visibility.Collapsed;
                    };

                    GeminiWebView.CoreWebView2.Navigate("https://gemini.google.com/app");
                }
                _webViewInitialized = true;
            }
            catch (Exception ex)
            {
                App.Log($"WebView2 init error: {ex}");
                GeminiWebLoadingText.Text = $"WebView2 Notice: {ex.Message}\nSwitching to Fast API Stream mode...";
                await Task.Delay(900);
                SetGeminiOverlayMode(useWebMode: false, initialPrompt: "");
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
            // Archive previous exchange into history panel if it had content
            if (AiUserPromptText.Text != "Ask Gemini anything below..." &&
                AiUserPromptText.Text != "New Conversation Started" &&
                !string.IsNullOrWhiteSpace(AiResponseText.Text))
            {
                var prevUser = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 33, 43)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(47, 54, 70)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = "You: " + AiUserPromptText.Text,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 213, 219)),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                var prevAi = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 25, 34)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 44, 58)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock
                    {
                        Text = AiResponseText.Text,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 250, 251)),
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

            _aiCts?.Cancel();
            _aiCts = new CancellationTokenSource();

            try
            {
                bool firstChunk = true;
                await AiClient.StreamChatAsync(
                    _settings.CloudBackendUrl,
                    prompt,
                    _settings.DefaultAiModel,
                    _settings.GetApiKeyForModel(_settings.DefaultAiModel),
                    delta =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (firstChunk)
                            {
                                AiResponseText.Text = "";
                                firstChunk = false;
                            }
                            AiResponseText.Text += delta;
                            ApiChatScrollViewer.ScrollToBottom();
                        });
                    },
                    _aiCts.Token);

                AiStatusText.Text = "✓ Completed • Type below to continue chatting";
            }
            catch (OperationCanceledException)
            {
                AiStatusText.Text = "Cancelled";
            }
            catch (Exception ex)
            {
                AiResponseText.Text = $"⚠️ Fast API Connection Notice:\n{ex.Message}\n\n" +
                    "Tip: Click '✦ Gemini Web (gemini.google.com)' in the top bar above to chat with Google Gemini directly without needing a backend or API key, or add your Gemini API Key in Settings (Ctrl+,)!";
                AiStatusText.Text = "Click '✦ Gemini Web' above or configure API Key in Settings";
            }
        }

        private void CloseAiOverlay_Click(object sender, MouseButtonEventArgs e) => CloseAiOverlay();

        private void CloseAiOverlay()
        {
            _aiCts?.Cancel();
            // Strictly collapse WebView2 HWND so it never blocks WPF airspace
            if (GeminiWebView != null)
            {
                GeminiWebView.Visibility = Visibility.Collapsed;
                try { _ = GeminiWebView.CoreWebView2?.TrySuspendAsync(); } catch { }
                StartWebViewTeardownTimer();
            }
            GeminiWebViewContainer.Visibility = Visibility.Collapsed;
            AiOverlay.Visibility = Visibility.Collapsed;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
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
