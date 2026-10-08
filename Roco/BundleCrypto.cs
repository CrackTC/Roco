using System.Security.Cryptography;

namespace Roco;

/// <summary>bundle 里 TextAsset 加解密用的固定密钥，decrypt 和 encrypt 共用一份。</summary>
internal static class BundleCrypto
{
    const string KeyBase64 = "rT8Pie5RxTdzHxeW91xxhAFhdW2g1IbJ";
    const string IvBase64 = "TkCziuvxqFMSLF+tzKNoXQ==";

    public static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = Convert.FromBase64String(KeyBase64);
        aes.IV = Convert.FromBase64String(IvBase64);
        return aes;
    }
}
