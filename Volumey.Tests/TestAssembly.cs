using Xunit;

// The existing tests share SettingsProvider and HotkeysControl static state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
