namespace Hakobune.Presentation.Connections;

/// <summary>
/// パスワードの保管場所を、失敗しても接続を止めないように使う。
/// 読めなければ「保存していない」とみなして聞き直し、書けなければ今回だけ使う。
/// </summary>
internal static class SecretStoreExtensions
{
    public static string? TryRead(this ISecretStore store, Guid id)
    {
        try
        {
            return store.Read(id);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return null;
        }
    }

    public static bool TryWrite(this ISecretStore store, Guid id, string secret)
    {
        try
        {
            store.Write(id, secret);
            return true;
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return false;
        }
    }

    public static bool TryDelete(this ISecretStore store, Guid id)
    {
        try
        {
            store.Delete(id);
            return true;
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return false;
        }
    }

    // 資格情報マネージャーのエラー（Win32Exception など）。引数の誤りやメモリ不足は隠さない。
    private static bool IsStoreError(Exception ex) => ex is not (ArgumentException or OutOfMemoryException);
}
