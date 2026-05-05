using System.Security.Cryptography;
using System.Text;

namespace AwesomeImapMcp.Core.Encryption;

/// <summary>
/// Encrypts and decrypts credential strings using AES-256-GCM with PBKDF2 key derivation.
/// Encoded payload layout (base64): salt(16) | nonce(12) | tag(16) | ciphertext
/// </summary>
public sealed class CredentialEncryptor
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    private readonly string _passphrase;

    /// <summary>Creates an encryptor locked to the given passphrase.</summary>
    public CredentialEncryptor(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        _passphrase = passphrase;
    }

    /// <summary>
    /// Creates an encryptor whose passphrase is derived from the current machine's unique identifier.
    /// </summary>
    public static CredentialEncryptor FromMachineId() => new(MachineId.Get());

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a base64-encoded string containing
    /// the salt, nonce, authentication tag, and ciphertext.
    /// </summary>
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Generate random salt and nonce for this operation
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var key = DeriveKey(_passphrase, salt);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: salt | nonce | tag | ciphertext
        var payload = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        var offset = 0;

        Buffer.BlockCopy(salt,       0, payload, offset, SaltSize);   offset += SaltSize;
        Buffer.BlockCopy(nonce,      0, payload, offset, NonceSize);   offset += NonceSize;
        Buffer.BlockCopy(tag,        0, payload, offset, TagSize);     offset += TagSize;
        Buffer.BlockCopy(ciphertext, 0, payload, offset, ciphertext.Length);

        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypts a base64-encoded payload produced by <see cref="Encrypt"/>.
    /// Throws if the passphrase is wrong or the data has been tampered with.
    /// </summary>
    public string Decrypt(string encryptedBase64)
    {
        ArgumentNullException.ThrowIfNull(encryptedBase64);

        var payload = Convert.FromBase64String(encryptedBase64);

        if (payload.Length < SaltSize + NonceSize + TagSize)
            throw new CryptographicException("Payload is too short to be valid.");

        var offset = 0;

        var salt = payload[offset..(offset + SaltSize)];         offset += SaltSize;
        var nonce = payload[offset..(offset + NonceSize)];       offset += NonceSize;
        var tag = payload[offset..(offset + TagSize)];           offset += TagSize;
        var ciphertext = payload[offset..];

        var key = DeriveKey(_passphrase, salt);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(key, TagSize);
        // AesGcm.Decrypt throws AuthenticationTagMismatchException on wrong key/tampered data
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }
}
