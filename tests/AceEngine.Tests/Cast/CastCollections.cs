using Xunit;

namespace AceEngine.Tests.Cast
{
    /// <summary>
    /// The sending tests that run a whole engine over simulated peers, real sockets or a lot of CPU. They run alone, after the parallel collections: next to the timing-sensitive tests of the
    /// rest of the suite (the checker ones race on pool threads) they made those fail under load.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class CastIsolatedCollection
    {
        public const string Name = "Cast isolated";
    }
}
