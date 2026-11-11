using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace KnightWinNotes.Services;

public static class WindowsNativeInterop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Windows 11 System Acrylic Backdrop

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    private static readonly UIntPtr HKEY_CURRENT_USER = new(0x80000001u);
    private const uint RRF_RT_REG_DWORD = 0x00000010;
    private const uint RRF_RT_REG_SZ = 0x00000002;
    private const uint REG_SZ = 1;
    private const uint KEY_WRITE = 0x20006;
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    // Application-specific entropy bound into Windows DPAPI
    private static readonly byte[] DpapiEntropy = Encoding.ASCII.GetBytes("KnightWinNotes.DPAPI.v1.2026.Key!");

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(
        UIntPtr hkey,
        string lpSubKey,
        string lpValue,
        uint dwFlags,
        out uint pdwType,
        out uint pvData,
        ref uint pcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(
        UIntPtr hkey,
        string lpSubKey,
        string? lpValue,
        uint dwFlags,
        out uint pdwType,
        StringBuilder? pvData,
        ref uint pcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegCreateKeyExW(
        UIntPtr hKey,
        string lpSubKey,
        uint Reserved,
        string? lpClass,
        uint dwOptions,
        uint samDesired,
        IntPtr lpSecurityAttributes,
        out UIntPtr phkResult,
        out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueExW(
        UIntPtr hKey,
        string? lpValueName,
        uint Reserved,
        uint dwType,
        byte[] lpData,
        uint cbData);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegCloseKey(UIntPtr hKey);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static void ApplyFluentWindowChrome(IntPtr hwnd, bool isDark, bool enableDwmAcrylicFallback = false)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;

        try
        {
            int darkFlag = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            int roundPref = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPref, sizeof(int));

            // Note: Do NOT set DWMWA_CAPTION_COLOR so Avalonia's WinUIComposition AcrylicBlur shines cleanly
            // through the titlebar (12% Dark / 6% Light) and command/tab/status bars (5% Dark / 2% Light).
            if (enableDwmAcrylicFallback)
            {
                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

                int backdrop = DWMSBT_TRANSIENTWINDOW; // System Desktop Acrylic
                _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // Older Windows builds ignore newer DWM attributes gracefully
        }
    }

    public static void ApplyPopupAcrylicChrome(IntPtr popupHwnd, bool isDark, bool enableDwmAcrylicFallback = false)
    {
        if (!OperatingSystem.IsWindows() || popupHwnd == IntPtr.Zero)
            return;

        try
        {
            int darkFlag = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(popupHwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            // Hardware-clip the popup HWND to 8px rounded corners + native Windows 11 flyout drop-shadow
            int roundPref = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(popupHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPref, sizeof(int));

            if (enableDwmAcrylicFallback)
            {
                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                _ = DwmExtendFrameIntoClientArea(popupHwnd, ref margins);

                int backdrop = DWMSBT_TRANSIENTWINDOW; // Transient Acrylic (never Mica)
                _ = DwmSetWindowAttribute(popupHwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // Ignore on older OS builds
        }
    }

    public static bool GetSystemIsDarkMode()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint size = sizeof(uint);
                int hr = RegGetValueW(
                    HKEY_CURRENT_USER,
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    RRF_RT_REG_DWORD,
                    out _,
                    out uint val,
                    ref size);
                if (hr == 0)
                    return val == 0;
            }
            catch
            {
                // Fallback below
            }
        }

        var variant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        if (variant == Avalonia.Platform.PlatformThemeVariant.Dark)
            return true;
        if (variant == Avalonia.Platform.PlatformThemeVariant.Light)
            return false;

        return Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    }

    public static Color GetSystemAccentColor()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint size = sizeof(uint);
                // Windows 10/11 AccentColor is stored in ABGR order (0xAABBGGRR)
                int hr = RegGetValueW(
                    HKEY_CURRENT_USER,
                    @"Software\Microsoft\Windows\DWM",
                    "AccentColor",
                    RRF_RT_REG_DWORD,
                    out _,
                    out uint abgr,
                    ref size);
                if (hr == 0 && abgr != 0)
                {
                    byte r = (byte)(abgr & 0xFF);
                    byte g = (byte)((abgr >> 8) & 0xFF);
                    byte b = (byte)((abgr >> 16) & 0xFF);
                    return Color.FromRgb(r, g, b);
                }

                size = sizeof(uint);
                hr = RegGetValueW(
                    HKEY_CURRENT_USER,
                    @"Software\Microsoft\Windows\DWM",
                    "ColorizationColor",
                    RRF_RT_REG_DWORD,
                    out _,
                    out uint argb,
                    ref size);
                if (hr == 0 && argb != 0)
                {
                    byte r = (byte)((argb >> 16) & 0xFF);
                    byte g = (byte)((argb >> 8) & 0xFF);
                    byte b = (byte)(argb & 0xFF);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch
            {
                // Fallback below
            }
        }

        try
        {
            var colorValues = Application.Current?.PlatformSettings?.GetColorValues();
            if (colorValues is not null && colorValues.AccentColor1.A > 0)
            {
                var c = colorValues.AccentColor1;
                // Avoid degenerate gray fallback on headless X11
                if (c.R != c.G || c.G != c.B)
                    return Color.FromRgb(c.R, c.G, c.B);
            }
        }
        catch
        {
            // Ignore
        }

        // Authentic Windows 11 / KnightWin Electric Fluent Blue fallback
        return Color.Parse("#0078D4");
    }

    public static byte[] ProtectSecretForCurrentUser(byte[] plainSecret)
    {
        if (OperatingSystem.IsWindows())
        {
            IntPtr inPtr = Marshal.AllocHGlobal(plainSecret.Length);
            IntPtr entPtr = Marshal.AllocHGlobal(DpapiEntropy.Length);
            DATA_BLOB outBlob = default;
            try
            {
                Marshal.Copy(plainSecret, 0, inPtr, plainSecret.Length);
                Marshal.Copy(DpapiEntropy, 0, entPtr, DpapiEntropy.Length);

                var inBlob = new DATA_BLOB { cbData = plainSecret.Length, pbData = inPtr };
                var entBlob = new DATA_BLOB { cbData = DpapiEntropy.Length, pbData = entPtr };

                if (!CryptProtectData(ref inBlob, "KnightWinNotesMasterSecret", ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new CryptographicException("Windows DPAPI CryptProtectData failed.");

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                // Wipe unmanaged input copy before freeing
                for (int i = 0; i < plainSecret.Length; i++)
                    Marshal.WriteByte(inPtr, i, 0);
                Marshal.FreeHGlobal(inPtr);
                Marshal.FreeHGlobal(entPtr);
                if (outBlob.pbData != IntPtr.Zero)
                    LocalFree(outBlob.pbData);
            }
        }

        // Non-Windows fallback using user-scoped AES-256-GCM envelope
        byte[] fallbackKey = DeriveLocalFallbackKey();
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] cipher = new byte[plainSecret.Length];
            using var aes = new AesGcm(fallbackKey, 16);
            aes.Encrypt(nonce, plainSecret, cipher, tag, DpapiEntropy);

            byte[] packed = new byte[12 + 16 + cipher.Length];
            Buffer.BlockCopy(nonce, 0, packed, 0, 12);
            Buffer.BlockCopy(tag, 0, packed, 12, 16);
            Buffer.BlockCopy(cipher, 0, packed, 28, cipher.Length);
            return packed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fallbackKey);
        }
    }

    public static byte[] UnprotectSecretForCurrentUser(byte[] protectedBlob)
    {
        if (OperatingSystem.IsWindows())
        {
            IntPtr inPtr = Marshal.AllocHGlobal(protectedBlob.Length);
            IntPtr entPtr = Marshal.AllocHGlobal(DpapiEntropy.Length);
            DATA_BLOB outBlob = default;
            try
            {
                Marshal.Copy(protectedBlob, 0, inPtr, protectedBlob.Length);
                Marshal.Copy(DpapiEntropy, 0, entPtr, DpapiEntropy.Length);

                var inBlob = new DATA_BLOB { cbData = protectedBlob.Length, pbData = inPtr };
                var entBlob = new DATA_BLOB { cbData = DpapiEntropy.Length, pbData = entPtr };

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new CryptographicException("Windows DPAPI CryptUnprotectData failed.");

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                for (int i = 0; i < outBlob.cbData; i++)
                    Marshal.WriteByte(outBlob.pbData, i, 0);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(inPtr);
                Marshal.FreeHGlobal(entPtr);
                if (outBlob.pbData != IntPtr.Zero)
                    LocalFree(outBlob.pbData);
            }
        }

        if (protectedBlob.Length < 28)
            throw new CryptographicException("Invalid protected secret blob.");

        byte[] fallbackKey = DeriveLocalFallbackKey();
        try
        {
            ReadOnlySpan<byte> span = protectedBlob;
            var nonce = span[..12];
            var tag = span.Slice(12, 16);
            var cipher = span[28..];
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(fallbackKey, 16);
            aes.Decrypt(nonce, cipher, tag, plain, DpapiEntropy);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fallbackKey);
        }
    }

    private static byte[] DeriveLocalFallbackKey()
    {
        string seed = $"{Environment.UserName}@{Environment.MachineName}:KnightWinNotes.LocalKey.v1";
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(seed),
            DpapiEntropy,
            100_000,
            HashAlgorithmName.SHA256,
            32);
    }

    public static bool IsKwNoteAssociated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            uint size = 512;
            var sb = new StringBuilder(256);
            int hr = RegGetValueW(
                HKEY_CURRENT_USER,
                @"Software\Classes\.kwnote",
                null,
                RRF_RT_REG_SZ,
                out _,
                sb,
                ref size);
            return hr == 0 && sb.ToString() == "KnightWinNotes.SecureDocument";
        }
        catch
        {
            return false;
        }
    }

    public static (bool Success, string Message) RegisterKwNoteAssociation()
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Windows Registry file association is active when running on Windows 10/11.");

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return (false, "Could not resolve executable path.");

            SetHkcuRegString(@"Software\Classes\.kwnote", null, "KnightWinNotes.SecureDocument");
            SetHkcuRegString(@"Software\Classes\.kwnote", "Content Type", "application/x-knightwin-note");
            SetHkcuRegString(@"Software\Classes\KnightWinNotes.SecureDocument", null, "KnightWin Encrypted Note (.kwnote)");
            SetHkcuRegString(@"Software\Classes\KnightWinNotes.SecureDocument\DefaultIcon", null, $"\"{exePath}\",0");
            SetHkcuRegString(@"Software\Classes\KnightWinNotes.SecureDocument\shell\open\command", null, $"\"{exePath}\" \"%1\"");

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            return (true, "Registered .kwnote file association in Windows Explorer (Current User).");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to register association: {ex.Message}");
        }
    }

    private static void SetHkcuRegString(string subKey, string? valueName, string data)
    {
        int hr = RegCreateKeyExW(
            HKEY_CURRENT_USER,
            subKey,
            0,
            null,
            0,
            KEY_WRITE,
            IntPtr.Zero,
            out UIntPtr hKey,
            out _);

        if (hr != 0)
            throw new InvalidOperationException($"RegCreateKeyExW failed with code {hr}");

        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(data + "\0");
            int setHr = RegSetValueExW(hKey, valueName, 0, REG_SZ, bytes, (uint)bytes.Length);
            if (setHr != 0)
                throw new InvalidOperationException($"RegSetValueExW failed with code {setHr}");
        }
        finally
        {
            _ = RegCloseKey(hKey);
        }
    }
}
