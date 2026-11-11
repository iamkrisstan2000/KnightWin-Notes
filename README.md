# KnightWin Notes

**KnightWin Notes** is a fast, lightweight **FluentUI** notepad for Windows 10 & Windows 11 that combines the clean, familiar feel of the modern Windows 11 Notepad with built-in **AES-256 password encryption** for your private notes.

Built natively in C# / .NET 8 (**zero Electron**, zero web-view bloat, and **100% offline**), KnightWin Notes ships as a single portable `KnightWinNotes.exe` you can run right away.

---

## ✨ Features

- **Plain & Secure Modes in One Click**
  - **Plain (`.txt`)** — Everyday fast text editing, compatible with every text editor (default mode).
  - **Secure (`.kwnote`)** — Toggle the **Shield** button on the command bar (or use `File → Save As (Choose Mode)...`) to save your note as a password-protected `.kwnote` file that only KnightWin Notes can unlock.
- **Flexible Password Options**
  - Save time by setting a **Master Password** in **Settings → Security**, or choose **Ask every time** to give each `.kwnote` file its own unique password.
- **Designed for Windows 11 (FluentUI)**
  - Multi-tab workspace with Notepad-style yellow unsaved-change indicator dots.
  - Subtle multi-layer **Acrylic translucency**, crisp rounded corners, and a sleek collapsible Settings navigation pane.
  - **Personalization**:
    - **App mode**: `Light`, `Dark`, or `Sync with Windows`
    - **Accent**: `Sync with Windows`, `Red`, `Purple`, `Matcha`, `Beige`, or `Orange`
    - **Pad color**: `White`, `Yellow` (warm legal pad), or `Accent`
- **Fast & Light on RAM**
  - Native compiled desktop app with workstation memory tuning, zero duplicate text snapshots in RAM, and instant tab cleanup when notes are closed.
- **Optional `.kwnote` File Association**
  - Register `.kwnote` in **Settings → Security** with one click (no administrator rights needed) so double-clicking any `.kwnote` file in File Explorer opens it directly in KnightWin Notes.

---

## 🔒 Security & Privacy (Plain & Simple)

Your notes belong strictly to you:

1. **100% Offline — Zero Telemetry**
   - KnightWin Notes contains **no networking code, no cloud sync, no analytics, and no telemetry**. Nothing you type or open ever leaves your computer.
2. **Real Authenticated Encryption (`AES-256-GCM`)**
   - Every `.kwnote` file is encrypted with **AES-256-GCM** using a fresh random 256-bit salt and 96-bit nonce on every single save.
   - A 128-bit authentication tag protects both the file header and your note — if a file is tampered with or the wrong password is entered, decryption safely stops before anything is shown.
3. **Brute-Force Resistant Key Derivation (`PBKDF2-HMAC-SHA512`)**
   - Passwords are stretched through **600,000 iterations** of `PBKDF2-HMAC-SHA512` (following OWASP recommendations) to defend against automated password guessing.
4. **Memory & Master Key Hygiene**
   - Optional Master Passwords are encrypted locally using **Windows DPAPI** tied to your Windows user profile.
   - Decrypted key material and password input fields are actively wiped from memory (`CryptographicOperations.ZeroMemory`) as soon as saves, unlocks, or tabs close.

> **Important:** Because KnightWin Notes is 100% offline with zero backdoors, **forgotten passwords cannot be recovered**. Please keep your password somewhere safe!

---

## 🚀 Quick Start

1. Download **`KnightWinNotes.exe`** and run it — no installation required.
2. Start typing! Press **`Ctrl + S`** to save:
   - Leave the **Shield** icon off to save a standard **`.txt`** file.
   - Click the **Shield** icon on the right side of the command bar to save an encrypted **`.kwnote`** file.
3. Click the **Gear** icon (`Settings`) to customize themes, colors, fonts, or your Master Password.

### Handy Keyboard Shortcuts

| Action | Shortcut |
| :--- | :--- |
| New Tab / New Window | `Ctrl + N` / `Ctrl + Shift + N` |
| Open File | `Ctrl + O` |
| Save / Save As (Choose Mode) | `Ctrl + S` / `Ctrl + Shift + S` |
| Close Tab | `Ctrl + W` |
| Find / Replace / Go to Line | `Ctrl + F` / `Ctrl + H` / `Ctrl + G` |
| Insert Time & Date | `F5` |
| Zoom In / Zoom Out / Reset Zoom | `Ctrl + Plus` / `Ctrl + Minus` / `Ctrl + 0` |

---

## ☕ Support the Creator

**KnightWin Notes** is free, open-source, ad-free, and built with care for people who want a clean, private Windows notepad without subscriptions or bloatware.

If KnightWin Notes helps you stay organized or keeps your personal notes safe, consider buying the creator a coffee — every bit of support helps fund future updates and new open-source tools!

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N7D7292WUD)

---

## 🛠️ Building from Source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:DebugSymbols=false
```

---

## 📄 License

Created by **KnightWin** • Source repository: [github.com/iamkrisstan2000/KnightWin-Notes](https://github.com/iamkrisstan2000/KnightWin-Notes)

Licensed under the **GNU General Public License v3.0 (GPL-3.0)**. See [`LICENSE`](LICENSE) for the full license text.
