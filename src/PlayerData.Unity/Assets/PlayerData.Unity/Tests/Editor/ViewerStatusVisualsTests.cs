using NUnit.Framework;
using UnityEngine;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class ViewerStatusVisualsTests
    {
        [Test]
        public void AccentColor_DistinguishesTheAttentionStates()
        {
            Color editable = ViewerStatusVisuals.AccentColor(DocumentState.Editable);
            Color unreadable = ViewerStatusVisuals.AccentColor(DocumentState.Unreadable);
            Color unknown = ViewerStatusVisuals.AccentColor(DocumentState.UnknownKey);

            Assert.That(editable, Is.Not.EqualTo(unreadable));
            Assert.That(editable, Is.Not.EqualTo(unknown));
            Assert.That(unreadable, Is.Not.EqualTo(unknown));
        }

        [Test]
        public void AccentColor_TreatsBothViewOnlyStatesAsOneColour()
        {
            // Round-trip and old-format are both "view only", so they share the amber accent.
            Assert.That(
                ViewerStatusVisuals.AccentColor(DocumentState.ReadOnlyRoundTrip),
                Is.EqualTo(ViewerStatusVisuals.AccentColor(DocumentState.ReadOnlyFormatVersion)));
        }

        [Test]
        public void AccentColor_IsAlwaysOpaque()
        {
            foreach (DocumentState state in System.Enum.GetValues(typeof(DocumentState)))
                Assert.That(ViewerStatusVisuals.AccentColor(state).a, Is.EqualTo(1f), state.ToString());
        }

        [Test]
        public void LiveAccentColor_DiffersFromEveryDiskState()
        {
            Color live = ViewerStatusVisuals.LiveAccentColor();
            foreach (DocumentState state in System.Enum.GetValues(typeof(DocumentState)))
                Assert.That(live, Is.Not.EqualTo(ViewerStatusVisuals.AccentColor(state)), state.ToString());
        }

        [Test]
        public void BadgeTints_KeepTheHueButLowerTheOpacity()
        {
            Color accent = ViewerStatusVisuals.AccentColor(DocumentState.Editable);
            Color background = ViewerStatusVisuals.BadgeBackground(accent);
            Color border = ViewerStatusVisuals.BadgeBorder(accent);

            Assert.That(background.r, Is.EqualTo(accent.r));
            Assert.That(background.g, Is.EqualTo(accent.g));
            Assert.That(background.b, Is.EqualTo(accent.b));
            Assert.That(background.a, Is.LessThan(border.a), "the fill is fainter than the border");
            Assert.That(border.a, Is.LessThan(accent.a), "both tints are more transparent than the solid accent");
        }
    }
}
