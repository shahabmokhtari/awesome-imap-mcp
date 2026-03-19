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
}
