using Xunit;

// The command catalogue is process-wide state, built on first use and deliberately
// never built by a config that parses cleanly. CommandCatalogueTests asserts exactly
// that, which means it has to be able to reset the catalogue and then observe that
// nothing rebuilt it.
//
// Run in parallel, any other class in this assembly that parses a bad command - and
// several do, because reporting bad commands well is most of what this suite is for -
// builds the catalogue underneath that assertion. The test would fail for a reason
// having nothing to do with the property it is checking, and only sometimes.
//
// These tests are pure and take about 80 ms in total, so serialising them costs
// nothing worth measuring.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
