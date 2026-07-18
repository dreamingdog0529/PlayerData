using UnityEngine;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// Single source of truth for the viewer's status colour language. The same
    /// <see cref="DocumentState"/> maps to one accent colour everywhere it appears — the tree dot,
    /// the detail-header badge and the detail card's left accent — so colour reads as data
    /// (can I trust/edit this document?) rather than decoration. Pure and UIToolkit-independent so
    /// EditMode tests can assert the mapping without a window.
    /// </summary>
    public static class ViewerStatusVisuals
    {
        // Mid-tone values chosen to stay legible against both the light and dark editor skins.
        private static readonly Color Editable = new Color(0.24f, 0.62f, 0.31f); // green
        private static readonly Color ViewOnly = new Color(0.78f, 0.57f, 0.18f); // amber
        private static readonly Color Unknown = new Color(0.43f, 0.48f, 0.54f); // slate
        private static readonly Color Unreadable = new Color(0.78f, 0.35f, 0.35f); // red
        private static readonly Color Live = new Color(0.24f, 0.61f, 0.69f); // teal

        /// <summary>The accent colour for a document's on-disk state.</summary>
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
        /// Accent for a live (play-mode) document. Its on-disk <see cref="DocumentState"/> is
        /// undefined until opened, so the tree flags it as "live" rather than guessing a state.
        /// </summary>
        public static Color LiveAccentColor() => Live;

        /// <summary>Faint fill behind a status badge: the accent at low opacity.</summary>
        public static Color BadgeBackground(Color accent) => WithAlpha(accent, 0.18f);

        /// <summary>Hairline around a status badge: the accent at partial opacity.</summary>
        public static Color BadgeBorder(Color accent) => WithAlpha(accent, 0.5f);

        private static Color WithAlpha(Color color, float alpha) =>
            new Color(color.r, color.g, color.b, alpha);
    }
}
