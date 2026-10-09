using System;
using System.Collections.Generic;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// One `suite` run's worth of metrics, serialized to JSON so a later run can be diffed against it with `compare`.
    /// Label is meant to be the git commit SHA of the code under test, supplied by the caller (e.g.
    /// `--label=$(git rev-parse --short HEAD)`), not auto-detected - keeps the tool simple and independent of
    /// being invoked from any particular working directory.
    /// </summary>
    public class BenchmarkReport
    {
        public string Label { get; set; }
        public DateTime TimestampUtc { get; set; }
        public Dictionary<string, MetricValue> Metrics { get; set; }
    }
}
