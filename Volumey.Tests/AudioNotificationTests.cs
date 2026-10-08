using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Moq;
using Volumey.CoreAudioWrapper.CoreAudio;
using Volumey.CoreAudioWrapper.CoreAudio.Enums;
using Volumey.CoreAudioWrapper.CoreAudio.Interfaces;
using Volumey.CoreAudioWrapper.Wrapper;
using Xunit;

namespace Volumey.Tests
{
    public class AudioNotificationTests
    {
        // Keep the owning dispatcher paused while invoking callbacks from another
        // thread. A blocking callback or synchronous COM query fails this harness.
        internal static async Task OnDispatcher(Action<Action> test)
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                try
                {
                    test(() =>
                    {
                        var frame = new DispatcherFrame();
                        dispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.ApplicationIdle);
                        Dispatcher.PushFrame(frame);
                    });
                    completed.TrySetResult(true);
                }
                catch(Exception e) { completed.TrySetException(e); }
                finally { dispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(5))) == completed.Task,
                "Audio callback blocked while its UI dispatcher was paused.");
            await completed.Task;
        }

        private static Mock<IAudioSessionControl2> SessionControl()
        {
            var control = new Mock<IAudioSessionControl2>(MockBehavior.Strict);
            control.As<ISimpleAudioVolume>();
            control.Setup(c => c.UnregisterAudioSessionNotification(It.IsAny<IAudioSessionEvents>())).Returns(0);
            return control;
        }

        [Fact]
        public Task DeviceCallbacksReturnBeforeSubscribersRun() => OnDispatcher(drain =>
        {
            var enumerator = new Mock<IMMDeviceEnumerator>(MockBehavior.Strict);
            enumerator.Setup(e => e.RegisterEndpointNotificationCallback(It.IsAny<IMMNotificationClient>())).Returns(0);
            enumerator.Setup(e => e.UnregisterEndpointNotificationCallback(It.IsAny<IMMNotificationClient>())).Returns(0);
            using var handler = new DeviceStateNotificationsHandler(enumerator.Object);
            var events = new List<string>();
            var owner = Thread.CurrentThread.ManagedThreadId;
            handler.DefaultDeviceChanged += id => { Assert.Equal(owner, Thread.CurrentThread.ManagedThreadId); events.Add("default:" + id); };
            handler.DeviceDisabled += id => events.Add("disabled:" + id);
            handler.NameChanged += id => events.Add("name:" + id);

            Task.Run(() =>
            {
                handler.OnDefaultDeviceChanged(EDataFlow.Render, ERole.Console, "device");
                handler.OnDeviceStateChanged("device", DeviceState.Unplugged);
                handler.OnDeviceRemoved("device");
                handler.OnPropertyValueChanged("device", PROPERTYKEY.DeviceProperties.FriendlyName);
            }).GetAwaiter().GetResult();
            Assert.Empty(events);
            drain();
            Assert.Equal(new[] { "default:device", "disabled:device", "disabled:device", "name:device" }, events);
            // No GetDevice call is allowed for an unavailable endpoint.
        });

        [Fact]
        public Task SessionCallbacksAllowCleanupOutsideNativeCallback() => OnDispatcher(drain =>
        {
            var control = SessionControl();
            var handler = new AudioSessionStateNotifications(control.Object);
            var events = new List<string>();
            handler.NameChanged += name => events.Add(name);
            handler.StateChanged += state => events.Add(state.ToString());
            handler.SessionEnded += () => { events.Add("ended"); handler.Dispose(); };
            Task.Run(() =>
            {
                var context = Guid.Empty;
                handler.OnDisplayNameChanged("app", ref context);
                handler.OnStateChanged(AudioSessionState.Inactive);
                handler.OnStateChanged(AudioSessionState.Expired);
                handler.OnDisplayNameChanged("late", ref context);
            }).GetAwaiter().GetResult();
            Assert.Empty(events);
            control.Verify(c => c.UnregisterAudioSessionNotification(It.IsAny<IAudioSessionEvents>()), Times.Never);
            drain();
            Assert.Equal(new[] { "app", "Inactive", "ended" }, events);
            handler.Dispose();
            control.Verify(c => c.UnregisterAudioSessionNotification(It.IsAny<IAudioSessionEvents>()), Times.Once);
        });

        [Fact]
        public Task DisposedHandlersIgnoreQueuedNotifications() => OnDispatcher(drain =>
        {
            var control = SessionControl();
            var handler = new AudioSessionStateNotifications(control.Object);
            int count = 0;
            handler.StateChanged += _ => count++;
            handler.OnStateChanged(AudioSessionState.Active); // UI-thread callbacks must enqueue too.
            Assert.Equal(0, count);
            handler.Dispose();
            handler.OnStateChanged(AudioSessionState.Inactive);
            drain();
            Assert.Equal(0, count);
        });

        [Fact]
        public Task VolumeQueriesRunAfterCallbackAndDiscardStaleMuteChanges() => OnDispatcher(drain =>
        {
            var control = SessionControl();
            var volume = control.As<ISimpleAudioVolume>();
            float currentVolume = 0.5f;
            bool currentMute = true;
            volume.Setup(v => v.GetMasterVolume(out currentVolume)).Returns(0);
            volume.Setup(v => v.GetMute(out currentMute)).Returns(0);
            using var handler = new AudioSessionStateNotifications(control.Object);
            var changes = new List<VolumeChangedEventArgs>();
            handler.VolumeChanged += changes.Add;
            Task.Run(() =>
            {
                var context = Guid.Empty;
                handler.OnSimpleVolumeChanged(0.9f, false, ref context);
                handler.OnSimpleVolumeChanged(0.5f, false, ref context);
                handler.OnSimpleVolumeChanged(0.5f, true, ref context);
                handler.OnSimpleVolumeChanged(0.5f, true, ref GuidValue.Internal.VolumeGUID);
            }).GetAwaiter().GetResult();
            volume.Verify(v => v.GetMasterVolume(out currentVolume), Times.Never);
            drain();
            var change = Assert.Single(changes);
            Assert.Equal(50, change.NewVolume);
            Assert.True(change.IsMuted);
        });

        [Fact]
        public Task EndpointCallbackCopiesPayloadBeforeWindowsReleasesMemory() => OnDispatcher(drain =>
        {
            var volume = new Mock<IAudioEndpointVolume>(MockBehavior.Strict);
            float currentVolume = 0.4f;
            bool currentMute = true;
            volume.Setup(v => v.GetMasterVolumeLevelScalar(out currentVolume)).Returns(0);
            volume.Setup(v => v.GetMute(out currentMute)).Returns(0);
            volume.Setup(v => v.UnregisterControlChangeNotify(It.IsAny<IAudioEndpointVolumeCallback>())).Returns(0);
            using var handler = new MasterVolumeNotificationHandler(volume.Object);
            var changes = new List<VolumeChangedEventArgs>();
            handler.VolumeChanged += changes.Add;
            var payload = new AUDIO_VOLUME_NOTIFICATION_DATA { guidEventContext = Guid.Empty, masterVolume = 0.4f, isMuted = true };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIO_VOLUME_NOTIFICATION_DATA>());
            try
            {
                Marshal.StructureToPtr(payload, buffer, false);
                Task.Run(() => handler.OnNotify(buffer)).GetAwaiter().GetResult();
                volume.Verify(v => v.GetMasterVolumeLevelScalar(out currentVolume), Times.Never);
                // Overwrite then free the borrowed buffer before the UI handles the event.
                Marshal.StructureToPtr(default(AUDIO_VOLUME_NOTIFICATION_DATA), buffer, false);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            drain();
            var change = Assert.Single(changes);
            Assert.Equal(40, change.NewVolume);
            Assert.True(change.IsMuted);
        });

        [Fact]
        public Task SessionCreationDoesNotQueryComDuringCallback() => OnDispatcher(drain =>
        {
            var manager = new Mock<IAudioSessionManager2>(MockBehavior.Strict);
            manager.Setup(m => m.RegisterSessionNotification(It.IsAny<IAudioSessionNotification>())).Returns(0);
            manager.Setup(m => m.UnregisterSessionNotification(It.IsAny<IAudioSessionNotification>())).Returns(0);
            var control = SessionControl();
            var provider = new AudioSessionProvider(manager.Object);
            Task.Run(() => provider.OnSessionCreated(control.Object)).GetAwaiter().GetResult();
            // Dispose before draining: no queries against the late session are allowed.
            provider.Dispose();
            drain();
            manager.Verify(m => m.UnregisterSessionNotification(It.IsAny<IAudioSessionNotification>()), Times.Once);
        });
    }
}
