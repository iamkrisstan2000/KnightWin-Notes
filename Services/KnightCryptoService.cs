using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnightWinNotes.Services;

/// <summary>
/// Authenticated Encryption container for KnightWin Notes (.kwnote).
///
/// Binary Layout (v1):
///   [0..7]   8B : Magic "KWNOTE\x01\x00"
///   [8..11]  4B : Flags (UInt32 LE, 0x01 = AES-256-GCM + PBKDF2-HMAC-SHA512)
///   [12..15] 4B : KDF Iteration Count (UInt32 LE, default 600,000)
///   [16..47] 32B: Per-file CSPRNG Salt
///   [48..59] 12B: Per-save CSPRNG AES-GCM Nonce
///   [60..75] 16B: AES-GCM 128-bit Authentication Tag
///   [76..N]  var: Ciphertext (Header bytes [0..59] bound as AAD)
/// </summary>
public static class KnightCryptoService
{
    public const string SecureExtension = ".kwnote";
    public const int DefaultPbkdf2Iterations = 600_000;
    private const int MinAllowedIterations = 100_000;
    private const int MaxAllowedIterations = 5_000_000;
    private const int SaltLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int HeaderBeforeTagLength = 8 + 4 + 4 + SaltLength + NonceLength; // 60 bytes
    private const int FullHeaderLength = HeaderBeforeTagLength + TagLength;        // 76 bytes

    private static ReadOnlySpan<byte> MagicBytes => "KWNOTE\x01\x00"u8;

    private sealed class EncryptedNoteEnvelope
    {
        public string App { get; set; } = "KnightWinNotes";
        public int Schema { get; set; } = 1;
        public string LineEnding { get; set; } = "CRLF";
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public string Text { get; set; } = string.Empty;
    }

    public static bool HasSecureMagic(ReadOnlySpan<byte> data) =>
        data.Length >= MagicBytes.Length && data[..MagicBytes.Length].SequenceEqual(MagicBytes);

    public static bool IsSecureFileByPathOrMagic(string filePath)
    {
        if (filePath.EndsWith(SecureExtension, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            if (!File.Exists(filePath))
                return false;
            Span<byte> header = stackalloc byte[8];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = fs.Read(header);
            return read == 8 && HasSecureMagic(header);
        }
        catch
        {
            return false;
        }
    }

    public static byte[] EncryptNote(string content, string lineEnding, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A non-empty password is required to encrypt a Secure note.", nameof(password));

        var envelope = new EncryptedNoteEnvelope
        {
            LineEnding = lineEnding == "LF" ? "LF" : "CRLF",
            SavedUtc = DateTime.UtcNow,
            Text = NormalizeToLf(content)
        };

        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] key = new byte[KeyLength];

        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);

            Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                key,
                DefaultPbkdf2Iterations,
                HashAlgorithmName.SHA512);

            byte[] output = new byte[FullHeaderLength + plainBytes.Length];
            Span<byte> outSpan = output;

            MagicBytes.CopyTo(outSpan[..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(outSpan.Slice(8, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(outSpan.Slice(12, 4), (uint)DefaultPbkdf2Iterations);
            salt.CopyTo(outSpan.Slice(16, SaltLength));
            nonce.CopyTo(outSpan.Slice(48, NonceLength));

            ReadOnlySpan<byte> aad = outSpan[..HeaderBeforeTagLength];
            Span<byte> tagSpan = outSpan.Slice(HeaderBeforeTagLength, TagLength);
            Span<byte> cipherSpan = outSpan[FullHeaderLength..];

            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plainBytes, cipherSpan, tagSpan, aad);

            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static (string Content, string LineEnding, DateTime SavedUtc) DecryptNote(ReadOnlySpan<byte> fileBytes, string password)
    {
        if (fileBytes.Length < FullHeaderLength)
            throw new InvalidDataException("This file is truncated or is not a valid KnightWin Secure Note (.kwnote).");

        if (!HasSecureMagic(fileBytes))
            throw new InvalidDataException("Invalid file signature. Only authentic KnightWin Secure Notes (.kwnote) can be decrypted.");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(8, 4));
        if (flags != 1u)
            throw new InvalidDataException($"Unsupported .kwnote container version/flags (0x{flags:X}).");

        int iterations = (int)BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.Slice(12, 4));
        if (iterations < MinAllowedIterations || iterations > MaxAllowedIterations)
            throw new InvalidDataException("Invalid KDF parameters in .kwnote header.");

        ReadOnlySpan<byte> salt = fileBytes.Slice(16, SaltLength);
        ReadOnlySpan<byte> nonce = fileBytes.Slice(48, NonceLength);
        ReadOnlySpan<byte> aad = fileBytes[..HeaderBeforeTagLength];
        ReadOnlySpan<byte> tag = fileBytes.Slice(HeaderBeforeTagLength, TagLength);
        ReadOnlySpan<byte> cipher = fileBytes[FullHeaderLength..];

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        byte[] key = new byte[KeyLength];
        byte[] plainBytes = new byte[cipher.Length];

        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                key,
                iterations,
                HashAlgorithmName.SHA512);

            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipher, tag, plainBytes, aad);

            var envelope = JsonSerializer.Deserialize<EncryptedNoteEnvelope>(plainBytes)
                ?? throw new InvalidDataException("Decrypted payload is malformed.");

            return (envelope.Text ?? string.Empty, envelope.LineEnding == "LF" ? "LF" : "CRLF", envelope.SavedUtc);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("Incorrect password or the encrypted file has been tampered with.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static (string SaltBase64, string VerifierBase64, string ProtectedSecretBase64) CreateMasterPasswordCredentials(string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword))
            throw new ArgumentException("Master password cannot be empty.", nameof(masterPassword));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] pwBytes = Encoding.UTF8.GetBytes(masterPassword);
        byte[] verifier = new byte[KeyLength];

        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                pwBytes,
                salt,
                verifier,
                DefaultPbkdf2Iterations,
                HashAlgorithmName.SHA512);

            byte[] protectedBlob = WindowsNativeInterop.ProtectSecretForCurrentUser(pwBytes);
            return (
                Convert.ToBase64String(salt),
                Convert.ToBase64String(verifier),
                Convert.ToBase64String(protectedBlob));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwBytes);
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public static bool VerifyMasterPassword(string candidatePassword, string? saltBase64, string? verifierBase64)
    {
        if (string.IsNullOrEmpty(candidatePassword) ||
            string.IsNullOrWhiteSpace(saltBase64) ||
            string.IsNullOrWhiteSpace(verifierBase64))
        {
            return false;
        }

        byte[] pwBytes = Encoding.UTF8.GetBytes(candidatePassword);
        byte[] derived = new byte[KeyLength];
        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] expected = Convert.FromBase64String(verifierBase64);
            Rfc2898DeriveBytes.Pbkdf2(
                pwBytes,
                salt,
                derived,
                DefaultPbkdf2Iterations,
                HashAlgorithmName.SHA512);
            return CryptographicOperations.FixedTimeEquals(derived, expected);
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwBytes);
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    public static string? UnprotectStoredMasterPassword(string? protectedSecretBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedSecretBase64))
            return null;

        byte[]? raw = null;
        try
        {
            byte[] blob = Convert.FromBase64String(protectedSecretBase64);
            raw = WindowsNativeInterop.UnprotectSecretForCurrentUser(blob);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (raw is not null)
                CryptographicOperations.ZeroMemory(raw);
        }
    }

    public static (int Score, string Label, string ColorHex) EvaluatePasswordStrength(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return (0, "Enter a password", "#888888");

        int score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));
        int variety = (hasUpper ? 1 : 0) + (hasLower ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        if (variety >= 2) score++;
        if (variety >= 3 && password.Length >= 10) score++;

        score = Math.Clamp(score, 1, 4);
        return score switch
        {
            1 => (1, "Weak — consider 10+ characters", "#E05252"),
            2 => (2, "Fair — add numbers or symbols", "#E09B36"),
            3 => (3, "Good — solid encryption key", "#3B9EE8"),
            _ => (4, "Strong — Knight-Grade security", "#4CAF6A")
        };
    }

    public static string NormalizeToLf(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

    public static (string NormalizedLf, string DetectedLineEnding) NormalizeAndDetectLineEnding(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return (string.Empty, "CRLF");

        bool hasCrLf = raw.Contains("\r\n", StringComparison.Ordinal);
        bool hasLf = raw.Contains('\n');
        string ending = hasCrLf || !hasLf ? "CRLF" : "LF";
        return (NormalizeToLf(raw), ending);
    }

    public static string FormatWithLineEnding(string text, string lineEnding)
    {
        string lf = NormalizeToLf(text);
        return lineEnding == "LF" ? lf : lf.Replace("\n", "\r\n");
    }
}
