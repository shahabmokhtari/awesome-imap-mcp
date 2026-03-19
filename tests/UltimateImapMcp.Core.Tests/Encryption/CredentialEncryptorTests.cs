using UltimateImapMcp.Core.Encryption;

namespace UltimateImapMcp.Core.Tests.Encryption;

public class CredentialEncryptorTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginal()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var plaintext = """{"password":"s3cret","token":"abc123"}""";
        var encrypted = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutput_EachCall()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var encrypted1 = encryptor.Encrypt("same input");
        var encrypted2 = encryptor.Encrypt("same input");
        Assert.NotEqual(encrypted1, encrypted2); // different nonce each time
    }

    [Fact]
    public void Decrypt_WrongPassphrase_Throws()
    {
        var encryptor1 = new CredentialEncryptor("passphrase-1");
        var encryptor2 = new CredentialEncryptor("passphrase-2");
        var encrypted = encryptor1.Encrypt("secret data");
        Assert.ThrowsAny<Exception>(() => encryptor2.Decrypt(encrypted));
    }

    [Fact]
    public void Encrypt_EmptyString_Works()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var encrypted = encryptor.Encrypt("");
        var decrypted = encryptor.Decrypt(encrypted);
        Assert.Equal("", decrypted);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var encrypted = encryptor.Encrypt("sensitive data");

        // Tamper with the ciphertext by flipping a byte
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0xFF; // flip last byte (in ciphertext region)
        var tampered = Convert.ToBase64String(bytes);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => encryptor.Decrypt(tampered));
    }

    [Fact]
    public void Decrypt_TruncatedPayload_Throws()
    {
        // Salt(16) + Nonce(12) + Tag(16) = 44 bytes minimum
        // Provide a payload shorter than that
        var shortPayload = Convert.ToBase64String(new byte[20]);

        var encryptor = new CredentialEncryptor("test-passphrase");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => encryptor.Decrypt(shortPayload));
    }

    [Fact]
    public void EncryptDecrypt_UnicodeContent_PreservesExactly()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var unicode = "\ud83d\ude00 Hello \u4e16\u754c \u00fc\u00f6\u00e4 \u0410\u0411\u0412";
        var encrypted = encryptor.Encrypt(unicode);
        var decrypted = encryptor.Decrypt(encrypted);
        Assert.Equal(unicode, decrypted);
    }

    [Fact]
    public void Constructor_NullPassphrase_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CredentialEncryptor(null!));
    }

    [Fact]
    public void Encrypt_NullPlaintext_ThrowsArgumentNullException()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        Assert.Throws<ArgumentNullException>(() => encryptor.Encrypt(null!));
    }
}
