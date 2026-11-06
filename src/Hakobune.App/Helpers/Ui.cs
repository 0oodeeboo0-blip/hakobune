using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace Hakobune.App.Helpers;

/// <summary>画面の x:Bind から呼ぶ、値の変換。</summary>
public static class Ui
{
    /// <summary>選んでいるものは太字にする。</summary>
    public static Windows.UI.Text.FontWeight SemiBoldIf(bool value) => value ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>選んでいないものは少し薄くする。</summary>
    public static double DimUnless(bool value) => value ? 1.0 : 0.72;

    /// <summary>当てはまらないときだけ見せる。</summary>
    public static Visibility VisibleUnless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>どちらにも当てはまらないときだけ見せる。</summary>
    public static Visibility VisibleUnlessAny(bool first, bool second) => first || second ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>接続先の一覧の開閉ボタンのアイコン。</summary>
    public static string SidebarGlyph(bool isOpen) => isOpen ? "" : "";

    /// <summary>中央の余白。接続先の一覧を閉じたときは左にも余白をとる。</summary>
    public static Thickness ContentPadding(bool isSidebarOpen) => new(isSidebarOpen ? 4 : 12, 0, 12, 12);

    /// <summary>転送の一覧の高さ（たたんだときは見出しだけ）。</summary>
    public static double TransferPanelHeight(bool isExpanded) => isExpanded ? 244 : 50;

    /// <summary>転送の一覧の見出しの下線（たたんだときは出さない）。</summary>
    public static Thickness BottomLine(bool isExpanded) => new(0, 0, 0, isExpanded ? 1 : 0);

    /// <summary>ファイルの一覧の行の形（設定の「コンパクト」なら低くする）。</summary>
    public static Style FileRowStyle(bool compact) =>
        (Style)Application.Current.Resources[compact ? "CompactFileRowContainerStyle" : "FileRowContainerStyle"];
}
