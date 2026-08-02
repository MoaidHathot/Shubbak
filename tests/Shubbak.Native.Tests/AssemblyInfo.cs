using Xunit;

// These tests manipulate the real desktop: they spawn processes, create top-level
// windows, cloak and hide them, and talk to shell COM interfaces that hand out
// per-thread proxies. Running them concurrently makes them interfere with one another
// and produces failures that have nothing to do with the code under test.
//
// Serial execution costs a couple of seconds and buys results that mean something.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
