using Xunit;

namespace AceEngine.Tests
{
    /// <summary>
    /// Tests that simulate a live stream, peers or a player in real time with tight wall-clock margins. They run alone, after the parallel
    /// collections: under a busy thread pool (the rest of the suite in parallel) the simulated source falls behind and their timing
    /// assertions fail for reasons that have nothing to do with the code under test.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class RealtimeCollection
    {
        public const string Name = "Real-time (isolated)";
    }
}
