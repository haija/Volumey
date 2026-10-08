using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Moq;
using Volumey.CoreAudioWrapper.Wrapper;
using Volumey.Model;
using Xunit;

namespace Volumey.Tests
{
    public class AudioHangRegressionTests
    {
        [Fact]
        public async Task SnapshotReadDoesNotWaitForPendingProcessRemoval()
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiThread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                try
                {
                    var device = OutputDeviceModelTests.GetDeviceMock("111", "speakers", new Mock<IDeviceStateNotificationHandler>().Object);
                    var remove = typeof(OutputDeviceModel).GetMethod("OnProcessExitedAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    var snapshot = typeof(OutputDeviceModel).GetMethod("GetImmutableProcesses", BindingFlags.Instance | BindingFlags.NonPublic);

                    // The dispatcher is deliberately not pumping yet. The background handler
                    // must return its pending Task without requiring any UI work to complete.
                    Task removal = null;
                    Task.Run(() => { removal = (Task)remove.Invoke(device, new object[] { device.Processes[0] }); })
                        .GetAwaiter().GetResult();

                    // Before the fix this blocks on the semaphore held by removal, which
                    // cannot release it until this same dispatcher processes the removal.
                    Assert.NotNull(snapshot.Invoke(device, null));
                    dispatcher.BeginInvoke(new Action(() => dispatcher.InvokeShutdown()), DispatcherPriority.ApplicationIdle);
                    Dispatcher.Run();
                    removal.GetAwaiter().GetResult();
                    Assert.Empty(device.Processes);
                    completed.TrySetResult(true);
                }
                catch(Exception e) { completed.TrySetException(e); }
            }) { IsBackground = true };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(finished == completed.Task, "UI deadlocked while reading processes during background process removal.");
            await completed.Task;
        }
    }
}
