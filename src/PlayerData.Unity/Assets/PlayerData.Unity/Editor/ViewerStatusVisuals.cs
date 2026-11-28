using UnityEngine;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// ビューワーのステータスの色のルールを、ここに一本化しています。同じ
    /// <see cref="DocumentState"/> は、どこに出てきても同じアクセントカラーに対応します。ツリーのドット、
    /// 詳細ヘッダーのバッジ、詳細カードの左側のアクセントがそうです。色が飾りではなく、データとして
    /// 読めるようにするためです（このドキュメントは信用できるか、編集できるか、といったことです）。UIToolkit に依存しない純粋な作りにしているので、
    /// EditMode テストではウィンドウなしで対応関係を確認できます。
    /// </summary>
    public static class ViewerStatusVisuals
    {
        // 明るいエディタースキンでも暗いスキンでも読みやすいよう、中くらいのトーンを選んでいます。
        private static readonly Color Editable = new Color(0.24f, 0.62f, 0.31f); // 緑
        private static readonly Color ViewOnly = new Color(0.78f, 0.57f, 0.18f); // 琥珀色
        private static readonly Color Unknown = new Color(0.43f, 0.48f, 0.54f); // スレート色
        private static readonly Color Unreadable = new Color(0.78f, 0.35f, 0.35f); // 赤
        private static readonly Color Live = new Color(0.24f, 0.61f, 0.69f); // ティール色

        /// <summary>ドキュメントのディスク上の状態に対応するアクセントカラーです。</summary>
        public static Color AccentColor(DocumentState state)
        {
            switch (state)
            {
                case DocumentState.Editable:
                    return Editable;
                case DocumentState.ReadOnlyRoundTrip:
                case DocumentState.ReadOnlyFormatVersion:
                    return ViewOnly;
                case DocumentState.UnknownKey:
                    return Unknown;
                case DocumentState.Unreadable:
                    return Unreadable;
                default:
                    return Unknown;
            }
        }

        /// <summary>
        /// ライブ（プレイモード中）のドキュメント向けのアクセントです。そのディスク上の <see cref="DocumentState"/> は
        /// 開くまで定まらないので、ツリーでは状態を推測せず「live」と示します。
        /// </summary>
        public static Color LiveAccentColor() => Live;

        /// <summary>ステータスバッジの背景の薄い塗りです。アクセントカラーを低い不透明度で使っています。</summary>
        public static Color BadgeBackground(Color accent) => WithAlpha(accent, 0.18f);

        /// <summary>ステータスバッジのまわりの細い線です。アクセントカラーを少し薄くして使っています。</summary>
        public static Color BadgeBorder(Color accent) => WithAlpha(accent, 0.5f);

        private static Color WithAlpha(Color color, float alpha) =>
            new Color(color.r, color.g, color.b, alpha);
    }
}
