using System.ComponentModel;
using System.Globalization;
using Hakobune.App.Views;
using Hakobune.Core.Connections;
using Hakobune.Core.Transfers;
using Hakobune.Core.Remote;
using Hakobune.Presentation.Backups;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Transfers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Hakobune.App.Services;

/// <summary>
/// 接続のときの質問（パスワード、サーバーの鍵、証明書）と、接続先の作成・編集を、ダイアログで聞く。
/// どのスレッドから呼んでもよい（画面のスレッドに頼んで出す）。
/// </summary>
internal sealed class DialogService : IConnectionPrompts, IConnectionDialogs, ITransferDialogs, IFileDialogs, ISettingsDialogs, ISyncDialogs, IBackupDialogs
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<nint> _windowHandle;

    // ダイアログは一度に 1 つしか出せない
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    // ほかの質問が来たら、いったん隠して先を譲るダイアログ（同期の確認）。比べているあいだに、
    // つなぎ直しでパスワードや鍵を聞くことがあるので、譲らないと互いに待ち続けてしまう
    private ContentDialog? _yieldable;
    private bool _yielded;

    // 同じ名前のものがあったときに、前に選んだ扱い（次に聞くときの最初の選択にする）
    private ConflictPolicy _lastConflictPolicy = ConflictPolicy.OverwriteIfNewer;

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
                    Style = Styled("CriticalTextStyle"),
                });
            }
            panel.Children.Add(Paragraph($"「{profile.DisplayName}」にログインします。"));
            panel.Children.Add(Field("ユーザー", profile.UserName.Length > 0 ? $"{profile.UserName}@{profile.Host}" : profile.Host));
            var password = new PasswordBox { Header = "パスワード" };
            var save = new CheckBox { Content = "パスワードを保存する（Windows の資格情報マネージャーに入れます）", IsChecked = true };
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

    public Task<string?> AskPassphraseAsync(ConnectionProfile profile, bool previousAttemptFailed) =>
        OnUiThreadAsync<string?>(async () =>
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            if (previousAttemptFailed)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "パスフレーズが違うようです。もう一度入れてください。",
                    Style = Styled("CriticalTextStyle"),
                });
            }
            panel.Children.Add(Paragraph($"「{profile.DisplayName}」に入るための秘密鍵に、パスフレーズが掛かっています。"));
            panel.Children.Add(Field("秘密鍵", profile.PrivateKeyPath ?? ""));
            var passphrase = new PasswordBox { Header = "パスフレーズ" };
            panel.Children.Add(passphrase);
            panel.Children.Add(new TextBlock
            {
                Text = "パスフレーズは保存しません。アプリを閉じるまで覚えておきます。",
                FontSize = 12,
                Style = Styled("SecondaryTextStyle"),
            });
            passphrase.Loaded += (_, _) => passphrase.Focus(FocusState.Programmatic);

            var dialog = new ContentDialog
            {
                Title = "パスフレーズを入れてください",
                Content = panel,
                PrimaryButtonText = "接続",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Primary,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary ? passphrase.Password : null;
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
        OnUiThreadAsync(() => ShowEditorAsync(new ConnectionEditorViewModel(existing, hasSavedPassword)));

    public Task<ConnectionEditResult?> AskQuickConnectAsync() =>
        OnUiThreadAsync(() => ShowEditorAsync(new ConnectionEditorViewModel(quickConnect: true)));

    // 接続先の作成・編集・クイック接続のダイアログ（画面のスレッドで呼ぶ）
    private async Task<ConnectionEditResult?> ShowEditorAsync(ConnectionEditorViewModel editor)
    {
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
    }

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

    public Task<ConflictPolicy?> AskConflictAsync(TransferDirection direction, IReadOnlyList<string> names, int count) =>
        OnUiThreadAsync<ConflictPolicy?>(async () =>
        {
            var where = direction == TransferDirection.Upload ? "サーバー" : "パソコン";
            var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
            panel.Children.Add(Paragraph(count > 1
                ? $"{where}に、同じ名前のものが {count.ToString("N0", CultureInfo.CurrentCulture)} 件あります。"
                : $"{where}に、同じ名前のものがあります。"));
            panel.Children.Add(NameList(names, count));
            panel.Children.Add(Paragraph("フォルダの中のファイルも、同じように扱います。"));

            var choices = new RadioButtons();
            choices.Items.Add(new RadioButton { Content = "新しいときだけ上書きする（おすすめ）", Tag = ConflictPolicy.OverwriteIfNewer });
            choices.Items.Add(new RadioButton { Content = "すべて上書きする", Tag = ConflictPolicy.Overwrite });
            choices.Items.Add(new RadioButton { Content = "同じ名前のものは送らない", Tag = ConflictPolicy.Skip });
            choices.SelectedIndex = _lastConflictPolicy switch
            {
                ConflictPolicy.Overwrite => 1,
                ConflictPolicy.Skip => 2,
                _ => 0,
            };
            panel.Children.Add(choices);

            var dialog = new ContentDialog
            {
                Title = "同じ名前のものがあります",
                Content = panel,
                PrimaryButtonText = direction == TransferDirection.Upload ? "アップロード" : "ダウンロード",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ShowAsync(dialog).ConfigureAwait(true) != ContentDialogResult.Primary)
                return null;
            var policy = (choices.SelectedItem as RadioButton)?.Tag is ConflictPolicy selected ? selected : ConflictPolicy.OverwriteIfNewer;
            _lastConflictPolicy = policy;
            return policy;
        });

    public Task<bool> ConfirmStopTransfersAsync(int pendingCount, bool closingApp) =>
        OnUiThreadAsync(async () =>
        {
            var count = pendingCount.ToString("N0", CultureInfo.CurrentCulture);
            var dialog = new ContentDialog
            {
                Title = "転送が終わっていません",
                Content = Paragraph(
                    $"まだ {count} 件の転送が残っています。{(closingApp ? "閉じる" : "切断する")}と、転送を止めます。" +
                    "残りは保存しておき、次にこの接続先につないだときに続きから送れます。"),
                PrimaryButtonText = closingApp ? "止めて閉じる" : "止めて切断",
                CloseButtonText = "転送を続ける",
                DefaultButton = ContentDialogButton.Close,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
        });

    public Task<string?> AskNameAsync(string title, string initialName, string primaryButtonText, Func<string, string?> validate) =>
        OnUiThreadAsync<string?>(async () =>
        {
            var box = new TextBox { Text = initialName, MinWidth = 360, IsSpellCheckEnabled = false };
            var problem = new TextBlock
            {
                FontSize = 12,
                Style = Styled("CriticalTextStyle"),
                Visibility = Visibility.Collapsed,
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(box);
            panel.Children.Add(problem);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Primary,
            };
            void Check()
            {
                var message = validate(box.Text);
                problem.Text = message ?? "";
                problem.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = message is null;
            }
            box.TextChanged += (_, _) => Check();
            box.Loaded += (_, _) =>
            {
                box.Focus(FocusState.Programmatic);
                // 拡張子の前までを選んでおく（エクスプローラーと同じ）
                var dot = initialName.LastIndexOf('.');
                box.Select(0, dot > 0 ? dot : initialName.Length);
            };
            Check();
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary ? box.Text : null;
        });

    public Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> names, bool remote, bool recoverable) =>
        OnUiThreadAsync(async () =>
        {
            var what = names.Count == 1
                ? $"「{names[0]}」"
                : $"{names.Count.ToString("N0", CultureInfo.CurrentCulture)} 個の項目（{string.Join("、", names.Take(3))}{(names.Count > 3 ? " ほか" : "")}）";
            var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
            panel.Children.Add(Paragraph($"{what}を削除しますか？"));
            if (remote && recoverable)
                panel.Children.Add(Paragraph("フォルダは中身ごと消えます。消す前に控えを取るので、下の「控え」から元に戻せます（100 MB より大きいファイルは除きます）。"));
            else if (remote)
                panel.Children.Add(Warning("サーバーから消したものは、元に戻せません。フォルダは中身ごと消えます。"));
            else
                panel.Children.Add(Paragraph(recoverable ? "ごみ箱に移します。" : "元に戻せません。"));

            var dialog = new ContentDialog
            {
                Title = "削除の確認",
                Content = panel,
                PrimaryButtonText = "削除",
                CloseButtonText = "やめる",
                // 元に戻せないときは、うっかり Enter で消さないようにする
                DefaultButton = remote || !recoverable ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
        });

    public Task<int?> AskPermissionsAsync(IReadOnlyList<string> names, int? currentMode) =>
        OnUiThreadAsync<int?>(async () =>
        {
            var mode = currentMode ?? Convert.ToInt32("644", 8);
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(Paragraph(names.Count == 1
                ? $"「{names[0]}」の属性"
                : $"{names.Count.ToString("N0", CultureInfo.CurrentCulture)} 個の項目の属性（すべて同じにします）"));

            // 所有者・グループ・その他 × 読み・書き・実行
            var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
            for (var i = 0; i < 4; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var i = 0; i < 4; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string[] who = ["所有者", "グループ", "その他"];
            string[] what = ["読み", "書き", "実行"];
            for (var c = 0; c < 3; c++)
                AddCell(grid, new TextBlock { Text = what[c], Style = Styled("SecondaryTextStyle") }, 0, c + 1);
            var boxes = new CheckBox[9];
            for (var r = 0; r < 3; r++)
            {
                AddCell(grid, new TextBlock { Text = who[r], VerticalAlignment = VerticalAlignment.Center }, r + 1, 0);
                for (var c = 0; c < 3; c++)
                {
                    var bit = 8 - (r * 3 + c);
                    var box = new CheckBox { MinWidth = 0, IsChecked = (mode & (1 << bit)) != 0 };
                    AutomationProperties.SetName(box, $"{who[r]}の{what[c]}");
                    boxes[r * 3 + c] = box;
                    AddCell(grid, box, r + 1, c + 1);
                }
            }
            panel.Children.Add(grid);
            var octal = new TextBox { Header = "数字で（例: 644、755）", Text = UnixMode.Format(mode), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(octal);

            var dialog = new ContentDialog
            {
                Title = "属性の変更",
                Content = panel,
                PrimaryButtonText = "変更",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Primary,
            };

            // チェックと数字を、どちらから変えても合わせる
            var syncing = false;
            int Current() => boxes.Select((box, i) => box.IsChecked == true ? 1 << (8 - i) : 0).Sum() | (mode & ~0x1FF);
            foreach (var box in boxes)
            {
                box.Click += (_, _) =>
                {
                    if (syncing)
                        return;
                    syncing = true;
                    octal.Text = UnixMode.Format(Current());
                    syncing = false;
                };
            }
            octal.TextChanged += (_, _) =>
            {
                var parsed = UnixMode.Parse(octal.Text);
                dialog.IsPrimaryButtonEnabled = parsed is not null;
                if (syncing || parsed is not { } value)
                    return;
                syncing = true;
                for (var i = 0; i < 9; i++)
                    boxes[i].IsChecked = (value & (1 << (8 - i))) != 0;
                mode = value;
                syncing = false;
            };

            if (await ShowAsync(dialog).ConfigureAwait(true) != ContentDialogResult.Primary)
                return null;
            return UnixMode.Parse(octal.Text);
        });

    private static void AddCell(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

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

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, bool yieldable = false)
    {
        if (!yieldable && _yieldable is { } open)
        {
            _yielded = true;
            open.Hide();
        }
        await _dialogGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // まだウインドウが無いとき（起動中など）は、やめたことにする
            if (_xamlRoot() is not { } root)
                return ContentDialogResult.None;
            dialog.XamlRoot = root;
            dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            // ダイアログは画面の外側に出るので、設定で選んだライト・ダークを受け継がない。画面に合わせ、出している間に変わっても追う。
            var content = root.Content as FrameworkElement;
            TypedEventHandler<FrameworkElement, object> follow = (sender, _) => dialog.RequestedTheme = sender.ActualTheme;
            if (content is not null)
            {
                dialog.RequestedTheme = content.ActualTheme;
                content.ActualThemeChanged += follow;
            }
            if (yieldable)
                _yieldable = dialog;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                if (yieldable)
                    _yieldable = null;
                if (content is not null)
                    content.ActualThemeChanged -= follow;
            }
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public Task ShowSettingsAsync(SettingsViewModel settings) =>
        OnUiThreadAsync(async () =>
        {
            var view = new SettingsView(settings);
            var dialog = new ContentDialog
            {
                Title = "設定",
                Content = view,
                CloseButtonText = "閉じる",
                DefaultButton = ContentDialogButton.Close,
            };
            // カードを並べるので、ふつうのダイアログより少し広げる
            dialog.Resources["ContentDialogMaxWidth"] = 640d;
            if (settings.OpensAtExclusions)
                dialog.Opened += (_, _) => _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, view.ShowExclusions);
            await ShowAsync(dialog).ConfigureAwait(true);
            return true;
        });

    public Task<bool> ReviewSyncAsync(SyncViewModel sync) =>
        OnUiThreadAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = sync.Title,
                Content = new SyncView(sync),
                PrimaryButtonText = sync.PrimaryText,
                IsPrimaryButtonEnabled = sync.CanConfirm,
                CloseButtonText = "やめる",
                DefaultButton = sync.DeleteOnlyOnTarget ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };
            // 一覧を並べるので、ふつうのダイアログより広く、高くする（ウインドウが小さければ、中を送って見る）
            dialog.Resources["ContentDialogMaxWidth"] = 780d;
            dialog.Resources["ContentDialogMaxHeight"] = 960d;
            PropertyChangedEventHandler onChanged = (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(SyncViewModel.PrimaryText):
                        dialog.PrimaryButtonText = sync.PrimaryText;
                        break;
                    case nameof(SyncViewModel.CanConfirm):
                        dialog.IsPrimaryButtonEnabled = sync.CanConfirm;
                        break;
                    case nameof(SyncViewModel.DeleteOnlyOnTarget):
                        // 消すものを選んだら、Enter でうっかり決めないようにする
                        dialog.DefaultButton = sync.DeleteOnlyOnTarget ? ContentDialogButton.Close : ContentDialogButton.Primary;
                        break;
                }
            };
            sync.PropertyChanged += onChanged;
            try
            {
                while (true)
                {
                    var result = await ShowAsync(dialog, yieldable: true).ConfigureAwait(true);
                    if (_yielded)
                    {
                        // 先に出した質問が終わってから、もう一度出す（比べているのは続いている）
                        _yielded = false;
                        continue;
                    }
                    return result == ContentDialogResult.Primary && sync.CanConfirm;
                }
            }
            finally
            {
                sync.PropertyChanged -= onChanged;
            }
        });

    public Task<bool> ConfirmRestoreAsync(IReadOnlyList<string> names, int count, IReadOnlyList<string> changed) =>
        OnUiThreadAsync(async () =>
        {
            var panel = new StackPanel { Spacing = 12, MaxWidth = 480 };
            panel.Children.Add(Paragraph(count > 1
                ? $"{count.ToString("N0", CultureInfo.CurrentCulture)} 件を、控えを取ったときの中身に戻します。"
                : "控えを取ったときの中身に戻します。"));
            panel.Children.Add(NameList(names, count));
            if (changed.Count > 0)
            {
                panel.Children.Add(Warning(changed.Count == 1
                    ? "次のものは、控えを取ったあとにもサーバーで変わっています。"
                    : $"次の {changed.Count.ToString("N0", CultureInfo.CurrentCulture)} 件は、控えを取ったあとにもサーバーで変わっています。"));
                panel.Children.Add(NameList([.. changed.Take(3)], changed.Count));
            }
            panel.Children.Add(Paragraph("いまの中身も控えに残すので、あとでまた戻せます。"));
            var dialog = new ContentDialog
            {
                Title = "元に戻す",
                Content = panel,
                PrimaryButtonText = count > 1 ? $"{count.ToString("N0", CultureInfo.CurrentCulture)} 件を元に戻す" : "元に戻す",
                CloseButtonText = "やめる",
                // そのあとに変わったものがあるときは、うっかり Enter で戻さないようにする
                DefaultButton = changed.Count > 0 ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };
            return await ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
        });

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    // 名前を 1 行ずつ並べた枠（多いときは「ほか N 件」）
    private static Border NameList(IReadOnlyList<string> names, int total)
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var name in names)
            list.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
        if (total > names.Count)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"ほか {(total - names.Count).ToString("N0", CultureInfo.CurrentCulture)} 件",
                Style = Styled("SecondaryTextStyle"),
            });
        }
        return new Border { Style = Styled("NameListBoxStyle"), Child = list };
    }

    // 16 進の指紋を「AB:CD:…」の形にする（ブラウザーの証明書の画面と同じ）
    private static string PairUp(string hex) =>
        string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private static Border Warning(string text) => new()
    {
        Style = Styled("CriticalBoxStyle"),
        Child = new TextBlock { Text = text, Style = Styled("CriticalTextStyle") },
    };

    private static StackPanel Field(string label, string value, bool monospace = false)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Style = Styled("SecondaryTextStyle") });
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (monospace)
            text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        panel.Children.Add(text);
        return panel;
    }

    // App.xaml の部品の形。色は ThemeResource なので、ダイアログの色（ライト・ダーク）に合う
    private static Style? Styled(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;
}
