using Hakobune.Presentation.Files;
using Microsoft.VisualBasic.FileIO;

namespace Hakobune.App.Services;

/// <summary>パソコンのファイルを消すときは、ごみ箱へ移す（エクスプローラーと同じ。あとから戻せる）。</summary>
internal sealed class RecycleBinFileOperations : ILocalFileOperations
{
    public bool UsesRecycleBin => true;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Rename(string fromPath, string toPath) => LocalFileOperations.Instance.Rename(fromPath, toPath);

    public void Delete(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            // 使用中などで消せないときは、Windows がその理由を出す
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            else if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
        }
    }
}
