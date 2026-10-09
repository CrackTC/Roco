namespace Roco;

/// <summary>资源服务器上 bundle 的地址。</summary>
internal static class AssetUrls
{
    public static string ForBundle(int version, string fileName) =>
        $"https://d2sf4w9bkv485c.cloudfront.net/{version}/production/2018/Android/{fileName}";
}
