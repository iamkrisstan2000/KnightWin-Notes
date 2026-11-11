using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using KnightWinNotes.Models;

namespace KnightWinNotes.Services;

public sealed record ResolvedThemePalette(
    bool IsDark,
    Color TitleBarAcrylicColor,
    Color ChromeBarAcrylicColor,
    Color AccentColor,
    Color AccentHoverColor,
    Color AccentOnFillTextColor,
    Color PadBackgroundColor,
    Color PadForegroundColor);

public static class FluentPaletteEngine
{
    public static ResolvedThemePalette Apply(
        Application app,
        AppSettings settings,
        IntPtr hwnd = default,
        bool enableDwmAcrylicFallback = false)
    {
        bool isDark = settings.ThemeMode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => WindowsNativeInterop.GetSystemIsDarkMode()
        };

        app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        Color accent = ResolveAccent(settings.Accent, isDark);
        Color accentHover = isDark ? Lighten(accent, 0.12) : Darken(accent, 0.10);
        Color accentPressed = isDark ? Darken(accent, 0.08) : Darken(accent, 0.18);

        // WinUI 3 TextOnAccentFillColorPrimary contrast rule
        double lum = RelativeLuminance(accent);
        Color onAccentText = lum > 0.30 ? Color.Parse("#101216") : Colors.White;

        // =====================================================================
        // EXACT ACRYLIC TRANSLUCENCY SPECIFICATION:
        //   1. Titlebar translucency:
        //      - Dark mode : 12% translucent -> 88% tint opacity (Alpha = 224)
        //      - Light mode:  6% translucent -> 94% tint opacity (Alpha = 240)
        //   2. Active Tab + Command Bar + Status Bar translucency:
        //      - Dark mode :  5% translucent -> 95% tint opacity (Alpha = 242)
        //      - Light mode:  2% translucent -> 98% tint opacity (Alpha = 250)
        // =====================================================================
        double titleTranslucency = isDark ? 0.12 : 0.06;
        double barTranslucency   = isDark ? 0.05 : 0.02;

        double titleOpacity = 1.0 - titleTranslucency; // 0.88 Dark, 0.94 Light
        double barOpacity   = 1.0 - barTranslucency;   // 0.95 Dark, 0.98 Light

        Color titleBaseRgb = isDark ? Color.Parse("#18191D") : Color.Parse("#EAECEF");
        Color barBaseRgb   = isDark ? Color.Parse("#25262B") : Color.Parse("#F8F9FC");

        Color titleBarAcrylic  = WithOpacity(titleBaseRgb, titleOpacity);
        Color chromeBarAcrylic = WithOpacity(barBaseRgb, barOpacity);

        // Porter-Duff SrcOver compensation for ActiveTab (which renders inside the Titlebar row)
        // so that (Titlebar + ActiveTab) produces the EXACT same 5% / 2% translucency & RGB as CommandBar!
        Color tabActiveCompensated = ComputeOverlayForTargetComposite(
            underColor: titleBaseRgb,
            underAlpha: titleOpacity,
            targetColor: barBaseRgb,
            targetAlpha: barOpacity);

        // Subtle hover overlay over titlebar
        Color tabHoverBg = isDark
            ? Color.FromArgb(28, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0);

        // Sleek Windows 11 Notepad Command Bar Menu/Icon Pill Hover & Open Tints
        Color cmdBarHoverBg = isDark
            ? Color.FromArgb(17, 255, 255, 255)
            : Color.FromArgb(12, 0, 0, 0);
        Color cmdBarActiveBg = isDark
            ? Color.FromArgb(24, 255, 255, 255)
            : Color.FromArgb(18, 0, 0, 0);

        Color cardBg       = isDark ? Color.FromArgb(242, 39, 40, 45) : Color.FromArgb(250, 255, 255, 255);
        Color cardHoverBg  = isDark ? Color.FromArgb(248, 46, 48, 54) : Color.FromArgb(252, 246, 247, 250);
        Color cardBorder    = isDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        Color subtleBorder  = isDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
        Color titleBarSeam  = isDark ? Color.FromArgb(27, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);

        Color textPrimary   = isDark ? Color.Parse("#F3F4F6") : Color.Parse("#18191C");
        Color textSecondary = isDark ? Color.Parse("#B4B8C2") : Color.Parse("#5A5F6B");
        Color textMuted     = isDark ? Color.Parse("#858A96") : Color.Parse("#7B818F");

        Color accentSubtleBg = isDark
            ? Blend(Color.Parse("#27282D"), accent, 0.18)
            : Blend(Colors.White, accent, 0.12);

        // Resolve Notepad Writing Sheet (Pad Color: Accent / Yellow / White — 100% crisp opaque for reading comfort)
        var (padBg, padFg, padBorder) = ResolvePadColors(
            settings.PadColor,
            accent,
            isDark,
            settings.AdaptPadInDarkMode);

        Color padSelection  = Color.FromArgb(95, accent.R, accent.G, accent.B);
        Color dialogOverlay = isDark ? Color.FromArgb(155, 8, 9, 12) : Color.FromArgb(110, 15, 18, 24);
        Color dialogSurface = isDark ? Color.Parse("#2B2C32") : Color.Parse("#FFFFFF");
        Color modifiedDot   = isDark ? Color.Parse("#FFD43B") : Color.Parse("#F59E0B");

        // Windows 11 New Context Menu / Dropdown Flyout Slight Acrylic Palette (Transient Acrylic, NOT Mica)
        Color menuFlyoutAcrylicBg = isDark
            ? Color.FromArgb(218, 38, 40, 46)     // ~14.5% slight Acrylic translucency in Dark mode
            : Color.FromArgb(232, 248, 249, 252); // ~9.0% slight Acrylic translucency in Light mode
        Color menuFlyoutFallbackSolid = isDark ? Color.Parse("#26282E") : Color.Parse("#F8F9FC");
        Color menuFlyoutBorder = isDark
            ? Color.FromArgb(228, 62, 65, 74)     // Crisp pre-composited Windows 11 specular glass rim (Dark)
            : Color.FromArgb(236, 214, 218, 226); // Crisp pre-composited Windows 11 frosted glass rim (Light)
        Color menuItemHoverBg = isDark
            ? Color.FromArgb(26, 255, 255, 255)
            : Color.FromArgb(15, 0, 0, 0);
        Color menuItemPressedBg = isDark
            ? Color.FromArgb(16, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0);
        Color menuSeparator = isDark
            ? Color.FromArgb(28, 255, 255, 255)
            : Color.FromArgb(18, 0, 0, 0);

        var res = app.Resources;
        SetBrush(res, "FallbackSolidChromeBg", titleBaseRgb);
        SetBrush(res, "FallbackPopupBg", menuFlyoutFallbackSolid);
        SetBrush(res, "WindowChromeBg", chromeBarAcrylic);
        SetBrush(res, "TabRailBg", titleBarAcrylic);         // Titlebar: 12% Dark / 6% Light translucency
        SetBrush(res, "TabActiveBg", tabActiveCompensated);  // Active Tab: composites to exact 5% Dark / 2% Light translucency
        SetBrush(res, "TabHoverBg", tabHoverBg);
        SetBrush(res, "CommandBarHoverBg", cmdBarHoverBg);
        SetBrush(res, "CommandBarActiveBg", cmdBarActiveBg);
        SetBrush(res, "MenuBarBg", chromeBarAcrylic);        // Command Bar: 5% Dark / 2% Light translucency
        SetBrush(res, "StatusBarBg", chromeBarAcrylic);      // Status Bar:  5% Dark / 2% Light translucency

        // Override FluentTheme MenuFlyout / Dropdown resources dynamically
        SetBrush(res, "MenuFlyoutPresenterBackground", menuFlyoutAcrylicBg);
        SetBrush(res, "MenuFlyoutPresenterBorderBrush", menuFlyoutBorder);
        SetBrush(res, "MenuFlyoutItemBackground", Colors.Transparent);
        SetBrush(res, "MenuFlyoutItemBackgroundPointerOver", menuItemHoverBg);
        SetBrush(res, "MenuFlyoutItemBackgroundPressed", menuItemPressedBg);
        SetBrush(res, "MenuFlyoutItemForeground", textPrimary);
        SetBrush(res, "MenuFlyoutItemForegroundPointerOver", textPrimary);
        SetBrush(res, "MenuFlyoutItemForegroundPressed", textPrimary);
        SetBrush(res, "MenuFlyoutItemKeyboardAcceleratorTextForeground", textMuted);
        SetBrush(res, "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", textSecondary);
        SetBrush(res, "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", textMuted);
        SetBrush(res, "MenuFlyoutSubItemChevron", textSecondary);
        SetBrush(res, "MenuFlyoutSubItemChevronPointerOver", textPrimary);
        SetBrush(res, "MenuSeparatorBrush", menuSeparator);

        SetBrush(res, "CardBg", cardBg);
        SetBrush(res, "CardHoverBg", cardHoverBg);
        SetBrush(res, "CardBorderBrush", cardBorder);
        SetBrush(res, "SubtleBorderBrush", subtleBorder);
        SetBrush(res, "TitleBarSeamBrush", titleBarSeam);
        SetBrush(res, "UnsavedYellowDotBrush", modifiedDot);

        SetBrush(res, "TextPrimary", textPrimary);
        SetBrush(res, "TextSecondary", textSecondary);
        SetBrush(res, "TextMuted", textMuted);

        SetBrush(res, "AccentPrimary", accent);
        SetBrush(res, "AccentHover", accentHover);
        SetBrush(res, "AccentPressed", accentPressed);
        SetBrush(res, "AccentSubtleBg", accentSubtleBg);
        SetBrush(res, "AccentOnFillText", onAccentText);

        SetBrush(res, "PadBackground", padBg);
        SetBrush(res, "PadForeground", padFg);
        SetBrush(res, "PadBorderBrush", padBorder);
        SetBrush(res, "PadSelectionBrush", padSelection);

        SetBrush(res, "DialogOverlayBg", dialogOverlay);
        SetBrush(res, "DialogSurfaceBg", dialogSurface);

        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Lighten(accent, 0.12);
        res["SystemAccentColorLight2"] = Lighten(accent, 0.24);
        res["SystemAccentColorDark1"] = Darken(accent, 0.12);
        res["SystemAccentColorDark2"] = Darken(accent, 0.24);

        if (hwnd != IntPtr.Zero)
        {
            WindowsNativeInterop.ApplyFluentWindowChrome(hwnd, isDark, enableDwmAcrylicFallback);
        }

        return new ResolvedThemePalette(
            isDark,
            titleBarAcrylic,
            chromeBarAcrylic,
            accent,
            accentHover,
            onAccentText,
            padBg,
            padFg);
    }

    private static Color WithOpacity(Color rgb, double opacity)
    {
        byte a = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
        return Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// Solves Porter-Duff SrcOver so painting returned color over (underColor, underAlpha)
    /// produces exact (targetColor, targetAlpha) over any Acrylic backdrop pixel.
    /// </summary>
    private static Color ComputeOverlayForTargetComposite(
        Color underColor,
        double underAlpha,
        Color targetColor,
        double targetAlpha)
    {
        double underTrans = 1.0 - underAlpha;
        double targetTrans = 1.0 - targetAlpha;

        if (underTrans <= 0.0001 || targetTrans >= underTrans)
            return WithOpacity(targetColor, targetAlpha);

        // (1 - overlayAlpha) * underTrans = targetTrans
        double overlayAlpha = 1.0 - (targetTrans / underTrans);

        static byte SolveChannel(byte cUnder, double aUnder, byte cTarget, double aTarget, double aOver)
        {
            double num = (aTarget * cTarget) - ((1.0 - aOver) * aUnder * cUnder);
            double raw = num / aOver;
            return (byte)Math.Clamp((int)Math.Round(raw), 0, 255);
        }

        byte r = SolveChannel(underColor.R, underAlpha, targetColor.R, targetAlpha, overlayAlpha);
        byte g = SolveChannel(underColor.G, underAlpha, targetColor.G, targetAlpha, overlayAlpha);
        byte b = SolveChannel(underColor.B, underAlpha, targetColor.B, targetAlpha, overlayAlpha);
        byte a = (byte)Math.Clamp((int)Math.Round(overlayAlpha * 255.0), 0, 255);

        return Color.FromArgb(a, r, g, b);
    }

    public static Color ResolveAccent(AccentPreset preset, bool isDark)
    {
        return preset switch
        {
            AccentPreset.Red    => isDark ? Color.Parse("#FF5C61") : Color.Parse("#D13438"),
            AccentPreset.Purple => isDark ? Color.Parse("#A584FF") : Color.Parse("#7149D6"),
            AccentPreset.Matcha => isDark ? Color.Parse("#87BD6B") : Color.Parse("#558742"),
            AccentPreset.Beige  => isDark ? Color.Parse("#D7BC96") : Color.Parse("#997B52"),
            AccentPreset.Orange => isDark ? Color.Parse("#FF8F40") : Color.Parse("#DA5B0B"),
            _ => AdjustSystemAccentForContrast(WindowsNativeInterop.GetSystemAccentColor(), isDark)
        };
    }

    private static (Color PadBg, Color PadFg, Color PadBorder) ResolvePadColors(
        PadColorPreset padPreset,
        Color accent,
        bool isDark,
        bool adaptInDarkMode)
    {
        bool useDarkSheet = isDark && adaptInDarkMode;

        if (!useDarkSheet)
        {
            return padPreset switch
            {
                PadColorPreset.Yellow => (
                    Color.Parse("#FFF9D4"),
                    Color.Parse("#241F10"),
                    Color.FromArgb(45, 180, 148, 30)),

                PadColorPreset.Accent => (
                    Blend(Colors.White, accent, 0.11),
                    Darken(accent, 0.72),
                    Color.FromArgb(55, accent.R, accent.G, accent.B)),

                _ => (
                    Color.Parse("#FFFFFF"),
                    Color.Parse("#1A1B1E"),
                    Color.FromArgb(20, 0, 0, 0))
            };
        }

        return padPreset switch
        {
            PadColorPreset.Yellow => (
                Color.Parse("#2B2617"),
                Color.Parse("#F7F1DC"),
                Color.FromArgb(65, 224, 190, 78)),

            PadColorPreset.Accent => (
                Blend(Color.Parse("#1F2024"), accent, 0.16),
                Color.Parse("#F3F5F8"),
                Color.FromArgb(70, accent.R, accent.G, accent.B)),

            _ => (
                Color.Parse("#212226"),
                Color.Parse("#EFF1F5"),
                Color.FromArgb(24, 255, 255, 255))
        };
    }

    private static Color AdjustSystemAccentForContrast(Color raw, bool isDark)
    {
        double lum = RelativeLuminance(raw);
        if (isDark && lum < 0.15)
            return Lighten(raw, 0.30);
        if (!isDark && lum > 0.65)
            return Darken(raw, 0.25);
        return raw;
    }

    private static void SetBrush(IResourceDictionary dict, string key, Color color)
    {
        dict[key] = new SolidColorBrush(color);
    }

    public static Color Blend(Color baseColor, Color tint, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        byte r = (byte)Math.Round(baseColor.R * (1 - t) + tint.R * t);
        byte g = (byte)Math.Round(baseColor.G * (1 - t) + tint.G * t);
        byte b = (byte)Math.Round(baseColor.B * (1 - t) + tint.B * t);
        return Color.FromRgb(r, g, b);
    }

    public static Color Lighten(Color c, double amount) => Blend(c, Colors.White, amount);
    public static Color Darken(Color c, double amount) => Blend(c, Colors.Black, amount);

    public static double RelativeLuminance(Color c)
    {
        static double Chan(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
    }
}
