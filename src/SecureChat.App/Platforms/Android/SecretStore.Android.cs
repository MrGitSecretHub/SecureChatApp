using System.Security.Cryptography;
using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace SecureChat.App.Services;

public sealed partial class SecretStore
{
    private const string KeyAlias = "securechat.secretstore.v2";
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("SecureChat.SecretStore.v1");

    private partial byte[] Protect(byte[] plaintext)
    {
        var key = GetOrCreateKey();

        // Android Keystore сам выбирает IV при шифровании и не принимает свой,
        // поэтому IV берём из шифра уже после Init.
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding");
        cipher.Init(CipherMode.EncryptMode, key);
        cipher.UpdateAAD(Aad);

        var iv = cipher.GetIV()
            ?? throw new CryptographicException("Keystore не вернул IV.");
        if (iv.Length != 12)
        {
            throw new CryptographicException("Неожиданная длина IV от Keystore.");
        }

        var encrypted = cipher.DoFinal(plaintext)
            ?? throw new CryptographicException("Не удалось зашифровать секрет.");

        var result = new byte[iv.Length + encrypted.Length];
        iv.CopyTo(result, 0);
        encrypted.CopyTo(result, iv.Length);
        return result;
    }

    private partial byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < 12 + 16)
        {
            throw new CryptographicException("Повреждённые защищённые данные.");
        }

        var key = GetOrCreateKey();
        var iv = protectedData.AsSpan(0, 12).ToArray();
        var ciphertext = protectedData.AsSpan(12).ToArray();

        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding");
        cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
        cipher.UpdateAAD(Aad);

        return cipher.DoFinal(ciphertext)
            ?? throw new CryptographicException("Не удалось расшифровать секрет.");
    }

    private static IKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore");
        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias))
        {
            return keyStore.GetKey(KeyAlias, null)
                ?? throw new CryptographicException("Ключ Android Keystore не найден.");
        }

        var generator = KeyGenerator.GetInstance(
            KeyProperties.KeyAlgorithmAes,
            "AndroidKeyStore");

        var spec = new KeyGenParameterSpec.Builder(
                KeyAlias,
                // PURPOSE_ENCRYPT (1) | PURPOSE_DECRYPT (2): ключ нужен и для шифрования, и для расшифровки.
                (KeyStorePurpose)3)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();

        generator.Init(spec);
        return generator.GenerateKey()
            ?? throw new CryptographicException("Не удалось создать ключ Android Keystore.");
    }
}
