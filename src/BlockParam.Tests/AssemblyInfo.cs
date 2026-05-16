using Xunit;

// WPF has process-wide static state (default collection views, theme/
// resource dictionaries, TypeDescriptor) that is not safe to touch from
// multiple STA threads at once. xUnit runs test collections in parallel
// by default, so [UIFact] classes across files raced and intermittently
// threw ArgumentOutOfRangeException inside a WPF control constructor
// (e.g. PillMultiSelect). The suite runs in ~7s, so serializing is a
// negligible cost for removing the entire class of WPF/STA flakiness.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
