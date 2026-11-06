using System.ComponentModel;
using System.Globalization;
using Hakobune.App.Views;
using Hakobune.Core.Connections;
using Hakobune.Presentation.Connections;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hakobune.App.Services;

/// <summary>
/// 接続のときの質問（パスワード、サーバーの鍵、証明書）と、接続先の作成・編集を、ダイアログで聞く。
/// どのスレッドから呼んでもよい（画面のスレッドに頼んで出す）。
/// </summary>
internal sealed class DialogService : IConnectionPrompts, IConnectionDialogs
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<nint> _windowHandle;

    // ダイアログは一度に 1 つしか出せない
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>
    /// <paramref name="xamlRoot"/> はダイアログを出す画面、<paramref name="windowHandle"/> はファイルを選ぶ画面の持ち主のウインドウ。
    /// まだウインドウが無いうちに聞かれたら、やめたことにする。
    /// </summary>
    public DialogService(DispatcherQueue dispatcher, Func<XamlRoot?> xamlRoot, Func<nint> windowHandle)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(windowHandle);
        _dispatcher = dispatcher;
        _xamlRoot = xamlRoot;
        _windowHandle = windowHandle;
    }

    public Task<PasswordAnswer?> AskPasswordAsync(ConnectionProfile profile, bool previousAttemptFailed) =>
        OnUiThreadAsync<PasswordAnswer?>(async () =>
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            if (previousAttemptFailed)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "パスワードが違うようです。もう一度入れてください。",
                    Foreground = Brush("SystemFillColorCriticalBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var account = profile.UserName.Length > 0 ? $"{profile.UserName}@{profile.Host}" : profile.Host;
            panel.Children.Add(new TextBlock { Text = $"{profile.DisplayName}（{account}）にログインします。", TextWrapping = TextWrapping.Wrap });
            var password = new PasswordBox { Header = "パスワード" };
            var save = new CheckBox { Content = "パスワードを保存する（Windows の資格情報マネージャーに入れる）", IsChecked = true };
            panel.Children.Add(password);
            panel.Children.Add(save);
            password.Loaded += (_, _) => password.Focus(FocusState.Programmatic);

            var dialog = new ContentDialog
            {
                Title = "パスワードを入れてください",
                Content = panel,
                PrimaryButtonText = "接続",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await ShowAsync(dialog).ConfigureAwait(true);
            return result == ContentDialogResult.Primary ? new PasswordAnswer(password.Password, save.IsChecked == true) : null;
        });

    public Task<bool> ConfirmHostKeyAsync(HostKeyQuestion question) =>
        OnUiThreadAsync(async () =>
        {
            var profile = question.Profile;
            var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
            if (question.IsChanged)
            {
                panel.Children.Add(Warning(
                    "前に接続したときと、サーバーの鍵が違います。サーバーを入れ替えたなどの理由がなければ、別のサーバーになりすまされているおそれがあります。"));
            }
            panel.Children.Add(Paragraph(
                $"{profile.Host}（ポート {profile.EffectivePort.ToString(CultureInfo.InvariantCulture)}）の鍵を確かめてください。" +
                "サーバーの管理者から聞いた指紋と同じなら、「信頼して接続」を押してください。次からは聞きません。"));
            panel.Children.Add(Field("鍵の種類", $"{question.Algorithm}（{question.KeyLength.ToString(CultureInfo.InvariantCulture)} ビット）"));
            // ssh-keygen -l と同じ書き方にして、管理者から聞いた指紋と見比べやすくする
            panel.Children.Add(Field("指紋", "SHA256:" + question.Fingerprint, monospace: true));
            if (question.PreviousFingerprint is { } previous)
                panel.Children.Add(Field("前の指紋", "SHA256:" + previous, monospace: true));

            var dialog = new ContentDialog
            {
                Title = question.IsChanged ? "サーバーの鍵が変わっています" : "初めて接続するサーバーです",
                Content = panel,
                PrimaryButtonText = "信頼して接続",
                CloseButtonText = "やめる",
                // 鍵が変わったときは、うっかり Enter で信頼しないようにする
                DefaultButton = question.IsChanged ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
        });

    public Task<bool> ConfirmCertificateAsync(CertificateQuestion question) =>
        OnUiThreadAsync(async () =>
        {
            var profile = question.Profile;
            var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
            if (question.IsChanged)
                panel.Children.Add(Warning("前に受け入れた証明書と違います。なりすましのおそれがあります。"));
            panel.Children.Add(Paragraph($"{profile.Host} の証明書は、Windows が信頼する証明書ではありません。"));
            foreach (var problem in question.Problems)
                panel.Children.Add(Paragraph("・" + problem));
            panel.Children.Add(Field("発行先", question.Subject));
            panel.Children.Add(Field("発行者", question.Issuer));
            if (question.NotBefore is { } from && question.NotAfter is { } to)
                panel.Children.Add(Field("有効期間", $"{from.ToLocalTime():yyyy/MM/dd} 〜 {to.ToLocalTime():yyyy/MM/dd}"));
            panel.Children.Add(Field("指紋（SHA-256）", PairUp(question.Fingerprint), monospace: true));
            panel.Children.Add(Paragraph("自分で用意したサーバーなど、相手を確かめられるときだけ受け入れてください。次からは聞きません。"));

            var dialog = new ContentDialog
            {
                Title = question.IsChanged ? "サーバーの証明書が変わっています" : "証明書を確かめてください",
                Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
                PrimaryButtonText = "受け入れて接続",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Close,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
        });

    public Task<ConnectionEditResult?> EditAsync(ConnectionProfile? existing, bool hasSavedPassword) =>
        OnUiThreadAsync<ConnectionEditResult?>(async () =>
        {
            var editor = new ConnectionEditorViewModel(existing, hasSavedPassword);
            var dialog = new ContentDialog
            {
                Title = editor.Title,
                Content = new ConnectionEditorView(editor, _windowHandle),
                PrimaryButtonText = editor.PrimaryButtonText,
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = editor.IsValid,
            };
            PropertyChangedEventHandler onChanged = (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionEditorViewModel.IsValid))
                    dialog.IsPrimaryButtonEnabled = editor.IsValid;
            };
            editor.PropertyChanged += onChanged;
            try
            {
                var result = await ShowAsync(dialog).ConfigureAwait(true);
                return result == ContentDialogResult.Primary && editor.IsValid ? editor.ToResult() : null;
            }
            finally
            {
                editor.PropertyChanged -= onChanged;
            }
        });

    public Task<bool> ConfirmDeleteAsync(ConnectionProfile profile) =>
        OnUiThreadAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = $"「{profile.DisplayName}」を削除しますか？",
                Content = Paragraph("保存してあるパスワードも消えます。サーバーのファイルは消えません。"),
                PrimaryButtonText = "削除",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Close,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
        });

    // 画面のスレッドで動かし、終わるまで待てる Task を返す
    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher.HasThreadAccess)
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        if (!queued)
            completion.SetException(new InvalidOperationException("画面を出せませんでした（アプリを閉じるところかもしれません）。"));
        return completion.Task;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // まだウインドウが無いとき（起動中など）は、やめたことにする
            if (_xamlRoot() is not { } root)
                return ContentDialogResult.None;
            dialog.XamlRoot = root;
            dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    // 16 進の指紋を「AB:CD:…」の形にする（ブラウザーの証明書の画面と同じ）
    private static string PairUp(string hex) =>
        string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private static Border Warning(string text) => new()
    {
        Padding = new Thickness(12, 8, 12, 8),
        CornerRadius = new CornerRadius(4),
        Background = Brush("SystemFillColorCriticalBackgroundBrush"),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("SystemFillColorCriticalBrush") },
    };

    private static StackPanel Field(string label, string value, bool monospace = false)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush") });
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (monospace)
            text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        panel.Children.Add(text);
        return panel;
    }

    private static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
