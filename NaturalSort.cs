using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ModernImageViewer
{
    /// <summary>
    /// File and folder ordering that matches Windows Explorer.
    ///
    /// Ordinal comparison sorts digits as characters, so "10" lands between "1" and "2"
    /// and a folder of numbered images reads 1, 10, 11, ... 2, 20. Explorer instead
    /// compares runs of digits as numbers.
    ///
    /// This calls the same shell function Explorer itself uses rather than reimplementing
    /// the rule. A hand-written comparer has to guess at leading zeros, embedded spaces,
    /// punctuation and mixed digit runs, and any guess that differs is the same bug again
    /// in a narrower case.
    /// </summary>
    public static class NaturalSort
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);

        /// <summary>Orders two names the way Explorer's Name column does.</summary>
        public static int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return StrCmpLogicalW(x, y);
        }

        /// <summary>For <c>OrderBy</c> and other APIs that want a comparer instance.</summary>
        public static IComparer<string> Comparer { get; } = new NameComparer();

        private sealed class NameComparer : IComparer<string>
        {
            public int Compare(string? x, string? y) => NaturalSort.Compare(x, y);
        }
    }
}
