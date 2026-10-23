using System;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// How the player tells the shell something the viewer has to know while a channel is on screen ("this device cannot decode the channel's audio"). The player raises it from its own thread; the
    /// shell shows it (NoticeOverlay). Static because the players are created and thrown away for every attempt and have nothing to hold on to.
    /// </summary>
    internal static class PlayerNotices
    {
        /// <summary>A notice from a player (the owner): the text to show.</summary>
        public static event Action<object, string> Raised;
        /// <summary>The owner is gone: its notice, if it is the one on screen, goes too (a later player's notice stays).</summary>
        public static event Action<object> Cleared;

        public static void Raise(object owner, string text)
        {
            UdpLog.Info("player", "notice: " + text);
            var h = Raised;
            if (h != null) { try { h(owner, text); } catch (Exception ex) { UdpLog.Warn("player", "notice handler: " + ex.Message); } }
        }

        public static void Clear(object owner)
        {
            var h = Cleared;
            if (h != null) { try { h(owner); } catch (Exception ex) { UdpLog.Warn("player", "notice handler: " + ex.Message); } }
        }
    }

    /// <summary>
    /// A line of text over the picture, at the top and centred, on a dark rounded box: the notices of PlayerNotices. A plain TextView above the interface's view, so the interface (the same on every
    /// platform) knows nothing about it; it takes no focus and no touch. It stays for a while and goes away by itself, or at once when the player that raised it is gone.
    /// </summary>
    internal sealed class NoticeOverlay
    {
        private const int ShownMs = 15000;
        private readonly TextView _view;
        private readonly Handler _main = new Handler(Looper.MainLooper);
        private int _generation;
        private object _owner;

        public NoticeOverlay(Activity activity, FrameLayout root, bool tv)
        {
            var dm = activity.Resources.DisplayMetrics;
            Func<float, int> dp = v => (int)(v * dm.Density + 0.5f);
            _view = new TextView(activity) { Visibility = ViewStates.Gone, Gravity = GravityFlags.Center, Focusable = false, Clickable = false };
            _view.SetTextColor(Color.White);
            _view.SetTextSize(ComplexUnitType.Sp, 13);
            _view.SetMaxWidth((int)(dm.WidthPixels * 0.8f));
            var bg = new GradientDrawable();
            bg.SetColor(Color.Argb(225, 12, 16, 24));
            bg.SetCornerRadius(dp(10));
            bg.SetStroke(dp(1), Color.Argb(170, 255, 190, 70));
            _view.Background = bg;
            _view.SetPadding(dp(16), dp(10), dp(16), dp(10));
            var lp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.CenterHorizontal);
            lp.TopMargin = dp(tv ? 28 : 56);
            root.AddView(_view, lp);
        }

        /// <summary>Shows the notice of a player; any thread.</summary>
        public void Show(object owner, string text)
        {
            _main.Post(() =>
            {
                int gen = ++_generation;
                _owner = owner;
                _view.Text = text;
                _view.Visibility = ViewStates.Visible;
                _main.PostDelayed(() => { if (gen == _generation) _view.Visibility = ViewStates.Gone; }, ShownMs);
            });
        }

        /// <summary>Takes the notice away if it is this player's; any thread.</summary>
        public void Hide(object owner)
        {
            _main.Post(() => { if (!ReferenceEquals(_owner, owner)) return; _generation++; _owner = null; _view.Visibility = ViewStates.Gone; });
        }
    }
}
