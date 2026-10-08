using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using log4net;
using Volumey.Controls;
using Volumey.CoreAudioWrapper.CoreAudio;
using Volumey.CoreAudioWrapper.Wrapper;
using Volumey.Helper;
using Volumey.ViewModel.Settings;

namespace Volumey.Model
{
	/// <summary>
	/// Represents an audio output device and provides its master and application audio sessions
	/// </summary>
	public sealed class OutputDeviceModel : INotifyPropertyChanged, IDisposable
	{
		public MasterSessionModel Master { get; }
		
		/// <summary>
		/// Provides collection of audio processes of the output device.
		/// Should be used directly only in the UI, other classes must call <see cref="GetImmutableProcesses"/> methods to access processes.
		/// </summary>
		public ObservableCollection<AudioProcessModel> Processes { get; }
		
		private IImmutableList<AudioProcessModel> _cachedImmutableProcesses;
		
		private Action<OutputDeviceModel> _disabled;
		internal event Action<OutputDeviceModel> Disabled
		{
			add
			{
				//Prevent multiple subscribing of the same event handler
				this._disabled -= value;
				this._disabled += value;
			}
			remove => this._disabled -= value;
		}
		internal event Action<AudioProcessModel> ProcessCreated;
		internal event Action<OutputDeviceModel> FormatChanged;
		public string Id => this.Master.Id;
		private readonly IDevice device;
		private readonly ISessionProvider sessionProvider;
		private readonly IDeviceStateNotificationHandler deviceStateEvents;
		private WAVEFORMATEX? currentStreamFormat;


		private readonly Dispatcher dispatcher = App.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
		private bool disposed;

		public OutputDeviceModel(IDevice device, IDeviceStateNotificationHandler deviceStateNotifications, ISessionProvider sessionProvider,
			MasterSessionModel master, List<AudioProcessModel> processes)
		{
			this.device = device ?? throw new ArgumentNullException(nameof(device));
			this.Master = master ?? throw new ArgumentNullException(nameof(master));
			if(processes == null)
				throw new ArgumentNullException(nameof(processes));

			this.Processes = new ObservableCollection<AudioProcessModel>(processes);
			this._cachedImmutableProcesses = this.Processes.ToImmutableList();

			foreach(AudioProcessModel process in processes)
			{
				process.Exited += OnProcessExited;
			}

			this.currentStreamFormat = this.device.GetDeviceFormat();

			this.deviceStateEvents = deviceStateNotifications;
			this.deviceStateEvents.DeviceDisabled += OnDeviceDisabled;
			this.deviceStateEvents.NameChanged += OnDeviceNameChanged;
			this.deviceStateEvents.IconPathChanged += OnIconPathChanged;
			this.deviceStateEvents.FormatChanged += OnFormatChanged;
			this.sessionProvider = sessionProvider;
			this.sessionProvider.SessionCreated += OnSessionCreated;
			
			this.Processes.CollectionChanged += OnProcessesCollectionChanged;
		}

		internal bool SetVolumeHotkeys(HotKey volUp, HotKey volDown)
			=> this.Master.SetVolumeHotkeys(volUp: volUp, volDown: volDown);

		internal void ResetVolumeHotkeys() => this.Master.ResetVolumeHotkeys();

		internal void SetStateNotificationMediator(AudioProcessStateNotificationMediator mediator)
			=> this.Master.SetStateMediator(mediator);

		internal void ResetStateNotificationMediator(bool force = false)
			=> this.Master.ResetStateMediator(force);

		internal bool SetMuteHotkey(HotKey key)
			=> this.Master.SetMuteHotkey(key);

		internal void ResetMuteHotkeys()
			=> this.Master.ResetMuteHotkey();

		// Publish complete snapshots on the UI thread. Readers never acquire a lock
		// that a background handler could hold while waiting for this dispatcher.
		internal Task<IImmutableList<AudioProcessModel>> GetImmutableProcessesAsync()
			=> Task.FromResult(GetImmutableProcesses());

		internal IImmutableList<AudioProcessModel> GetImmutableProcesses()
			=> Volatile.Read(ref _cachedImmutableProcesses);

		/// <summary>
		/// Invalidates cached immutable processes collection if the original collection was changed
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnProcessesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			Volatile.Write(ref _cachedImmutableProcesses, this.Processes.ToImmutableList());
		}

		private void OnIconPathChanged(string deviceId)
		{
			if(this.CompareId(deviceId))
			{
				dispatcher.Invoke(() =>
				{
					try
					{
						if(this.Master.Icon != null)
						{
							NativeMethods.DestroyIcon(this.Master.Icon.Handle);
							this.Master.Icon.Dispose();
						}
						this.Master.Icon = this.device.GetIcon();
						this.Master.IconSource = this.Master.Icon.GetAsImageSource();
					}
					catch { }
				});
			}
		}

		private void OnDeviceNameChanged(string deviceId)
		{
			if(this.CompareId(deviceId))
			{
				dispatcher.Invoke(() =>
				{
					try
					{
						string friendlyName = this.device.GetFriendlyName();
						string deviceDesc = this.device.GetDeviceDesc();
						this.Master.DeviceDesc = deviceDesc;
						this.Master.Name = friendlyName;
					}
					catch { }
				});
			}
		}

		private async void OnFormatChanged(string deviceId)
		{
			if(this.CompareId(deviceId))
			{
				try
				{
					await dispatcher.InvokeAsync(() =>
					{
						var newFormat = this.device.GetDeviceFormat();
						if(newFormat.HasValue && !newFormat.Value.Equals(this.currentStreamFormat))
						{
							currentStreamFormat = newFormat.Value;
							this.FormatChanged?.Invoke(this);
						}
					});
				}
				catch { }
			}
		}

		private void OnDeviceDisabled(string deviceId)
		{
			if(this.CompareId(deviceId))
				this._disabled?.Invoke(this);
		}

		private async void OnProcessExited(AudioProcessModel exitedProcess)
		{
			try { await OnProcessExitedAsync(exitedProcess); }
			catch(OperationCanceledException) when(dispatcher.HasShutdownStarted) { }
			catch(Exception e) { LogManager.GetLogger(typeof(OutputDeviceModel)).Error("Failed to remove exited audio process", e); }
		}

		private async Task OnProcessExitedAsync(AudioProcessModel exitedProcess)
		{
			if(dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
				return;
			await dispatcher.InvokeAsync(() =>
			{
				if(disposed)
					return;
				exitedProcess.Exited -= OnProcessExited;
				this.Processes.Remove(exitedProcess);
				exitedProcess.Dispose();
			});
		}

		private async void OnSessionCreated(object sender, SessionCreatedEventArgs e)
		{
			try
			{
				await AddSessionAsync(e);
			}
			catch(OperationCanceledException) when(dispatcher.HasShutdownStarted) { }
			catch(Exception exception) { LogManager.GetLogger(typeof(OutputDeviceModel)).Error("Failed to add audio session", exception); }
		}

		private async Task AddSessionAsync(SessionCreatedEventArgs e)
		{
			if(dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
				return;
			await dispatcher.InvokeAsync(() =>
			{
				if(disposed)
				{
					e.SessionModel.Dispose();
					return;
				}
				if(Processes.FirstOrDefault(p => p.GroupingParam.Equals(e.SessionModel.GroupingParam) ||
											     p.FilePath.Equals(e.SessionModel.FilePath)) is AudioProcessModel
				   process)
				{
					e.SessionModel.Name = process.Name;
					process.AddSession(e.SessionModel);
				}
				else
				{
					AudioProcessModel newProcess = e.SessionModel.GetProcessModelFromSessionModel(e.SessionControl);
					if(newProcess == null)
					{
						e.SessionModel.Dispose();
						return;
					}
					newProcess.AddSession(e.SessionModel);
					newProcess.Exited += OnProcessExited;
					this.Processes.Add(newProcess);
					this.ProcessCreated?.Invoke(newProcess);
				}
			});
		}

		internal bool CompareId(string deviceId) =>
			string.Compare(this.Id, deviceId, StringComparison.InvariantCulture) == 0;

		public event PropertyChangedEventHandler PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		public void Dispose()
		{
			if(!dispatcher.CheckAccess())
			{
				dispatcher.BeginInvoke(new Action(Dispose));
				return;
			}
			if(disposed)
				return;
			disposed = true;
			this.sessionProvider.SessionCreated -= OnSessionCreated;
			this.sessionProvider.Dispose();
			this.deviceStateEvents.DeviceDisabled -= OnDeviceDisabled;
			this.deviceStateEvents.NameChanged -= OnDeviceNameChanged;
			this.deviceStateEvents.IconPathChanged -= OnIconPathChanged;
			this.deviceStateEvents.FormatChanged -= OnFormatChanged;
			this.Master.Dispose();

			foreach(var process in this.Processes)
			{
				process.Exited -= OnProcessExited;
				process.Dispose();
			}
			this.Processes.Clear();
			this.Processes.CollectionChanged -= OnProcessesCollectionChanged;
			this._disabled = null;
			this.ProcessCreated = null;
		}
	}
}
