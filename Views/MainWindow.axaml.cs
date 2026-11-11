using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KnightWinNotes.Models;
using KnightWinNotes.Services;

namespace KnightWinNotes.Views;

public partial class MainWindow : Window
{
    public ObservableCollection<NoteTabItem> Tabs { get; } = new();

    private AppSettings _settings = new();
    private NoteTabItem? _activeTab;
    private bool _suppressEditorSync;
    private bool _suppressSettingsSync;
    private bool _forceCloseConfirmed;

    // Pending modal state
    private bool _pendingSaveIsSaveAs;
    private bool _pendingForceCustomPwOnly;
    private string? _pendingUnlockFilePath;
    private byte[]? _pendingUnlockBytes;
    private MasterDialogMode _masterDialogMode = MasterDialogMode.SetNew;
    private NoteTabItem? _pendingCloseTab;
    private bool _pendingCloseWholeWindow;
    private CancellationTokenSource? _toastCts;

    private enum MasterDialogMode
    {
        SetNew,
        ChangeExisting,
        RemoveExisting
    }

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsService.Load();
        TabsItemsControl.ItemsSource = Tabs;

        ConfigurePlatformWindowChrome();
        ApplyThemeAndSettingsToUi();

        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(KeyDownEvent, OnPreviewWindowKeyDown, RoutingStrategies.Tunnel);
        EditorTextBox.AddHandler(PointerWheelChangedEvent, OnEditorPointerWheelChanged, RoutingStrategies.Tunnel);

        Activated += (_, _) =>
        {
            if (_settings.ThemeMode == AppThemeMode.Sync || _settings.Accent == AccentPreset.Sync)
                ApplyThemeAndSettingsToUi();
        };

        if (Application.Current?.PlatformSettings is { } ps)
        {
            ps.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_settings.ThemeMode == AppThemeMode.Sync || _settings.Accent == AccentPreset.Sync)
                    ApplyThemeAndSettingsToUi();
            });
        }

        Opened += OnMainWindowOpened;
        Closing += OnMainWindowClosing;
        Closed += OnMainWindowClosed;
    }

    private void ConfigurePlatformWindowChrome()
    {
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 42;
            WindowsCaptionSpacer.Width = 142;
        }
        else
        {
            WindowsCaptionSpacer.Width = 12;
        }
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        ApplyNativeDwmAttributes();

        var startupFiles = App.StartupFiles;
        if (startupFiles is { Count: > 0 })
        {
            foreach (string file in startupFiles)
            {
                if (File.Exists(file))
                    await OpenFileFromPathAsync(file);
            }
        }

        if (Tabs.Count == 0)
        {
            CreateAndSelectNewTab();
        }

        RebuildRecentFilesMenu();
        EditorTextBox.Focus();
    }

    private void ApplyNativeDwmAttributes()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (Application.Current is not null)
        {
            bool useDwmFallback = OperatingSystem.IsWindows() && ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur;
            FluentPaletteEngine.Apply(Application.Current, _settings, hwnd, useDwmFallback);
        }
    }

    private void ApplyThemeAndSettingsToUi()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (Application.Current is not null)
        {
            bool useDwmFallback = OperatingSystem.IsWindows() && ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur;
            FluentPaletteEngine.Apply(Application.Current, _settings, hwnd, useDwmFallback);
        }

        // Update live Windows system accent dot inside Settings
        Color sysAccent = WindowsNativeInterop.GetSystemAccentColor();
        SyncAccentSwatchDot.Background = new SolidColorBrush(sysAccent);

        // Editor typography, zoom, word-wrap & status bar
        EditorTextBox.FontFamily = new FontFamily(_settings.EditorFontFamily);
        UpdateEditorZoomFontSize();
        EditorTextBox.TextWrapping = _settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        WordWrapCheckIcon.IsVisible = _settings.WordWrap;

        StatusBarBorder.IsVisible = _settings.ShowStatusBar;
        StatusBarCheckIcon.IsVisible = _settings.ShowStatusBar;
        PadCardBorder.Margin = _settings.ShowStatusBar
            ? new Thickness(6, 0, 6, 2)
            : new Thickness(6, 0, 6, 6);

        UpdateStatusBarAndBadges();
        SyncSettingsControlsFromModel();
    }

    private void UpdateEditorZoomFontSize()
    {
        int zoom = _activeTab?.ZoomPercent ?? 100;
        double effectiveSize = Math.Clamp(_settings.EditorFontSize * (zoom / 100.0), 8.0, 96.0);
        EditorTextBox.FontSize = effectiveSize;
    }

    private void SyncSettingsControlsFromModel()
    {
        _suppressSettingsSync = true;
        try
        {
            SetSelectedClass(ModeDarkBtn, _settings.ThemeMode == AppThemeMode.Dark);
            SetSelectedClass(ModeLightBtn, _settings.ThemeMode == AppThemeMode.Light);
            SetSelectedClass(ModeSyncBtn, _settings.ThemeMode == AppThemeMode.Sync);

            SetSelectedClass(AccentSyncBtn, _settings.Accent == AccentPreset.Sync);
            SetSelectedClass(AccentRedBtn, _settings.Accent == AccentPreset.Red);
            SetSelectedClass(AccentPurpleBtn, _settings.Accent == AccentPreset.Purple);
            SetSelectedClass(AccentMatchaBtn, _settings.Accent == AccentPreset.Matcha);
            SetSelectedClass(AccentBeigeBtn, _settings.Accent == AccentPreset.Beige);
            SetSelectedClass(AccentOrangeBtn, _settings.Accent == AccentPreset.Orange);

            SetSelectedClass(PadAccentBtn, _settings.PadColor == PadColorPreset.Accent);
            SetSelectedClass(PadYellowBtn, _settings.PadColor == PadColorPreset.Yellow);
            SetSelectedClass(PadWhiteBtn, _settings.PadColor == PadColorPreset.White);

            AdaptDarkPadSwitch.IsChecked = _settings.AdaptPadInDarkMode;

            // Font Family Combo
            int familyIdx = 0;
            for (int i = 0; i < FontFamilyCombo.Items.Count; i++)
            {
                if (FontFamilyCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), _settings.EditorFontFamily, StringComparison.OrdinalIgnoreCase))
                {
                    familyIdx = i;
                    break;
                }
            }
            FontFamilyCombo.SelectedIndex = familyIdx;

            // Font Size Combo
            int sizeIdx = 2; // 15 pt default
            string targetSize = ((int)Math.Round(_settings.EditorFontSize)).ToString();
            for (int i = 0; i < FontSizeCombo.Items.Count; i++)
            {
                if (FontSizeCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == targetSize)
                {
                    sizeIdx = i;
                    break;
                }
            }
            FontSizeCombo.SelectedIndex = sizeIdx;

            // Security Section
            SetSelectedClass(PolicyUseMasterBtn, _settings.PasswordPolicy == SecureSavePasswordPolicy.UseMasterPassword);
            SetSelectedClass(PolicyAskEveryTimeBtn, _settings.PasswordPolicy == SecureSavePasswordPolicy.AskEveryTime);

            if (_settings.HasMasterPassword)
            {
                MasterStatusBadgeText.Text = "Master Password Active";
                MasterPasswordDetailTitle.Text = "Master Password is Set (DPAPI + PBKDF2-SHA512)";
                MasterPasswordDetailSub.Text = _settings.PasswordPolicy == SecureSavePasswordPolicy.UseMasterPassword
                    ? "Secure saves use your Master Password by default, with an option to pick a custom password per file."
                    : "Master Password is saved, and policy is set to ask for a password every time you save a Secure file.";
                SetOrChangeMasterBtn.Content = "Change Master Password";
                RemoveMasterBtn.IsVisible = true;
            }
            else
            {
                MasterStatusBadgeText.Text = "No Master Password";
                MasterPasswordDetailTitle.Text = "Master Password (Not Set)";
                MasterPasswordDetailSub.Text = "You haven't set a default Master Password yet — saving a Secure file will ask you to create a password.";
                SetOrChangeMasterBtn.Content = "Set Master Password";
                RemoveMasterBtn.IsVisible = false;
            }

            if (OperatingSystem.IsWindows() && WindowsNativeInterop.IsKwNoteAssociated())
            {
                AssocStatusText.Text = "✓ .kwnote is registered with KnightWin Notes in Windows Explorer.";
            }
        }
        finally
        {
            _suppressSettingsSync = false;
        }
    }

    private static void SetSelectedClass(Control ctrl, bool selected)
    {
        if (selected)
        {
            if (!ctrl.Classes.Contains("selected"))
                ctrl.Classes.Add("selected");
        }
        else
        {
            ctrl.Classes.Remove("selected");
        }
    }

    // =======================================================================
    // TAB MANAGEMENT & EDITOR BINDING
    // =======================================================================

    public NoteTabItem CreateAndSelectNewTab(string initialContent = "", string? filePath = null, bool isSecure = false, string lineEnding = "CRLF")
    {
        var tab = new NoteTabItem();
        if (!string.IsNullOrEmpty(initialContent) || !string.IsNullOrWhiteSpace(filePath) || isSecure)
        {
            tab.LoadInitialContent(initialContent, lineEnding, filePath, isSecure);
        }
        Tabs.Add(tab);
        SelectTab(tab);
        return tab;
    }

    public void SelectTab(NoteTabItem tab)
    {
        if (_activeTab == tab && EditorTextBox.Text == tab.Content)
        {
            UpdateStatusBarAndBadges();
            return;
        }

        // Save outgoing caret position
        if (_activeTab is not null)
        {
            _activeTab.IsSelected = false;
        }

        _activeTab = tab;
        _activeTab.IsSelected = true;

        _suppressEditorSync = true;
        try
        {
            EditorTextBox.Text = _activeTab.Content;
            int safeCaret = Math.Clamp(_activeTab.CaretIndex, 0, _activeTab.Content.Length);
            EditorTextBox.CaretIndex = safeCaret;
            EditorTextBox.SelectionStart = safeCaret;
            EditorTextBox.SelectionEnd = safeCaret;
        }
        finally
        {
            _suppressEditorSync = false;
        }

        UpdateEditorZoomFontSize();
        UpdateStatusBarAndBadges();
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        if (SettingsWorkspaceScroll.IsVisible)
        {
            Title = "KnightWin Notes — Settings";
            return;
        }

        if (_activeTab is null)
        {
            Title = "KnightWin Notes";
            return;
        }

        string prefix = _activeTab.IsSecureFile ? "[Encrypted] " : string.Empty;
        Title = $"{prefix}{_activeTab.HeaderTitle} - KnightWin Notes";
    }

    private void UpdateStatusBarAndBadges()
    {
        if (_activeTab is null)
            return;

        StatusCaretText.Text = _activeTab.CaretPositionText;
        StatusCountText.Text = _activeTab.StatsText;
        StatusSecurityText.Text = _activeTab.SecurityBadgeText;
        StatusZoomText.Text = _activeTab.ZoomText;
        StatusLineEndingText.Text = _activeTab.LineEndingDisplay;

        bool secureSelected = _activeTab.IsSecureModeSelected;
        SetSelectedClass(SecureModeToggleBtn, secureSelected);

        if (Application.Current is { } app)
        {
            string toggleGeoKey = secureSelected ? "IconShieldLock" : "IconShieldOutline";
            if (app.Resources.TryGetResource(toggleGeoKey, ActualThemeVariant, out object? tGeoObj) && tGeoObj is Geometry tGeo)
            {
                SecureModeToggleIcon.Data = tGeo;
            }

            string fgKey = secureSelected ? "AccentOnFillText" : "TextSecondary";
            if (app.Resources.TryGetResource(fgKey, ActualThemeVariant, out object? brushObj) && brushObj is IBrush fgBrush)
            {
                SecureModeToggleIcon.Foreground = fgBrush;
            }

            string statusGeoKey = (_activeTab.IsSecureFile || secureSelected) ? "IconShieldLock" : "IconDocumentPlain";
            if (app.Resources.TryGetResource(statusGeoKey, ActualThemeVariant, out object? sGeoObj) && sGeoObj is Geometry sGeo)
            {
                StatusSecurityIcon.Data = sGeo;
            }
        }

        ToolTip.SetTip(
            SecureModeToggleBtn,
            secureSelected
                ? "Secure Mode: On (Saves as Encrypted .kwnote) — Click to switch to Plain mode"
                : "Secure Mode: Off (Saves as Plain .txt) — Click to enable .kwnote encryption");
    }

    private void OnToggleSecureModeClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null)
            return;

        _activeTab.IsSecureModeSelected = !_activeTab.IsSecureModeSelected;
        UpdateStatusBarAndBadges();

        ShowToast(_activeTab.IsSecureModeSelected
            ? "Secure Mode On — Ctrl+S will encrypt & save as .kwnote"
            : "Secure Mode Off — Ctrl+S will save as Plain .txt");
    }

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEditorSync || _activeTab is null)
            return;

        _activeTab.Content = EditorTextBox.Text ?? string.Empty;
        _activeTab.CaretIndex = EditorTextBox.CaretIndex;
        _activeTab.SelectionLength = Math.Abs(EditorTextBox.SelectionEnd - EditorTextBox.SelectionStart);
        UpdateStatusBarAndBadges();
        UpdateWindowTitle();
    }

    private void OnEditorTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_suppressEditorSync || _activeTab is null)
            return;

        if (e.Property == TextBox.CaretIndexProperty ||
            e.Property == TextBox.SelectionStartProperty ||
            e.Property == TextBox.SelectionEndProperty)
        {
            _activeTab.CaretIndex = EditorTextBox.CaretIndex;
            _activeTab.SelectionLength = Math.Abs(EditorTextBox.SelectionEnd - EditorTextBox.SelectionStart);
            StatusCaretText.Text = _activeTab.CaretPositionText;
            StatusCountText.Text = _activeTab.StatsText;
        }
    }

    private void OnTabBorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: NoteTabItem tab })
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed)
            {
                RequestCloseTab(tab);
                e.Handled = true;
                return;
            }

            if (SettingsWorkspaceScroll.IsVisible)
                ShowEditorView();

            SelectTab(tab);
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only initiate window move when clicking empty titlebar space (not buttons or tabs)
        if (e.Source is Visual v)
        {
            Visual? curr = v;
            while (curr is not null && curr != TitleBarInteractiveGrid)
            {
                if (curr is Button || (curr is Border b && b.Tag is NoteTabItem))
                    return;
                curr = curr.Parent as Visual;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void OnNewTabClick(object? sender, RoutedEventArgs e)
    {
        if (SettingsWorkspaceScroll.IsVisible)
            ShowEditorView();
        CreateAndSelectNewTab();
        EditorTextBox.Focus();
    }

    private void OnNewWindowClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true });
            }
        }
        catch
        {
            // Ignore if blocked by sandbox
        }
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoteTabItem tab })
        {
            RequestCloseTab(tab);
        }
    }

    private void OnCloseActiveTabClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
            RequestCloseTab(_activeTab);
    }

    private void RequestCloseTab(NoteTabItem tab)
    {
        if (tab.IsModified)
        {
            SelectTab(tab);
            _pendingCloseTab = tab;
            _pendingCloseWholeWindow = false;
            UnsavedPromptText.Text = $"Do you want to save changes to \"{tab.Title}\"?";
            ShowModalCard(UnsavedDialogCard);
            return;
        }

        CloseTabImmediate(tab);
    }

    private void CloseTabImmediate(NoteTabItem tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0)
            return;

        tab.ReleaseMemory();
        Tabs.RemoveAt(idx);

        if (Tabs.Count == 0)
        {
            CreateAndSelectNewTab();
            return;
        }

        if (_activeTab == tab)
        {
            int nextIdx = Math.Clamp(idx - 1, 0, Tabs.Count - 1);
            SelectTab(Tabs[nextIdx]);
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_pendingUnlockBytes is not null)
        {
            CryptographicOperations.ZeroMemory(_pendingUnlockBytes);
            _pendingUnlockBytes = null;
        }

        foreach (var tab in Tabs)
        {
            tab.ReleaseMemory();
        }

        NoteTabItem.WipeProcessSessionKey();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceCloseConfirmed)
            return;

        var dirtyTab = Tabs.FirstOrDefault(t => t.IsModified);
        if (dirtyTab is not null)
        {
            e.Cancel = true;
            if (SettingsWorkspaceScroll.IsVisible)
                ShowEditorView();
            SelectTab(dirtyTab);
            _pendingCloseTab = dirtyTab;
            _pendingCloseWholeWindow = true;
            UnsavedPromptText.Text = $"Do you want to save changes to \"{dirtyTab.Title}\" before exiting?";
            ShowModalCard(UnsavedDialogCard);
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    // =======================================================================
    // OPEN & SAVE WORKFLOWS (PLAIN .TXT & SECURE .KWNOTE)
    // =======================================================================

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Text or KnightWin Secure Note",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All KnightWin & Text Notes (*.txt;*.kwnote)")
                {
                    Patterns = new[] { "*.txt", "*.kwnote", "*.md", "*.log", "*.json", "*.csv" }
                },
                new FilePickerFileType("KnightWin Encrypted Note (*.kwnote)")
                {
                    Patterns = new[] { "*.kwnote" }
                },
                new FilePickerFileType("Plain Text Documents (*.txt)")
                {
                    Patterns = new[] { "*.txt" }
                },
                FilePickerFileTypes.All
            }
        });

        foreach (var f in files)
        {
            string? localPath = f.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
                await OpenFileFromPathAsync(localPath);
        }
    }

    public async Task OpenFileFromPathAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ShowToast($"File not found: {Path.GetFileName(filePath)}");
            return;
        }

        string fullPath = Path.GetFullPath(filePath);

        // Focus existing tab if already open
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (SettingsWorkspaceScroll.IsVisible)
                ShowEditorView();
            SelectTab(existing);
            return;
        }

        byte[] rawBytes;
        try
        {
            rawBytes = await File.ReadAllBytesAsync(fullPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Cannot read file: {ex.Message}");
            return;
        }

        bool isSecureMagic = KnightCryptoService.HasSecureMagic(rawBytes);
        bool hasSecureExt = fullPath.EndsWith(KnightCryptoService.SecureExtension, StringComparison.OrdinalIgnoreCase);

        if (isSecureMagic || hasSecureExt)
        {
            if (!isSecureMagic)
            {
                ShowToast("Invalid .kwnote container signature.");
                return;
            }

            if (SettingsWorkspaceScroll.IsVisible)
                ShowEditorView();

            _pendingUnlockFilePath = fullPath;
            _pendingUnlockBytes = rawBytes;
            UnlockFileNameLabel.Text = Path.GetFileName(fullPath);
            UnlockPwBox.Text = string.Empty;
            RevealUnlockPwToggle.IsChecked = false;
            UnlockPwBox.PasswordChar = '●';
            UnlockErrorText.IsVisible = false;
            ShowModalCard(UnlockNoteDialogCard);
            Dispatcher.UIThread.Post(() => UnlockPwBox.Focus());
            return;
        }

        // Plain UTF-8 text file
        string rawText = Encoding.UTF8.GetString(rawBytes);
        // Strip UTF-8 BOM if present
        if (rawText.Length > 0 && rawText[0] == '\uFEFF')
            rawText = rawText[1..];

        var (normalizedLf, detectedEnding) = KnightCryptoService.NormalizeAndDetectLineEnding(rawText);

        if (SettingsWorkspaceScroll.IsVisible)
            ShowEditorView();

        // Reuse single clean empty Untitled tab if untouched
        if (Tabs.Count == 1 && !Tabs[0].HasFilePath && !Tabs[0].IsModified && string.IsNullOrEmpty(Tabs[0].Content))
        {
            Tabs[0].LoadInitialContent(normalizedLf, detectedEnding, fullPath, isSecure: false);
            SelectTab(Tabs[0]);
        }
        else
        {
            CreateAndSelectNewTab(normalizedLf, fullPath, isSecure: false, detectedEnding);
        }

        SettingsService.AddRecentFile(_settings, fullPath);
        RebuildRecentFilesMenu();
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null)
            return;

        foreach (var item in files)
        {
            string? localPath = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                await OpenFileFromPathAsync(localPath);
            }
        }
    }

    private void RebuildRecentFilesMenu()
    {
        RecentFilesMenuItem.Items.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            RecentFilesMenuItem.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
            return;
        }

        foreach (string path in _settings.RecentFiles)
        {
            string captured = path;
            bool isSec = captured.EndsWith(KnightCryptoService.SecureExtension, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = $"{(isSec ? "🔒 " : "")}{Path.GetFileName(captured)}  —  {captured}"
            };
            item.Click += async (_, _) => await OpenFileFromPathAsync(captured);
            RecentFilesMenuItem.Items.Add(item);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        await TriggerSmartSaveAsync();
    }

    private async Task TriggerSmartSaveAsync()
    {
        if (_activeTab is null)
            return;

        // 1. Secure Mode Toggle is OFF (Default) -> Save as Plain (.txt) just like Windows Notepad!
        if (!_activeTab.IsSecureModeSelected)
        {
            if (_activeTab.HasFilePath && !_activeTab.IsSecureFile)
            {
                await WritePlainFileDirectAsync(_activeTab, _activeTab.FilePath!);
            }
            else
            {
                await SaveActiveTabAsPlainAsync(forcePicker: true);
            }
            await ResumePendingCloseIfNeededAsync();
            return;
        }

        // 2. Secure Mode Toggle is ON -> Save as Encrypted (.kwnote)!
        if (_activeTab.HasFilePath && _activeTab.IsSecureFile && _activeTab.HasSessionPassword)
        {
            string? sessionPw = _activeTab.GetSessionPassword();
            if (!string.IsNullOrEmpty(sessionPw))
            {
                await WriteSecureFileDirectAsync(_activeTab, _activeTab.FilePath!, sessionPw);
                await ResumePendingCloseIfNeededAsync();
                return;
            }
        }

        _pendingSaveIsSaveAs = !_activeTab.IsSecureFile;
        OpenSecureSavePasswordDialog(forceCustomPasswordOnly: false);
    }

    private void OnSaveAsChoiceClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null)
            return;
        _pendingSaveIsSaveAs = true;
        ShowModalCard(SaveModeDialogCard);
    }

    private async void OnDialogChoosePlainClick(object? sender, RoutedEventArgs e)
    {
        HideAllModals();
        if (_activeTab is not null)
        {
            _activeTab.IsSecureModeSelected = false;
            UpdateStatusBarAndBadges();
        }
        await SaveActiveTabAsPlainAsync(forcePicker: true);
    }

    private void OnDialogChooseSecureClick(object? sender, RoutedEventArgs e)
    {
        HideAllModals();
        if (_activeTab is not null)
        {
            _activeTab.IsSecureModeSelected = true;
            UpdateStatusBarAndBadges();
        }
        OpenSecureSavePasswordDialog(forceCustomPasswordOnly: false);
    }

    private async Task SaveActiveTabAsPlainAsync(bool forcePicker)
    {
        if (_activeTab is null)
            return;

        string? targetPath = _activeTab.FilePath;
        if (forcePicker || string.IsNullOrWhiteSpace(targetPath) || _activeTab.IsSecureFile)
        {
            string suggested = _activeTab.HasFilePath
                ? Path.GetFileNameWithoutExtension(_activeTab.FilePath!) + ".txt"
                : "Untitled.txt";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save as Plain Text (.txt)",
                SuggestedFileName = suggested,
                DefaultExtension = "txt",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Text Documents (*.txt)") { Patterns = new[] { "*.txt" } },
                    FilePickerFileTypes.All
                }
            });

            targetPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(targetPath))
                return;
        }

        await WritePlainFileDirectAsync(_activeTab, targetPath);
        await ResumePendingCloseIfNeededAsync();
    }

    public async Task WritePlainFileDirectAsync(NoteTabItem tab, string filePath)
    {
        try
        {
            string formatted = KnightCryptoService.FormatWithLineEnding(tab.Content, tab.LineEnding);
            string dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
            Directory.CreateDirectory(dir);

            string tmp = filePath + ".kwtmp";
            await File.WriteAllTextAsync(tmp, formatted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, filePath, overwrite: true);

            tab.ClearSessionPassword();
            tab.MarkSaved(filePath, isSecure: false);

            SettingsService.AddRecentFile(_settings, filePath);
            RebuildRecentFilesMenu();
            UpdateStatusBarAndBadges();
            UpdateWindowTitle();
            ShowToast($"Saved Plain text: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            ShowToast($"Save failed: {ex.Message}");
        }
    }

    public void OpenSecureSavePasswordDialog(bool forceCustomPasswordOnly)
    {
        _pendingForceCustomPwOnly = forceCustomPasswordOnly;
        SecureSaveErrorText.IsVisible = false;
        SecureNewPwBox.Text = string.Empty;
        SecureConfirmPwBox.Text = string.Empty;
        RevealSecurePwToggle.IsChecked = false;
        SecureNewPwBox.PasswordChar = '●';
        SecureConfirmPwBox.PasswordChar = '●';
        UpdatePasswordStrengthUi(string.Empty, SecurePwStrengthBar, SecurePwStrengthText);

        bool canUseMasterDefault = !forceCustomPasswordOnly &&
                                   _settings.HasMasterPassword &&
                                   _settings.PasswordPolicy == SecureSavePasswordPolicy.UseMasterPassword;

        if (canUseMasterDefault)
        {
            SecureMasterChoicePanel.IsVisible = true;
            UseMasterPwRadio.IsChecked = true;
            UseCustomPwRadio.IsChecked = false;
            SecureCustomPasswordFields.IsVisible = false;
            SecureSaveSubHeader.Text = "Use your default Master Password, or create a unique password for this file.";
        }
        else
        {
            SecureMasterChoicePanel.IsVisible = false;
            SecureCustomPasswordFields.IsVisible = true;
            SecureSaveSubHeader.Text = !_settings.HasMasterPassword
                ? "Create a password for this .kwnote file (or set a default Master Password in Settings → Security)."
                : "Create a password to encrypt this .kwnote file with AES-256-GCM.";
        }

        ShowModalCard(SecureSavePasswordCard);
        if (SecureCustomPasswordFields.IsVisible)
            Dispatcher.UIThread.Post(() => SecureNewPwBox.Focus());
    }

    private void OnSecureSaveModeRadioChanged(object? sender, RoutedEventArgs e)
    {
        if (SecureCustomPasswordFields is null)
            return;

        bool customSelected = UseCustomPwRadio.IsChecked == true;
        SecureCustomPasswordFields.IsVisible = customSelected;
        SecureSaveErrorText.IsVisible = false;
        if (customSelected)
            Dispatcher.UIThread.Post(() => SecureNewPwBox.Focus());
    }

    private void OnRevealSecurePwToggled(object? sender, RoutedEventArgs e)
    {
        char mask = RevealSecurePwToggle.IsChecked == true ? '\0' : '●';
        SecureNewPwBox.PasswordChar = mask;
        SecureConfirmPwBox.PasswordChar = mask;
    }

    private void OnSecureNewPwTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePasswordStrengthUi(SecureNewPwBox.Text, SecurePwStrengthBar, SecurePwStrengthText);
    }

    private void OnSecureConfirmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirmSecureSavePasswordClick(sender, e);
            e.Handled = true;
        }
    }

    private async void OnConfirmSecureSavePasswordClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null)
            return;

        string? chosenPassword;
        bool useMaster = SecureMasterChoicePanel.IsVisible && UseMasterPwRadio.IsChecked == true;

        if (useMaster)
        {
            chosenPassword = KnightCryptoService.UnprotectStoredMasterPassword(_settings.MasterPasswordProtectedBlobBase64);
            if (string.IsNullOrEmpty(chosenPassword))
            {
                SecureSaveErrorText.Text = "Could not unprotect Master Password from DPAPI. Please re-set it in Settings → Security or choose a custom password.";
                SecureSaveErrorText.IsVisible = true;
                return;
            }
        }
        else
        {
            string pw1 = SecureNewPwBox.Text ?? string.Empty;
            string pw2 = SecureConfirmPwBox.Text ?? string.Empty;

            if (pw1.Length < 4)
            {
                SecureSaveErrorText.Text = "Please enter a password of at least 4 characters.";
                SecureSaveErrorText.IsVisible = true;
                return;
            }

            if (!string.Equals(pw1, pw2, StringComparison.Ordinal))
            {
                SecureSaveErrorText.Text = "Passwords do not match. Please verify both fields.";
                SecureSaveErrorText.IsVisible = true;
                return;
            }

            chosenPassword = pw1;
        }

        // Clear password UI inputs immediately
        SecureNewPwBox.Text = string.Empty;
        SecureConfirmPwBox.Text = string.Empty;
        HideAllModals();

        string? targetPath = (_activeTab.IsSecureFile && _activeTab.HasFilePath && !_pendingSaveIsSaveAs)
            ? _activeTab.FilePath
            : null;

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            string baseName = _activeTab.HasFilePath
                ? Path.GetFileNameWithoutExtension(_activeTab.FilePath!)
                : "ProtectedNote";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Secure Encrypted Note (.kwnote)",
                SuggestedFileName = baseName + KnightCryptoService.SecureExtension,
                DefaultExtension = "kwnote",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("KnightWin Encrypted Note (*.kwnote)")
                    {
                        Patterns = new[] { "*.kwnote" }
                    }
                }
            });

            targetPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            if (!targetPath.EndsWith(KnightCryptoService.SecureExtension, StringComparison.OrdinalIgnoreCase))
                targetPath += KnightCryptoService.SecureExtension;
        }

        await WriteSecureFileDirectAsync(_activeTab, targetPath, chosenPassword);
        await ResumePendingCloseIfNeededAsync();
    }

    public async Task WriteSecureFileDirectAsync(NoteTabItem tab, string filePath, string password)
    {
        try
        {
            byte[] encryptedContainer = KnightCryptoService.EncryptNote(tab.Content, tab.LineEnding, password);
            string dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
            Directory.CreateDirectory(dir);

            string tmp = filePath + ".kwtmp";
            try
            {
                await File.WriteAllBytesAsync(tmp, encryptedContainer);
                File.Move(tmp, filePath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedContainer);
            }

            tab.SetSessionPassword(password);
            tab.MarkSaved(filePath, isSecure: true);

            SettingsService.AddRecentFile(_settings, filePath);
            RebuildRecentFilesMenu();
            UpdateStatusBarAndBadges();
            UpdateWindowTitle();
            ShowToast($"Encrypted & saved: {Path.GetFileName(filePath)} (AES-256-GCM)");
        }
        catch (Exception ex)
        {
            ShowToast($"Encryption save failed: {ex.Message}");
        }
    }

    // =======================================================================
    // UNLOCK SECURE .KWNOTE DIALOG
    // =======================================================================

    private void OnRevealUnlockPwToggled(object? sender, RoutedEventArgs e)
    {
        UnlockPwBox.PasswordChar = RevealUnlockPwToggle.IsChecked == true ? '\0' : '●';
    }

    private void OnUnlockPwKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirmUnlockNoteClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnConfirmUnlockNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_pendingUnlockBytes is null || string.IsNullOrWhiteSpace(_pendingUnlockFilePath))
        {
            HideAllModals();
            return;
        }

        string candidatePw = UnlockPwBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(candidatePw))
        {
            UnlockErrorText.Text = "Please enter the password for this Secure note.";
            UnlockErrorText.IsVisible = true;
            return;
        }

        try
        {
            var (content, lineEnding, _) = KnightCryptoService.DecryptNote(_pendingUnlockBytes, candidatePw);
            string openedPath = _pendingUnlockFilePath;

            CryptographicOperations.ZeroMemory(_pendingUnlockBytes);
            UnlockPwBox.Text = string.Empty;
            _pendingUnlockBytes = null;
            _pendingUnlockFilePath = null;
            HideAllModals();

            NoteTabItem targetTab;
            if (Tabs.Count == 1 && !Tabs[0].HasFilePath && !Tabs[0].IsModified && string.IsNullOrEmpty(Tabs[0].Content))
            {
                targetTab = Tabs[0];
                targetTab.LoadInitialContent(content, lineEnding, openedPath, isSecure: true);
            }
            else
            {
                targetTab = CreateAndSelectNewTab(content, openedPath, isSecure: true, lineEnding);
            }

            targetTab.SetSessionPassword(candidatePw);
            SelectTab(targetTab);

            SettingsService.AddRecentFile(_settings, openedPath);
            RebuildRecentFilesMenu();
            ShowToast($"Unlocked {Path.GetFileName(openedPath)}");
            EditorTextBox.Focus();
        }
        catch (Exception ex)
        {
            UnlockErrorText.Text = ex.Message;
            UnlockErrorText.IsVisible = true;
            UnlockPwBox.SelectAll();
            UnlockPwBox.Focus();
        }
    }

    // =======================================================================
    // UNSAVED CHANGES DIALOG HANDLERS
    // =======================================================================

    private async void OnUnsavedSaveClick(object? sender, RoutedEventArgs e)
    {
        HideAllModals();
        await TriggerSmartSaveAsync();
    }

    private void OnUnsavedDiscardClick(object? sender, RoutedEventArgs e)
    {
        var tabToClose = _pendingCloseTab;
        bool closeWhole = _pendingCloseWholeWindow;
        _pendingCloseTab = null;
        _pendingCloseWholeWindow = false;
        HideAllModals();

        if (tabToClose is not null)
        {
            // Revert modification flag & close
            tabToClose.LoadInitialContent(string.Empty, tabToClose.LineEnding, tabToClose.FilePath, tabToClose.IsSecureFile);
            if (closeWhole)
            {
                if (Tabs.All(t => !t.IsModified))
                {
                    _forceCloseConfirmed = true;
                    Close();
                    return;
                }
                CloseTabImmediate(tabToClose);
                Close();
            }
            else
            {
                CloseTabImmediate(tabToClose);
            }
        }
    }

    private Task ResumePendingCloseIfNeededAsync()
    {
        if (_pendingCloseTab is not null && !_pendingCloseTab.IsModified)
        {
            var tab = _pendingCloseTab;
            bool whole = _pendingCloseWholeWindow;
            _pendingCloseTab = null;
            _pendingCloseWholeWindow = false;

            if (whole)
            {
                if (Tabs.All(t => !t.IsModified))
                {
                    _forceCloseConfirmed = true;
                    Close();
                }
                else
                {
                    Close();
                }
            }
            else
            {
                CloseTabImmediate(tab);
            }
        }
        return Task.CompletedTask;
    }

    // =======================================================================
    // EDIT, FIND & REPLACE, GO TO LINE, ZOOM COMMANDS
    // =======================================================================

    private void OnUndoClick(object? sender, RoutedEventArgs e) => EditorTextBox.Undo();
    private void OnRedoClick(object? sender, RoutedEventArgs e) => EditorTextBox.Redo();
    private void OnCutClick(object? sender, RoutedEventArgs e) => EditorTextBox.Cut();
    private void OnCopyClick(object? sender, RoutedEventArgs e) => EditorTextBox.Copy();
    private void OnPasteClick(object? sender, RoutedEventArgs e) => EditorTextBox.Paste();
    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        EditorTextBox.Focus();
        EditorTextBox.SelectAll();
    }

    private void OnDeleteSelectionClick(object? sender, RoutedEventArgs e)
    {
        int start = Math.Min(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        int end = Math.Max(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        string text = EditorTextBox.Text ?? string.Empty;
        if (end > start && end <= text.Length)
        {
            EditorTextBox.Text = text.Remove(start, end - start);
            EditorTextBox.CaretIndex = start;
        }
    }

    private void OnInsertTimeDateClick(object? sender, RoutedEventArgs e)
    {
        string stamp = DateTime.Now.ToString("h:mm tt M/d/yyyy");
        string text = EditorTextBox.Text ?? string.Empty;
        int start = Math.Min(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        int end = Math.Max(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        EditorTextBox.Text = text[..start] + stamp + text[end..];
        int newCaret = start + stamp.Length;
        EditorTextBox.CaretIndex = newCaret;
        EditorTextBox.SelectionStart = newCaret;
        EditorTextBox.SelectionEnd = newCaret;
        EditorTextBox.Focus();
    }

    private void OnOpenFindClick(object? sender, RoutedEventArgs e)
    {
        ShowFindBar(withReplace: false);
    }

    private void OnOpenReplaceClick(object? sender, RoutedEventArgs e)
    {
        ShowFindBar(withReplace: true);
    }

    private void ShowFindBar(bool withReplace)
    {
        if (SettingsWorkspaceScroll.IsVisible)
            ShowEditorView();

        FindReplacePanel.IsVisible = true;
        ReplaceRowGrid.IsVisible = withReplace;
        FindStatusLabel.IsVisible = false;

        int s = Math.Min(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        int end = Math.Max(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        string text = EditorTextBox.Text ?? string.Empty;
        if (end > s && end - s <= 120 && end <= text.Length)
        {
            FindQueryBox.Text = text[s..end];
        }

        FindQueryBox.Focus();
        FindQueryBox.SelectAll();
    }

    private void OnToggleReplaceRowClick(object? sender, RoutedEventArgs e)
    {
        ReplaceRowGrid.IsVisible = !ReplaceRowGrid.IsVisible;
    }

    private void OnCloseFindClick(object? sender, RoutedEventArgs e)
    {
        FindReplacePanel.IsVisible = false;
        EditorTextBox.Focus();
    }

    private void OnFindQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindInEditor(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e) => FindInEditor(forward: true);
    private void OnFindPrevClick(object? sender, RoutedEventArgs e) => FindInEditor(forward: false);

    private bool FindInEditor(bool forward)
    {
        string query = FindQueryBox.Text ?? string.Empty;
        string text = EditorTextBox.Text ?? string.Empty;

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            FindStatusLabel.Text = "No matches found";
            FindStatusLabel.IsVisible = true;
            return false;
        }

        var comparison = MatchCaseToggle.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int matchIndex;
        if (forward)
        {
            int startFrom = Math.Max(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
            if (startFrom > text.Length) startFrom = 0;
            matchIndex = text.IndexOf(query, startFrom, comparison);
            if (matchIndex < 0 && startFrom > 0)
                matchIndex = text.IndexOf(query, 0, comparison); // Wrap around
        }
        else
        {
            int startFrom = Math.Min(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd) - 1;
            if (startFrom < 0) startFrom = text.Length - 1;
            matchIndex = startFrom >= 0 ? text.LastIndexOf(query, startFrom, comparison) : -1;
            if (matchIndex < 0 && text.Length > 0)
                matchIndex = text.LastIndexOf(query, text.Length - 1, comparison);
        }

        if (matchIndex >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.SelectionStart = matchIndex;
            EditorTextBox.SelectionEnd = matchIndex + query.Length;
            EditorTextBox.CaretIndex = matchIndex + query.Length;

            int totalMatches = CountOccurrences(text, query, comparison);
            FindStatusLabel.Text = $"{totalMatches} {(totalMatches == 1 ? "match" : "matches")} in document";
            FindStatusLabel.IsVisible = true;
            return true;
        }

        FindStatusLabel.Text = $"Cannot find \"{query}\"";
        FindStatusLabel.IsVisible = true;
        return false;
    }

    private void OnReplaceSingleClick(object? sender, RoutedEventArgs e)
    {
        string query = FindQueryBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return;

        string replacement = ReplaceWithBox.Text ?? string.Empty;
        string text = EditorTextBox.Text ?? string.Empty;
        var comparison = MatchCaseToggle.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int s = Math.Min(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        int end = Math.Max(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        if (end > s && end <= text.Length && string.Equals(text[s..end], query, comparison))
        {
            EditorTextBox.Text = text[..s] + replacement + text[end..];
            EditorTextBox.SelectionStart = s;
            EditorTextBox.SelectionEnd = s + replacement.Length;
            EditorTextBox.CaretIndex = s + replacement.Length;
        }

        FindInEditor(forward: true);
    }

    private void OnReplaceAllClick(object? sender, RoutedEventArgs e)
    {
        string query = FindQueryBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return;

        string replacement = ReplaceWithBox.Text ?? string.Empty;
        string text = EditorTextBox.Text ?? string.Empty;
        var comparison = MatchCaseToggle.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int count = CountOccurrences(text, query, comparison);
        if (count == 0)
        {
            FindStatusLabel.Text = $"Cannot find \"{query}\"";
            FindStatusLabel.IsVisible = true;
            return;
        }

        EditorTextBox.Text = text.Replace(query, replacement, comparison);
        FindStatusLabel.Text = $"Replaced {count} {(count == 1 ? "occurrence" : "occurrences")}";
        FindStatusLabel.IsVisible = true;
    }

    private static int CountOccurrences(string text, string query, StringComparison comparison)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(query, idx, comparison)) >= 0)
        {
            count++;
            idx += query.Length;
        }
        return count;
    }

    private void OnOpenGoToLineClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null)
            return;

        int totalLines = 1 + _activeTab.Content.Count(c => c == '\n');
        GoToLineHintLabel.Text = $"Line number (1 – {totalLines}):";
        GoToLineInputBox.Text = _activeTab.CaretLine.ToString();
        ShowModalCard(GoToLineDialogCard);
        Dispatcher.UIThread.Post(() =>
        {
            GoToLineInputBox.Focus();
            GoToLineInputBox.SelectAll();
        });
    }

    private void OnGoToLineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirmGoToLineClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnConfirmGoToLineClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null)
            return;

        string text = EditorTextBox.Text ?? string.Empty;
        int totalLines = 1 + text.Count(c => c == '\n');
        if (!int.TryParse(GoToLineInputBox.Text, out int targetLine))
            targetLine = 1;

        targetLine = Math.Clamp(targetLine, 1, totalLines);
        int currentLine = 1;
        int charIndex = 0;
        while (currentLine < targetLine && charIndex < text.Length)
        {
            if (text[charIndex] == '\n')
                currentLine++;
            charIndex++;
        }

        HideAllModals();
        EditorTextBox.Focus();
        EditorTextBox.CaretIndex = charIndex;
        EditorTextBox.SelectionStart = charIndex;
        EditorTextBox.SelectionEnd = charIndex;
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => AdjustZoom(+10);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => AdjustZoom(-10);
    private void OnZoomResetClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        _activeTab.ZoomPercent = 100;
        UpdateEditorZoomFontSize();
        UpdateStatusBarAndBadges();
    }

    private void AdjustZoom(int delta)
    {
        if (_activeTab is null) return;
        _activeTab.ZoomPercent += delta;
        UpdateEditorZoomFontSize();
        UpdateStatusBarAndBadges();
    }

    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            AdjustZoom(e.Delta.Y > 0 ? +10 : -10);
            e.Handled = true;
        }
    }

    private void OnToggleWordWrapClick(object? sender, RoutedEventArgs e)
    {
        _settings.WordWrap = !_settings.WordWrap;
        SettingsService.Save(_settings);
        ApplyThemeAndSettingsToUi();
    }

    private void OnToggleStatusBarClick(object? sender, RoutedEventArgs e)
    {
        _settings.ShowStatusBar = !_settings.ShowStatusBar;
        SettingsService.Save(_settings);
        ApplyThemeAndSettingsToUi();
    }

    private void OnToggleLineEndingClick(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        _activeTab.LineEnding = _activeTab.LineEnding == "CRLF" ? "LF" : "CRLF";
        UpdateStatusBarAndBadges();
    }

    // =======================================================================
    // SETTINGS VIEW NAVIGATION & COLLAPSIBLE PANE HANDLERS (ALL 5 SECTIONS)
    // =======================================================================

    private bool _isSettingsPaneCollapsed;

    public void ShowSettingsView()
    {
        EditorWorkspaceGrid.IsVisible = false;
        SettingsWorkspaceScroll.IsVisible = true;
        SettingsBackBtn.IsVisible = true;
        SettingsTitleLabel.IsVisible = true;
        TabStripScroller.IsVisible = false;
        NewTabBtn.IsVisible = false;

        // Hide titlebar bottom seam so Titlebar fuses seamlessly into Left Collapsible Pane!
        LeftTitleSeamBorder.IsVisible = false;
        RightTitleSeamBorder.IsVisible = false;
        CaptionTitleSeamBorder.IsVisible = false;

        SyncSettingsControlsFromModel();
        UpdateWindowTitle();
    }

    public void ShowEditorView()
    {
        SettingsWorkspaceScroll.IsVisible = false;
        EditorWorkspaceGrid.IsVisible = true;
        SettingsBackBtn.IsVisible = false;
        SettingsTitleLabel.IsVisible = false;
        TabStripScroller.IsVisible = true;
        NewTabBtn.IsVisible = true;

        LeftTitleSeamBorder.IsVisible = true;
        RightTitleSeamBorder.IsVisible = true;
        CaptionTitleSeamBorder.IsVisible = true;

        UpdateWindowTitle();
        EditorTextBox.Focus();
    }

    public void SetSettingsPaneCollapsed(bool collapsed)
    {
        _isSettingsPaneCollapsed = collapsed;
        SettingsNavPane.Width = collapsed ? 52 : 232;

        bool showLabels = !collapsed;
        SettingsPaneHeaderText.IsVisible = showLabels;
        NavAppModeLabel.IsVisible = showLabels;
        NavAccentLabel.IsVisible = showLabels;
        NavPadColorLabel.IsVisible = showLabels;
        NavSecurityLabel.IsVisible = showLabels;
        NavAboutLabel.IsVisible = showLabels;

        ToolTip.SetTip(
            ToggleSettingsPaneBtn,
            collapsed ? "Expand navigation pane" : "Collapse navigation pane");
    }

    private void OnToggleSettingsPaneClick(object? sender, RoutedEventArgs e)
        => SetSettingsPaneCollapsed(!_isSettingsPaneCollapsed);

    public void SelectSettingsSection(int sectionIndex)
    {
        SetSelectedClass(NavAppModeBtn, sectionIndex == 0);
        SetSelectedClass(NavAccentBtn, sectionIndex == 1);
        SetSelectedClass(NavPadColorBtn, sectionIndex == 2);
        SetSelectedClass(NavSecurityBtn, sectionIndex == 3);
        SetSelectedClass(NavAboutBtn, sectionIndex == 4);

        NavAppModeIndicator.IsVisible = sectionIndex == 0;
        NavAccentIndicator.IsVisible = sectionIndex == 1;
        NavPadColorIndicator.IsVisible = sectionIndex == 2;
        NavSecurityIndicator.IsVisible = sectionIndex == 3;
        NavAboutIndicator.IsVisible = sectionIndex == 4;

        SettingsPageAppMode.IsVisible = sectionIndex == 0;
        SettingsPageAccent.IsVisible = sectionIndex == 1;
        SettingsPagePadColor.IsVisible = sectionIndex == 2;
        SettingsPageSecurity.IsVisible = sectionIndex == 3;
        SettingsPageAbout.IsVisible = sectionIndex == 4;

        SettingsContentScroller.Offset = Vector.Zero;
    }

    private void OnSettingsNavAppModeClick(object? sender, RoutedEventArgs e) => SelectSettingsSection(0);
    private void OnSettingsNavAccentClick(object? sender, RoutedEventArgs e) => SelectSettingsSection(1);
    private void OnSettingsNavPadColorClick(object? sender, RoutedEventArgs e) => SelectSettingsSection(2);
    private void OnSettingsNavSecurityClick(object? sender, RoutedEventArgs e) => SelectSettingsSection(3);
    private void OnSettingsNavAboutClick(object? sender, RoutedEventArgs e) => SelectSettingsSection(4);

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e) => ShowSettingsView();
    private void OnSettingsBackClick(object? sender, RoutedEventArgs e) => ShowEditorView();

    // 1. App Mode: Dark / Light / Sync
    private void OnAppModeDarkClick(object? sender, RoutedEventArgs e) => SetAppMode(AppThemeMode.Dark);
    private void OnAppModeLightClick(object? sender, RoutedEventArgs e) => SetAppMode(AppThemeMode.Light);
    private void OnAppModeSyncClick(object? sender, RoutedEventArgs e) => SetAppMode(AppThemeMode.Sync);

    public void SetAppMode(AppThemeMode mode)
    {
        _settings.ThemeMode = mode;
        SettingsService.Save(_settings);
        ApplyThemeAndSettingsToUi();
    }

    // 2. Accent: Sync / Red / Purple / Matcha / Beige / Orange
    private void OnAccentSyncClick(object? sender, RoutedEventArgs e) => SetAccent(AccentPreset.Sync);
    private void OnAccentRedClick(object? sender, RoutedEventArgs e) => SetAccent(AccentPreset.Red);
    private void OnAccentPurpleClick(object? sender, RoutedEventArgs e) => SetAccent(AccentPreset.Purple);
    private void OnAccentMatchaClick(object? sender, RoutedEventArgs e) => SetAccent(AccentPreset.Matcha);
    private void OnAccentBeigeClick(object? sender, RoutedEventArgs e) => SetAccent(AccentPreset.Beige);
    private void OnAccentOrangeClick(object? sender, RoutedEventArgs e) => SetAccent(AccentPreset.Orange);

    public void SetAccent(AccentPreset preset)
    {
        _settings.Accent = preset;
        SettingsService.Save(_settings);
        ApplyThemeAndSettingsToUi();
    }

    // 3. Pad Color: Accent / Yellow / White
    private void OnPadAccentClick(object? sender, RoutedEventArgs e) => SetPadColor(PadColorPreset.Accent);
    private void OnPadYellowClick(object? sender, RoutedEventArgs e) => SetPadColor(PadColorPreset.Yellow);
    private void OnPadWhiteClick(object? sender, RoutedEventArgs e) => SetPadColor(PadColorPreset.White);

    public void SetPadColor(PadColorPreset padColor)
    {
        _settings.PadColor = padColor;
        SettingsService.Save(_settings);
        ApplyThemeAndSettingsToUi();
    }

    private void OnAdaptDarkPadToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSync) return;
        _settings.AdaptPadInDarkMode = AdaptDarkPadSwitch.IsChecked == true;
        SettingsService.Save(_settings);
        ApplyThemeAndSettingsToUi();
    }

    private void OnFontFamilySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync) return;
        if (FontFamilyCombo.SelectedItem is ComboBoxItem { Tag: string ff })
        {
            _settings.EditorFontFamily = ff;
            SettingsService.Save(_settings);
            ApplyThemeAndSettingsToUi();
        }
    }

    private void OnFontSizeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync) return;
        if (FontSizeCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), out double sz))
        {
            _settings.EditorFontSize = sz;
            SettingsService.Save(_settings);
            ApplyThemeAndSettingsToUi();
        }
    }

    // 4. Security: Master Password & Policy
    private void OnPolicyUseMasterClick(object? sender, RoutedEventArgs e)
    {
        _settings.PasswordPolicy = SecureSavePasswordPolicy.UseMasterPassword;
        SettingsService.Save(_settings);
        SyncSettingsControlsFromModel();

        if (!_settings.HasMasterPassword)
        {
            OpenMasterPasswordModal(MasterDialogMode.SetNew);
        }
        else
        {
            ShowToast("Policy set: Use Master Password (with per-file option)");
        }
    }

    private void OnPolicyAskEveryTimeClick(object? sender, RoutedEventArgs e)
    {
        _settings.PasswordPolicy = SecureSavePasswordPolicy.AskEveryTime;
        SettingsService.Save(_settings);
        SyncSettingsControlsFromModel();
        ShowToast("Policy set: Ask to create a password every time you save a Secure file");
    }

    private void OnConfigureMasterPasswordClick(object? sender, RoutedEventArgs e)
    {
        OpenMasterPasswordModal(_settings.HasMasterPassword ? MasterDialogMode.ChangeExisting : MasterDialogMode.SetNew);
    }

    private void OnRemoveMasterPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (_settings.HasMasterPassword)
            OpenMasterPasswordModal(MasterDialogMode.RemoveExisting);
    }

    private void OpenMasterPasswordModal(MasterDialogMode mode)
    {
        _masterDialogMode = mode;
        MasterCurrentPwBox.Text = string.Empty;
        MasterNewPwBox.Text = string.Empty;
        MasterConfirmPwBox.Text = string.Empty;
        MasterDialogErrorText.IsVisible = false;
        UpdatePasswordStrengthUi(string.Empty, MasterPwStrengthBar, MasterPwStrengthText);

        switch (mode)
        {
            case MasterDialogMode.SetNew:
                MasterDialogTitle.Text = "Set Default Master Password";
                MasterDialogSubtitle.Text = "Encrypted locally with Windows DPAPI + PBKDF2-HMAC-SHA512 (600,000 iterations).";
                MasterCurrentPwBox.IsVisible = false;
                MasterNewPwPanel.IsVisible = true;
                MasterDialogSubmitBtn.Content = "Save Master Password";
                break;

            case MasterDialogMode.ChangeExisting:
                MasterDialogTitle.Text = "Change Master Password";
                MasterDialogSubtitle.Text = "Verify your current Master Password, then enter your new Master Password.";
                MasterCurrentPwBox.IsVisible = true;
                MasterNewPwPanel.IsVisible = true;
                MasterDialogSubmitBtn.Content = "Update Master Password";
                break;

            case MasterDialogMode.RemoveExisting:
                MasterDialogTitle.Text = "Remove Master Password";
                MasterDialogSubtitle.Text = "Enter your current Master Password to remove it. Future Secure saves will ask for a password each time.";
                MasterCurrentPwBox.IsVisible = true;
                MasterNewPwPanel.IsVisible = false;
                MasterDialogSubmitBtn.Content = "Remove Master Password";
                break;
        }

        ShowModalCard(MasterPwDialogCard);
    }

    private void OnMasterNewPwTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePasswordStrengthUi(MasterNewPwBox.Text, MasterPwStrengthBar, MasterPwStrengthText);
    }

    private void OnSubmitMasterPwDialogClick(object? sender, RoutedEventArgs e)
    {
        if (_masterDialogMode != MasterDialogMode.SetNew)
        {
            string curr = MasterCurrentPwBox.Text ?? string.Empty;
            if (!KnightCryptoService.VerifyMasterPassword(curr, _settings.MasterPasswordSaltBase64, _settings.MasterPasswordVerifierBase64))
            {
                MasterDialogErrorText.Text = "Current Master Password is incorrect.";
                MasterDialogErrorText.IsVisible = true;
                return;
            }
        }

        if (_masterDialogMode == MasterDialogMode.RemoveExisting)
        {
            _settings.MasterPasswordSaltBase64 = null;
            _settings.MasterPasswordVerifierBase64 = null;
            _settings.MasterPasswordProtectedBlobBase64 = null;
            SettingsService.Save(_settings);
            HideAllModals();
            SyncSettingsControlsFromModel();
            ShowToast("Master Password removed");
            return;
        }

        string newPw = MasterNewPwBox.Text ?? string.Empty;
        string confirmPw = MasterConfirmPwBox.Text ?? string.Empty;

        if (newPw.Length < 4)
        {
            MasterDialogErrorText.Text = "Master Password must be at least 4 characters.";
            MasterDialogErrorText.IsVisible = true;
            return;
        }

        if (!string.Equals(newPw, confirmPw, StringComparison.Ordinal))
        {
            MasterDialogErrorText.Text = "New Master Passwords do not match.";
            MasterDialogErrorText.IsVisible = true;
            return;
        }

        var (saltB64, verifierB64, protectedB64) = KnightCryptoService.CreateMasterPasswordCredentials(newPw);
        _settings.MasterPasswordSaltBase64 = saltB64;
        _settings.MasterPasswordVerifierBase64 = verifierB64;
        _settings.MasterPasswordProtectedBlobBase64 = protectedB64;
        SettingsService.Save(_settings);

        MasterCurrentPwBox.Text = string.Empty;
        MasterNewPwBox.Text = string.Empty;
        MasterConfirmPwBox.Text = string.Empty;

        HideAllModals();
        SyncSettingsControlsFromModel();
        ShowToast("Master Password configured securely");
    }

    private void OnRegisterFileAssocClick(object? sender, RoutedEventArgs e)
    {
        var (ok, msg) = WindowsNativeInterop.RegisterKwNoteAssociation();
        AssocStatusText.Text = msg;
        ShowToast(ok ? "Registered .kwnote file association!" : msg);
    }

    private void OnAboutLogoGitHubClick(object? sender, RoutedEventArgs e)
    {
        const string repoUrl = "https://github.com/iamkrisstan2000/KnightWin-Notes";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = repoUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            ShowToast($"GitHub: {repoUrl}");
        }
    }

    // =======================================================================
    // MODAL HELPERS, GLOBAL SHORTCUTS & TOASTS
    // =======================================================================

    private void ShowModalCard(Control targetCard)
    {
        SaveModeDialogCard.IsVisible = targetCard == SaveModeDialogCard;
        SecureSavePasswordCard.IsVisible = targetCard == SecureSavePasswordCard;
        UnlockNoteDialogCard.IsVisible = targetCard == UnlockNoteDialogCard;
        MasterPwDialogCard.IsVisible = targetCard == MasterPwDialogCard;
        GoToLineDialogCard.IsVisible = targetCard == GoToLineDialogCard;
        UnsavedDialogCard.IsVisible = targetCard == UnsavedDialogCard;
        ModalOverlay.IsVisible = true;
    }

    private void HideAllModals()
    {
        ModalOverlay.IsVisible = false;
        SaveModeDialogCard.IsVisible = false;
        SecureSavePasswordCard.IsVisible = false;
        UnlockNoteDialogCard.IsVisible = false;
        MasterPwDialogCard.IsVisible = false;
        GoToLineDialogCard.IsVisible = false;
        UnsavedDialogCard.IsVisible = false;

        // Scrub password UI fields immediately whenever any modal closes
        SecureNewPwBox.Text = string.Empty;
        SecureConfirmPwBox.Text = string.Empty;
        UnlockPwBox.Text = string.Empty;
        MasterCurrentPwBox.Text = string.Empty;
        MasterNewPwBox.Text = string.Empty;
        MasterConfirmPwBox.Text = string.Empty;
    }

    private void OnCancelModalClick(object? sender, RoutedEventArgs e)
    {
        _pendingCloseTab = null;
        _pendingCloseWholeWindow = false;
        if (_pendingUnlockBytes is not null)
        {
            CryptographicOperations.ZeroMemory(_pendingUnlockBytes);
            _pendingUnlockBytes = null;
        }
        _pendingUnlockFilePath = null;
        HideAllModals();
        if (!SettingsWorkspaceScroll.IsVisible)
            EditorTextBox.Focus();
    }

    private static void UpdatePasswordStrengthUi(string? pw, ProgressBar bar, TextBlock label)
    {
        var (score, text, hex) = KnightCryptoService.EvaluatePasswordStrength(pw);
        bar.Value = score;
        bar.Foreground = new SolidColorBrush(Color.Parse(hex));
        label.Text = text;
    }

    private async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastMessageText.Text = message;
        ToastBanner.IsVisible = true;

        try
        {
            await Task.Delay(3200, cts.Token);
            ToastBanner.IsVisible = false;
        }
        catch (TaskCanceledException)
        {
            // Replaced by newer toast
        }
    }

    private async void OnPreviewWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ModalOverlay.IsVisible)
            {
                OnCancelModalClick(sender, e);
                e.Handled = true;
                return;
            }
            if (FindReplacePanel.IsVisible)
            {
                OnCloseFindClick(sender, e);
                e.Handled = true;
                return;
            }
            if (SettingsWorkspaceScroll.IsVisible)
            {
                ShowEditorView();
                e.Handled = true;
                return;
            }
        }

        if (ModalOverlay.IsVisible)
            return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && !shift && (e.Key == Key.N || e.Key == Key.T))
        {
            OnNewTabClick(sender, e);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.N)
        {
            OnNewWindowClick(sender, e);
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.O)
        {
            OnOpenFileClick(sender, e);
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.S)
        {
            await TriggerSmartSaveAsync();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.S)
        {
            OnSaveAsChoiceClick(sender, e);
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.W)
        {
            OnCloseActiveTabClick(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab)
        {
            if (Tabs.Count > 1 && _activeTab is not null)
            {
                int idx = Tabs.IndexOf(_activeTab);
                int next = shift ? (idx - 1 + Tabs.Count) % Tabs.Count : (idx + 1) % Tabs.Count;
                SelectTab(Tabs[next]);
            }
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.F)
        {
            ShowFindBar(withReplace: false);
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.H)
        {
            ShowFindBar(withReplace: true);
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            if (!FindReplacePanel.IsVisible)
                ShowFindBar(withReplace: false);
            else
                FindInEditor(forward: !shift);
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.G)
        {
            OnOpenGoToLineClick(sender, e);
            e.Handled = true;
        }
        else if (!ctrl && !shift && e.Key == Key.F5 && !SettingsWorkspaceScroll.IsVisible)
        {
            OnInsertTimeDateClick(sender, e);
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            AdjustZoom(+10);
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            AdjustZoom(-10);
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            OnZoomResetClick(sender, e);
            e.Handled = true;
        }
    }
}

