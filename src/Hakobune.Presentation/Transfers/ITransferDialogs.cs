using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>転送のときに利用者に聞くこと（画面がダイアログで聞く）。</summary>
public interface ITransferDialogs
{
    /// <summary>
    /// 送り先に同じ名前のものがあるときの扱いを聞く。<paramref name="names"/> はぶつかった名前（多いときは一部）。やめたら null。
    /// </summary>
    Task<ConflictPolicy?> AskConflictAsync(TransferDirection direction, IReadOnlyList<string> names, int count);

    /// <summary>終わっていない転送を止めてよいか聞く（切断する、別の接続先にする、アプリを閉じるとき）。</summary>
    Task<bool> ConfirmStopTransfersAsync(int pendingCount, bool closingApp);
}
