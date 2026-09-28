using Xunit;

// The product enforces one Core host per Desktop process (DesktopKernelFactory throws
// "Only one Core host can be loaded in a Desktop process."), which is correct for production but means two
// tests cannot start hosts at the same time. xUnit parallelises by default, so runs raced and tests failed
// with that exception depending on scheduling - the failures looked like flakes in unrelated adapters.
// Serialising this assembly fixes the test infrastructure rather than weakening the product rule.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
