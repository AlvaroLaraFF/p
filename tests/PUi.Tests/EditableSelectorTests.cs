using PUi.Core;
using PUi.Input;
using SkiaSharp;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The selector component with edit mode (Settings: Upload, Resolution, Language; reusable by any future selector).</summary>
    public class EditableSelectorTests
    {
        private static EditableSelector Make(out int[] value)
        {
            var v = new[] { 2 };
            var sel = new EditableSelector("s") { Text = () => v[0].ToString() };
            sel.Step = d => { int n = System.Math.Max(0, System.Math.Min(4, v[0] + d)); bool ch = n != v[0]; v[0] = n; return ch; };
            value = v;
            return sel;
        }

        [Fact]
        public void ItStartsAtRestWithTheRowAndTheZoneAsSeparateFocusables()
        {
            int[] v; var sel = Make(out v);
            Assert.False(sel.Editing);
            Assert.NotSame(sel.Row, sel.Zone);
            Assert.Equal("s", sel.Row.Id); Assert.Equal("s:zone", sel.Zone.Id);
            Assert.Equal("rows", sel.Row.Group); Assert.Equal("zone", sel.Zone.Group);
        }

        [Fact]
        public void TheZoneSitsAtTheRightEdgeOfTheRowCentredVertically()
        {
            int[] v; var sel = Make(out v);
            sel.Layout(new SKRect(100, 200, 892, 340), 28);
            Assert.Equal(892 - 28, sel.Zone.R.Right, 1); Assert.Equal(EditableSelector.Width, sel.Zone.R.Width, 1);
            Assert.Equal(270, (sel.Zone.R.Top + sel.Zone.R.Bottom) / 2, 1); Assert.Equal(EditableSelector.Height, sel.Zone.R.Height, 1);
        }

        [Fact]
        public void InEditModeLeftAndRightStepTheValueAndEveryKeyIsConsumedExceptTheOnesThatLeave()
        {
            int[] v; var sel = Make(out v);
            sel.Begin();
            Assert.True(sel.Editing);
            Assert.False(sel.OnEditKey(UiKey.Right, 1.0)); Assert.Equal(3, v[0]); Assert.Equal(1, sel.PressedSide);
            Assert.False(sel.OnEditKey(UiKey.Left, 1.1)); Assert.False(sel.OnEditKey(UiKey.Left, 1.2)); Assert.Equal(1, v[0]); Assert.Equal(-1, sel.PressedSide);
            Assert.False(sel.OnEditKey(UiKey.Up, 1.3)); Assert.False(sel.OnEditKey(UiKey.Down, 1.4)); Assert.False(sel.OnEditKey(UiKey.ChannelUp, 1.5));
            Assert.Equal(1, v[0]);                                    // up, down and the rest do nothing
            Assert.True(sel.OnEditKey(UiKey.Ok, 2.0));                // OK exits
            Assert.True(sel.OnEditKey(UiKey.Back, 2.1));              // Back exits
        }

        [Fact]
        public void TheValueStopsAtTheEndsAndDoesNotWrapAround()
        {
            int[] v; var sel = Make(out v);
            sel.Begin();
            for (int i = 0; i < 10; i++) sel.OnEditKey(UiKey.Right, i);
            Assert.Equal(4, v[0]);
            for (int i = 0; i < 10; i++) sel.OnEditKey(UiKey.Left, i);
            Assert.Equal(0, v[0]);
        }

        [Fact]
        public void BeginAndEndToggleTheEditingState()
        {
            int[] v; var sel = Make(out v);
            sel.Begin(); Assert.True(sel.Editing);
            sel.End(); Assert.False(sel.Editing);
        }

        [Fact]
        public void AKeyPressAnimatesTheButtonBriefly()
        {
            int[] v; var sel = Make(out v);
            Assert.False(sel.Animating(5.0));
            sel.Begin(); sel.OnEditKey(UiKey.Right, 5.0);
            Assert.True(sel.Animating(5.1));
            Assert.False(sel.Animating(5.5));
        }
    }
}
