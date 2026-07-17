using System.Security.Cryptography;
using System.Text;

namespace TelegramConsole.Infrastructure;

internal sealed class PortableDataProtection
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public PortableDataProtection(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _key = LoadKey(dataDirectory);
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("TelegramConsole.NAS.v1"));
        var output = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        output[0] = 1;
        nonce.CopyTo(output.AsSpan(1, NonceSize));
        tag.CopyTo(output.AsSpan(1 + NonceSize, TagSize));
        ciphertext.CopyTo(output.AsSpan(1 + NonceSize + TagSize));
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (protectedData.Length < 1 + NonceSize + TagSize || protectedData[0] != 1)
            throw new CryptographicException("配置文件格式无效");
        var plaintext = new byte[protectedData.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            protectedData.Slice(1, NonceSize),
            protectedData.Slice(1 + NonceSize + TagSize),
            protectedData.Slice(1 + NonceSize, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes("TelegramConsole.NAS.v1"));
        return plaintext;
    }

    private static byte[] LoadKey(string dataDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("TELEGRAMCONSOLE_MASTER_KEY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var key = Convert.FromBase64String(configured.Trim());
                if (key.Length == 32) return key;
            }
            catch (FormatException) { }
            throw new InvalidOperationException("TELEGRAMCONSOLE_MASTER_KEY 必须是 Base64 编码的 32 字节密钥");
        }

        var secretFile = Environment.GetEnvironmentVariable("TELEGRAMCONSOLE_MASTER_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(secretFile) && File.Exists(secretFile))
        {
            var value = File.ReadAllText(secretFile).Trim();
            var key = Convert.FromBase64String(value);
            if (key.Length != 32) throw new InvalidOperationException("密钥文件必须包含 Base64 编码的 32 字节密钥");
            return key;
        }

        var path = Path.Combine(dataDirectory, "master.key");
        if (File.Exists(path))
        {
            var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (key.Length != 32) throw new InvalidOperationException("master.key 长度无效");
            return key;
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Convert.ToBase64String(generated));
        File.Move(temporary, path, false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return generated;
    }
}

