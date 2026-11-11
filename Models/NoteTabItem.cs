using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KnightWinNotes.Models;

public sealed class NoteTabItem : INotifyPropertyChanged
{
    // Ephemeral process-only 256-bit key so session passwords are never stored in cleartext fields
    private static readonly byte[] _processEphemeralKey = RandomNumberGenerator.GetBytes(32);

    private string? _filePath;
    private string _content = string.Empty;
    private int _savedLength;
    private readonly byte[] _savedHash = new byte[32];
    private bool _isModified;
    private int _cachedWordCount;
    private bool _isSecureFile;
    private bool _isSecureModeSelected;
    private bool _isSelected;
    private string _lineEnding = "CRLF";
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private int _caretIndex;
    private int _selectionLength;
    private int _zoomPercent = 100;

    // Ephemeral encrypted session password (for seamless Ctrl+S on an unlocked .kwnote tab)
    private byte[]? _encryptedSessionPw;
    private byte[]? _sessionPwNonce;
    private byte[]? _sessionPwTag;

    public NoteTabItem()
    {
        SHA256.HashData(ReadOnlySpan<byte>.Empty, _savedHash);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath != value)
            {
                _filePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(HasFilePath));
            }
        }
    }

    public bool HasFilePath => !string.IsNullOrWhiteSpace(_filePath);

    public string Title => string.IsNullOrWhiteSpace(_filePath)
        ? "Untitled"
        : Path.GetFileName(_filePath);

    // Tab text stays clean ("Untitled" or filename); unsaved state is shown by the yellow tab dot
    public string HeaderTitle => Title;

    public string Content
    {
        get => _content;
        set
        {
            string normalized = value ?? string.Empty;
            if (!string.Equals(_content, normalized, StringComparison.Ordinal))
            {
                bool wasModified = _isModified;
                _content = normalized;
                _cachedWordCount = ComputeWordCount(_content.AsSpan());
                _isModified = ComputeIsModified(_content, _savedLength, _savedHash);

                OnPropertyChanged();
                OnPropertyChanged(nameof(StatsText));
                if (_isModified != wasModified)
                {
                    OnPropertyChanged(nameof(IsModified));
                }
            }
        }
    }

    public bool IsModified => _isModified;

    public bool IsSecureFile
    {
        get => _isSecureFile;
        set
        {
            if (_isSecureFile != value)
            {
                _isSecureFile = value;
                if (value)
                    IsSecureModeSelected = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SecurityBadgeText));
            }
        }
    }

    public bool IsSecureModeSelected
    {
        get => _isSecureModeSelected;
        set
        {
            if (_isSecureModeSelected != value)
            {
                _isSecureModeSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SecurityBadgeText));
            }
        }
    }

    public string SecurityBadgeText => IsSecureFile
        ? "Encrypted (.kwnote • AES-256-GCM)"
        : IsSecureModeSelected
            ? "Secure Mode (.kwnote on Save)"
            : "Plain Text (.txt)";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string LineEnding
    {
        get => _lineEnding;
        set
        {
            string norm = value == "LF" ? "LF" : "CRLF";
            if (_lineEnding != norm)
            {
                _lineEnding = norm;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LineEndingDisplay));
            }
        }
    }

    public string LineEndingDisplay => _lineEnding == "LF" ? "Unix (LF)" : "Windows (CRLF)";

    public int CaretIndex
    {
        get => _caretIndex;
        set
        {
            if (_caretIndex != value)
            {
                _caretIndex = Math.Max(0, value);
                ComputeCaretLineAndColumn();
                OnPropertyChanged();
                OnPropertyChanged(nameof(CaretLine));
                OnPropertyChanged(nameof(CaretColumn));
                OnPropertyChanged(nameof(CaretPositionText));
            }
        }
    }

    public int CaretLine => _caretLine;
    public int CaretColumn => _caretColumn;
    public string CaretPositionText => $"Ln {_caretLine}, Col {_caretColumn}";

    public int SelectionLength
    {
        get => _selectionLength;
        set
        {
            int clean = Math.Max(0, value);
            if (_selectionLength != clean)
            {
                _selectionLength = clean;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatsText));
            }
        }
    }

    public int ZoomPercent
    {
        get => _zoomPercent;
        set
        {
            int clamped = Math.Clamp(value, 50, 300);
            if (_zoomPercent != clamped)
            {
                _zoomPercent = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ZoomText));
            }
        }
    }

    public string ZoomText => $"{_zoomPercent}%";

    public string StatsText
    {
        get
        {
            int chars = _content.Length;
            string wordLabel = _cachedWordCount == 1 ? "word" : "words";
            string charLabel = chars == 1 ? "character" : "characters";
            if (_selectionLength > 0)
                return $"{_selectionLength:N0} of {chars:N0} {charLabel}  •  {_cachedWordCount:N0} {wordLabel}";
            return $"{chars:N0} {charLabel}  •  {_cachedWordCount:N0} {wordLabel}";
        }
    }

    public bool HasSessionPassword =>
        _encryptedSessionPw is not null &&
        _sessionPwNonce is not null &&
        _sessionPwTag is not null;

    public void SetSessionPassword(string? password)
    {
        ClearSessionPassword();
        if (string.IsNullOrEmpty(password))
            return;

        byte[] plain = Encoding.UTF8.GetBytes(password);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];

        try
        {
            using var aes = new AesGcm(_processEphemeralKey, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
            _encryptedSessionPw = cipher;
            _sessionPwNonce = nonce;
            _sessionPwTag = tag;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string? GetSessionPassword()
    {
        if (_encryptedSessionPw is null || _sessionPwNonce is null || _sessionPwTag is null)
            return null;

        byte[] plain = new byte[_encryptedSessionPw.Length];
        try
        {
            using var aes = new AesGcm(_processEphemeralKey, 16);
            aes.Decrypt(_sessionPwNonce, _encryptedSessionPw, _sessionPwTag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void ClearSessionPassword()
    {
        if (_encryptedSessionPw is not null)
            CryptographicOperations.ZeroMemory(_encryptedSessionPw);
        if (_sessionPwNonce is not null)
            CryptographicOperations.ZeroMemory(_sessionPwNonce);
        if (_sessionPwTag is not null)
            CryptographicOperations.ZeroMemory(_sessionPwTag);

        _encryptedSessionPw = null;
        _sessionPwNonce = null;
        _sessionPwTag = null;
    }

    public void ReleaseMemory()
    {
        ClearSessionPassword();
        _content = string.Empty;
        _savedLength = 0;
        _cachedWordCount = 0;
        _isModified = false;
        CryptographicOperations.ZeroMemory(_savedHash);
    }

    public static void WipeProcessSessionKey()
    {
        CryptographicOperations.ZeroMemory(_processEphemeralKey);
    }

    public void MarkSaved(string? newPath = null, bool? isSecure = null)
    {
        if (newPath is not null)
            FilePath = newPath;
        if (isSecure.HasValue)
        {
            IsSecureFile = isSecure.Value;
            IsSecureModeSelected = isSecure.Value;
        }

        _savedLength = _content.Length;
        SHA256.HashData(MemoryMarshal.AsBytes(_content.AsSpan()), _savedHash);
        _isModified = false;
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(HeaderTitle));
    }

    public void LoadInitialContent(string text, string lineEnding, string? filePath, bool isSecure)
    {
        _content = text ?? string.Empty;
        _savedLength = _content.Length;
        SHA256.HashData(MemoryMarshal.AsBytes(_content.AsSpan()), _savedHash);
        _cachedWordCount = ComputeWordCount(_content.AsSpan());
        _isModified = false;
        _lineEnding = lineEnding == "LF" ? "LF" : "CRLF";
        _filePath = filePath;
        _isSecureFile = isSecure;
        _isSecureModeSelected = isSecure;
        _caretIndex = 0;
        _caretLine = 1;
        _caretColumn = 1;
        _selectionLength = 0;

        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(LineEnding));
        OnPropertyChanged(nameof(LineEndingDisplay));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(HasFilePath));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(IsSecureFile));
        OnPropertyChanged(nameof(IsSecureModeSelected));
        OnPropertyChanged(nameof(SecurityBadgeText));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(CaretPositionText));
    }

    private void ComputeCaretLineAndColumn()
    {
        if (string.IsNullOrEmpty(_content) || _caretIndex <= 0)
        {
            _caretLine = 1;
            _caretColumn = 1;
            return;
        }

        int limit = Math.Min(_caretIndex, _content.Length);
        int line = 1;
        int lastNewLine = -1;
        for (int i = 0; i < limit; i++)
        {
            if (_content[i] == '\n')
            {
                line++;
                lastNewLine = i;
            }
        }

        _caretLine = line;
        _caretColumn = limit - lastNewLine;
        if (_caretColumn > 1 && limit <= _content.Length && limit > 0 && _content[limit - 1] == '\r')
        {
            _caretColumn = Math.Max(1, _caretColumn - 1);
        }
    }

    private static bool ComputeIsModified(string current, int savedLen, ReadOnlySpan<byte> savedHash)
    {
        if (current.Length != savedLen)
            return true;

        Span<byte> curHash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(current.AsSpan()), curHash);
        return !CryptographicOperations.FixedTimeEquals(curHash, savedHash);
    }

    private static int ComputeWordCount(ReadOnlySpan<char> text)
    {
        int words = 0;
        bool inWord = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }
        return words;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
