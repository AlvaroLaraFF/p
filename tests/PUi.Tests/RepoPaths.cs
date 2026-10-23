using System;
using System.IO;

namespace PUi.Tests
{
    /// <summary>Finds the repository root (the folder with P.sln) from the test's output directory, for the tests that read project and source files.</summary>
    internal static class RepoPaths
    {
        public static readonly string Root = Find();

        private static string Find()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "P.sln"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("P.sln not found above " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        public static string Combine(params string[] parts) { return Path.Combine(Root, Path.Combine(parts)); }
    }
}
