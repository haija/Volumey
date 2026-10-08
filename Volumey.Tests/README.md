# Audio hang regression tests

Run on Windows with the project's .NET Core 3.1 SDK/runtime:

```powershell
dotnet test Volumey.Tests/Volumey.Tests.csproj -c Release -p:Platform=x64
```

`AudioHangRegressionTests` pauses an owning dispatcher, queues background process
removal, and reads the immutable process snapshot on the UI thread. With the
original semaphore implementation, the UI waits for a semaphore held by the
removal task, which is itself waiting for UI work. The five-second deadline
detects that cycle without hanging the test runner.

`AudioNotificationTests` invokes native callback entry points while their owning
dispatcher is paused. It checks callback return, ordered UI delivery, cleanup
outside callbacks, late-event suppression, stale volume/mute filtering, and
copying the borrowed endpoint-volume payload before Windows reuses its buffer.

The test adapter is explicitly referenced so `dotnet test` discovers xUnit tests.
Parallel test collections are disabled because existing tests mutate shared
`SettingsProvider` and `HotkeysControl` state. Asynchronous model tests await
their operations and assert after draining the owning dispatcher.

Manual follow-up should exercise idle periods, wake from sleep, hotkeys while
apps start and stop playing audio, and audio device connect/disconnect. Passing
the regression suite does not establish that every possible driver or long-lived
hang is fixed.
