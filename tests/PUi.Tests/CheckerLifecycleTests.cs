using System;
using PUi.Core;
using PUi.Input;
using PUi.Screens;
using PUi.Testing;
using Xunit;

namespace PUi.Tests
{
    /// <summary>User rule: while the screen where a channel appears is open it keeps being checked; on leaving it everything is cancelled (nothing is left in the background).</summary>
    public class CheckerLifecycleTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        [Fact]
        public void EveryScreenThatChecksChannelsStopsTheCheckerWhenItIsLeft()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            Assert.IsType<MainScreen>(r.App.Current);
            int s0 = r.Checker.Stops;
            r.Platform.PressKey(UiKey.Ok); r.Advance(r.Now + 0.4);                          // TV -> first category: leaves TV (it was checking the recents)
            Assert.IsType<CategoryScreen>(r.App.Current);
            Assert.True(r.Checker.Stops > s0, "TV must stop its checking on leaving");
            Assert.True(r.Checker.Batches.Count > 0);                                       // and the category asks for its own
            int s1 = r.Checker.Stops;
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.4);                        // back to TV: leaves the category
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.True(r.Checker.Stops > s1, "the category must stop its checking on leaving");
            int s2 = r.Checker.Stops;
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.4);                        // to Search: leaves TV
            Assert.IsType<SearchScreen>(r.App.Current);
            Assert.True(r.Checker.Stops > s2);
            int s3 = r.Checker.Stops;
            r.App.GoSection(Section.Tv); r.Advance(r.Now + 0.4);                            // and back: leaves Search
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.True(r.Checker.Stops > s3, "Search must stop its checking on leaving");
        }
    }
}
