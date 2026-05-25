using FluxoPrimeCore;
using FluxoPrimeMaui.ViewModels;
using CommunityToolkit.Maui.Core.Primitives;
using System.Reflection;

namespace FluxoPrimeMaui;

public partial class PlayerPage : ContentPage
{
	private static readonly TimeSpan LiveOpenTimeout = TimeSpan.FromSeconds(18);
	private static readonly TimeSpan LiveBufferingTimeout = TimeSpan.FromSeconds(12);
	private static readonly TimeSpan LiveNoProgressTimeout = TimeSpan.FromSeconds(18);
	private static readonly TimeSpan LiveBlindStallTimeout = TimeSpan.FromSeconds(75);
	private static readonly TimeSpan LiveRecoveryCooldown = TimeSpan.FromSeconds(9);
	private static readonly TimeSpan LiveRecoveryResetWindow = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan LivePulseDelay = TimeSpan.FromMilliseconds(350);
	private const int NativeStateIdle = 1;
	private const int NativeStateBuffering = 2;
	private const int NativeStateReady = 3;
	private const int NativeStateEnded = 4;
	private static string PlayerPlatformLabel
	{
		get => "iOS";
	}

	private readonly PlayerViewModel _vm;
	private IDispatcherTimer? _healthTimer;
	private TimeSpan _lastPosition = TimeSpan.Zero;
	private DateTimeOffset _lastPlaybackStartUtc = DateTimeOffset.MinValue;
	private DateTimeOffset _lastProgressUtc = DateTimeOffset.MinValue;
	private DateTimeOffset _lastStateChangeUtc = DateTimeOffset.MinValue;
	private DateTimeOffset _lastRecoveryUtc = DateTimeOffset.MinValue;
	private DateTimeOffset _lastWatchdogLogUtc = DateTimeOffset.MinValue;
	private string _currentUrl = "";
	private bool _hasOpened;
	private bool _hasPositionSignal;
	private bool _hasNativePositionSignal;
	private bool _isActive;
	private bool _isRecovering;
	private bool _nativeSnapshotExceptionLogged;
	private long _lastNativePositionMs = -1;
	private int _recoveryCount;

	public PlayerPage(PlayerViewModel vm)
	{
		InitializeComponent();
		_vm = vm;
		BindingContext = vm;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_isActive = true;
		_recoveryCount = 0;
		_lastRecoveryUtc = DateTimeOffset.MinValue;
		DeviceDisplay.Current.KeepScreenOn = true;
		await _vm.InitializeAsync();
		if (_isActive && !string.IsNullOrWhiteSpace(_vm.StreamUrl))
		{
			await StartPlaybackAsync(_vm.StreamUrl, "inicial");
		}
		StartHealthWatchdog();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_isActive = false;
		StopHealthWatchdog();
		DeviceDisplay.Current.KeepScreenOn = false;
		_vm.SetRecovering(false);
		MediaPlayer.Stop();
		MediaPlayer.Source = null;
	}

	private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
	{
		_hasOpened = true;
		RecordObservedPosition(MediaPlayer.Position);
		MarkPlaybackProgress(MediaPlayer.Position);
		AppLog.Info($"{PlayerPlatformLabel} player abriu {_vm.Stream.Type}/{_vm.Stream.Id}");
	}

	private void MediaPlayer_MediaFailed(object? sender, MediaFailedEventArgs e)
	{
		AppLog.Warn($"{PlayerPlatformLabel} player falhou {_vm.Stream.Type}/{_vm.Stream.Id}: {e.ErrorMessage}");
		if (IsLiveStream)
		{
			_ = RecoverLivePlaybackAsync($"falha do player: {e.ErrorMessage}");
			return;
		}

		_vm.SetPlaybackError("Nao consegui reproduzir este conteudo agora.");
	}

	private void MediaPlayer_StateChanged(object? sender, MediaStateChangedEventArgs e)
	{
		_lastStateChangeUtc = DateTimeOffset.UtcNow;
		AppLog.Info($"{PlayerPlatformLabel} player estado {_vm.Stream.Type}/{_vm.Stream.Id}: {e.PreviousState} -> {e.NewState}");
		if (e.NewState == MediaElementState.Playing)
		{
			RecordObservedPosition(MediaPlayer.Position);
			MarkPlaybackProgress(MediaPlayer.Position);
		}
	}

	private void MediaPlayer_PositionChanged(object? sender, MediaPositionChangedEventArgs e)
	{
		RecordObservedPosition(e.Position);
	}

	private bool IsLiveStream => string.Equals(_vm.Stream.Type, "live", StringComparison.OrdinalIgnoreCase);

	private async Task StartPlaybackAsync(string url, string reason)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return;
		}

		_currentUrl = url;
		ResetPlaybackHealth();
		AppLog.Info($"{PlayerPlatformLabel} player start {_vm.Stream.Type}/{_vm.Stream.Id}: {reason}");

		try
		{
			MediaPlayer.Stop();
		}
		catch (Exception ex)
		{
			AppLog.Error(ex, $"{PlayerPlatformLabel} player stop before start");
		}

		MediaPlayer.Source = null;
		await Task.Delay(250);
		if (!_isActive)
		{
			return;
		}

		MediaPlayer.Source = url;
		try
		{
			MediaPlayer.Play();
		}
		catch (Exception ex)
		{
			AppLog.Error(ex, $"{PlayerPlatformLabel} player play");
		}
	}

	private void StartHealthWatchdog()
	{
		StopHealthWatchdog();
		if (!IsLiveStream)
		{
			return;
		}

		_healthTimer = Dispatcher.CreateTimer();
		_healthTimer.Interval = TimeSpan.FromSeconds(2);
		_healthTimer.Tick += (_, _) => _ = CheckLiveHealthAsync();
		_healthTimer.Start();
	}

	private void StopHealthWatchdog()
	{
		if (_healthTimer is null)
		{
			return;
		}

		_healthTimer.Stop();
		_healthTimer = null;
	}

	private async Task CheckLiveHealthAsync()
	{
		if (!_isActive || !IsLiveStream || _isRecovering || string.IsNullOrWhiteSpace(_currentUrl))
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		if (_recoveryCount > 0 && now - _lastRecoveryUtc > LiveRecoveryResetWindow)
		{
			_recoveryCount = 0;
		}

		var state = MediaPlayer.CurrentState;
		RecordObservedPosition(MediaPlayer.Position);
		var native = TryGetNativePlaybackSnapshot();
		if (native is not null)
		{
			RecordNativePlaybackProgress(native);
		}

		var sinceStart = now - _lastPlaybackStartUtc;
		var sinceStateChange = now - _lastStateChangeUtc;
		var sinceProgress = now - _lastProgressUtc;
		string? reason = null;

		if (!_hasOpened && sinceStart > LiveOpenTimeout)
		{
			reason = $"nao abriu em {sinceStart.TotalSeconds:0}s";
		}
		else if ((state == MediaElementState.Opening || state == MediaElementState.Buffering)
			&& sinceStateChange > LiveBufferingTimeout)
		{
			reason = $"preso em {state} por {sinceStateChange.TotalSeconds:0}s";
		}
		else if (state == MediaElementState.Failed)
		{
			reason = "estado Failed";
		}
		else if (native?.HasFailed == true)
		{
			reason = $"AVPlayer falhou item={native.ItemStatus}";
		}
		else if (native?.IsWaitingForBuffer == true
			&& sinceProgress > LiveBufferingTimeout)
		{
			reason = $"AVPlayer aguardando buffer por {sinceProgress.TotalSeconds:0}s";
		}
		else if (native?.IsApplePlayer == true
			&& native.IsPlaying
			&& _hasNativePositionSignal
			&& sinceProgress > LiveNoProgressTimeout)
		{
			reason = $"AVPlayer sem avanco por {sinceProgress.TotalSeconds:0}s";
		}
		else if (native is { PlaybackState: NativeStateIdle or NativeStateEnded })
		{
			reason = $"player nativo estado {native.PlaybackState}";
		}
		else if (native is { PlaybackState: NativeStateBuffering }
			&& sinceProgress > LiveBufferingTimeout)
		{
			reason = $"player nativo buffering por {sinceProgress.TotalSeconds:0}s";
		}
		else if (native is { PlaybackState: NativeStateReady, PlayWhenReady: true, IsPlaying: false }
			&& sinceProgress > LiveNoProgressTimeout)
		{
			reason = $"player nativo pronto sem tocar por {sinceProgress.TotalSeconds:0}s";
		}
		else if (native is { PlaybackState: NativeStateReady, PlayWhenReady: true }
			&& _hasNativePositionSignal
			&& sinceProgress > LiveNoProgressTimeout)
		{
			reason = $"player nativo sem avanco por {sinceProgress.TotalSeconds:0}s";
		}
		else if (state == MediaElementState.Playing
			&& _hasPositionSignal
			&& sinceProgress > LiveNoProgressTimeout)
		{
			reason = $"sem avanco por {sinceProgress.TotalSeconds:0}s";
		}
		else if (state == MediaElementState.Playing
			&& _hasOpened
			&& !_hasPositionSignal
			&& !_hasNativePositionSignal
			&& sinceStart > LiveBlindStallTimeout
			&& sinceProgress > LiveBlindStallTimeout)
		{
			reason = $"sem sinal confiavel de progresso por {sinceProgress.TotalSeconds:0}s";
		}

		if (reason is not null)
		{
			await RecoverLivePlaybackAsync(reason);
		}
		else
		{
			LogLiveHealthIfNeeded(now, state, native, sinceProgress);
		}
	}

	private async Task RecoverLivePlaybackAsync(string reason)
	{
		if (!_isActive || !IsLiveStream || _isRecovering)
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		if (now - _lastRecoveryUtc < LiveRecoveryCooldown)
		{
			return;
		}

		_isRecovering = true;
		_lastRecoveryUtc = now;
		_recoveryCount++;
		var requiresReload = reason.Contains("Failed", StringComparison.OrdinalIgnoreCase)
			|| reason.Contains("falha", StringComparison.OrdinalIgnoreCase)
			|| reason.Contains("nao abriu", StringComparison.OrdinalIgnoreCase)
			|| reason.Contains("estado 1", StringComparison.OrdinalIgnoreCase)
			|| reason.Contains("estado 4", StringComparison.OrdinalIgnoreCase);
		var pulseOnly = _recoveryCount == 1 && !requiresReload;
		var restartSameUrl = !pulseOnly && _recoveryCount <= 2;
		var renewUrl = _recoveryCount >= 3;
		var preferHls = _vm.PrefersLiveHls || (_recoveryCount >= 4 && _recoveryCount % 2 == 0);
		var mode = pulseOnly ? "pulse" : restartSameUrl ? "restart" : preferHls ? "renew-hls" : "renew-ts";
		AppLog.Warn($"LIVE watchdog {PlayerPlatformLabel} {_vm.Stream.Id}: tentativa {_recoveryCount}, modo={mode}, motivo={reason}");
		_vm.SetRecovering(true);

		try
		{
			if (pulseOnly)
			{
				await PulsePlaybackAsync(reason);
			}
			else if (restartSameUrl)
			{
				await StartPlaybackAsync(_currentUrl, "recover mesma url");
			}
			else if (renewUrl)
			{
				var newUrl = await _vm.RenewLiveUrlAsync(preferHls);
				if (!string.IsNullOrWhiteSpace(newUrl))
				{
					await StartPlaybackAsync(newUrl, preferHls ? "recover hls" : "recover url");
				}
			}
		}
		finally
		{
			_vm.SetRecovering(false);
			_isRecovering = false;
		}
	}

	private async Task PulsePlaybackAsync(string reason)
	{
		AppLog.Warn($"LIVE pulse iOS {_vm.Stream.Id}: {reason}");
		try
		{
			MediaPlayer.Pause();
			await Task.Delay(LivePulseDelay);
			if (!_isActive)
			{
				return;
			}

			MediaPlayer.Play();
			_lastStateChangeUtc = DateTimeOffset.UtcNow;
			MarkPlaybackProgress(MediaPlayer.Position);
		}
		catch (Exception ex)
		{
			AppLog.Error(ex, $"{PlayerPlatformLabel} live pulse");
			await StartPlaybackAsync(_currentUrl, "pulse fallback");
		}
	}

	private void RecordObservedPosition(TimeSpan position)
	{
		var movedForward = position > _lastPosition + TimeSpan.FromMilliseconds(250);
		var jumpedBack = _lastPosition > TimeSpan.Zero && position < _lastPosition - TimeSpan.FromSeconds(2);
		if (position > TimeSpan.Zero)
		{
			_hasPositionSignal = true;
		}

		if (movedForward || jumpedBack)
		{
			MarkPlaybackProgress(position);
		}
	}

	private void RecordNativePlaybackProgress(NativePlaybackSnapshot native)
	{
		if (native.CurrentPositionMs <= 0)
		{
			return;
		}

		var movedForward = native.CurrentPositionMs > _lastNativePositionMs + 250;
		var jumpedBack = _lastNativePositionMs > 0 && native.CurrentPositionMs < _lastNativePositionMs - 2000;
		if (_lastNativePositionMs < 0 || movedForward || jumpedBack)
		{
			_hasNativePositionSignal = true;
			_lastNativePositionMs = native.CurrentPositionMs;
			MarkPlaybackProgress(TimeSpan.FromMilliseconds(native.CurrentPositionMs));
		}
	}

	private void MarkPlaybackProgress(TimeSpan position)
	{
		_lastPosition = position;
		_lastProgressUtc = DateTimeOffset.UtcNow;
	}

	private void ResetPlaybackHealth()
	{
		var now = DateTimeOffset.UtcNow;
		_hasOpened = false;
		_hasPositionSignal = false;
		_hasNativePositionSignal = false;
		_lastPosition = TimeSpan.Zero;
		_lastNativePositionMs = -1;
		_lastPlaybackStartUtc = now;
		_lastProgressUtc = now;
		_lastStateChangeUtc = now;
	}

	private void LogLiveHealthIfNeeded(DateTimeOffset now, MediaElementState state, NativePlaybackSnapshot? native, TimeSpan sinceProgress)
	{
		if (now - _lastWatchdogLogUtc < TimeSpan.FromMinutes(1))
		{
			return;
		}

		_lastWatchdogLogUtc = now;
		var nativeText = native is null
			? "native=indisponivel"
			: native.IsApplePlayer
				? $"native={native.NativeKind}; time={native.TimeControlStatus}; item={native.ItemStatus}; rate={native.Rate:0.##}; empty={native.PlaybackBufferEmpty}; keepUp={native.PlaybackLikelyToKeepUp}; pos={native.CurrentPositionMs}; buf={native.BufferedPositionMs}; totalBuf={native.TotalBufferedDurationMs}; loading={native.IsLoading}"
				: $"native={native.NativeKind}; nativeState={native.PlaybackState}; playWhenReady={native.PlayWhenReady}; isPlaying={native.IsPlaying}; pos={native.CurrentPositionMs}; buf={native.BufferedPositionMs}; totalBuf={native.TotalBufferedDurationMs}; loading={native.IsLoading}";
		AppLog.Info($"LIVE health {PlayerPlatformLabel} {_vm.Stream.Id}: maui={state}; semAvanco={sinceProgress.TotalSeconds:0}s; pos={MediaPlayer.Position.TotalSeconds:0}s; {nativeText}");
	}

	private NativePlaybackSnapshot? TryGetNativePlaybackSnapshot()
	{
		try
		{
			var nativePlayer = TryGetNativePlayer();
			if (nativePlayer is null)
			{
				return null;
			}

			var playbackState = ReadMember(nativePlayer, "PlaybackState");
			if (playbackState is not null)
			{
				return new NativePlaybackSnapshot
				{
					NativeKind = "ExoPlayer",
					PlaybackState = Convert.ToInt32(playbackState),
					CurrentPositionMs = ReadLongMember(nativePlayer, "CurrentPosition", 0),
					BufferedPositionMs = ReadLongMember(nativePlayer, "BufferedPosition", 0),
					TotalBufferedDurationMs = ReadLongMember(nativePlayer, "TotalBufferedDuration", 0),
					PlayWhenReady = ReadBoolMember(nativePlayer, "PlayWhenReady"),
					IsPlaying = ReadBoolMember(nativePlayer, "IsPlaying"),
					IsLoading = ReadBoolMember(nativePlayer, "IsLoading")
				};
			}

			var timeControlStatusValue = ReadMember(nativePlayer, "TimeControlStatus");
			if (timeControlStatusValue is null)
			{
				return null;
			}

			var currentItem = ReadMember(nativePlayer, "CurrentItem");
			var timeControlStatus = timeControlStatusValue.ToString() ?? "";
			var itemStatus = ReadMember(currentItem, "Status")?.ToString() ?? "";
			var rate = ReadDoubleMember(nativePlayer, "Rate", 0);
			var currentPositionMs = ReadTimeMilliseconds(ReadMember(nativePlayer, "CurrentTime"));
			var bufferedPositionMs = currentItem is null ? 0 : ReadBufferedPositionMilliseconds(currentItem);
			var bufferEmpty = ReadBoolMember(currentItem, "PlaybackBufferEmpty");
			var bufferLikely = ReadBoolMember(currentItem, "PlaybackLikelyToKeepUp");
			var bufferFull = ReadBoolMember(currentItem, "PlaybackBufferFull");
			var isWaiting = ContainsIgnoreCase(timeControlStatus, "Waiting");
			var isPlaying = rate > 0 || ContainsIgnoreCase(timeControlStatus, "Playing");

			return new NativePlaybackSnapshot
			{
				NativeKind = "AVPlayer",
				PlaybackState = ContainsIgnoreCase(itemStatus, "Failed")
					? NativeStateIdle
					: isWaiting ? NativeStateBuffering : NativeStateReady,
				CurrentPositionMs = currentPositionMs,
				BufferedPositionMs = bufferedPositionMs,
				TotalBufferedDurationMs = Math.Max(0, bufferedPositionMs - currentPositionMs),
				PlayWhenReady = !ContainsIgnoreCase(timeControlStatus, "Paused"),
				IsPlaying = isPlaying,
				IsLoading = isWaiting || bufferEmpty,
				TimeControlStatus = timeControlStatus,
				ItemStatus = itemStatus,
				Rate = rate,
				PlaybackBufferEmpty = bufferEmpty,
				PlaybackLikelyToKeepUp = bufferLikely,
				PlaybackBufferFull = bufferFull
			};
		}
		catch (Exception ex)
		{
			if (!_nativeSnapshotExceptionLogged)
			{
				_nativeSnapshotExceptionLogged = true;
				AppLog.Error(ex, $"{PlayerPlatformLabel} native playback snapshot");
			}

			return null;
		}
	}

	private object? TryGetNativePlayer()
	{
		var handler = MediaPlayer.Handler;
		var platformView = ReadMember(handler, "PlatformView");
		var mediaManager = ReadMember(handler, "mediaManager")
			?? ReadMember(handler, "MediaManager")
			?? FindMemberByTypeName(handler, "MediaManager");

		return TryResolveNativePlayer(MediaPlayer)
			?? TryResolveNativePlayer(handler)
			?? TryResolveNativePlayer(platformView)
			?? TryResolveNativePlayer(mediaManager);
	}

	private static object? TryResolveNativePlayer(object? source)
	{
		if (source is null)
		{
			return null;
		}

		if (LooksLikeNativePlayer(source))
		{
			return source;
		}

		var directPlayer = ReadMember(source, "Player");
		if (LooksLikeNativePlayer(directPlayer))
		{
			return directPlayer;
		}

		var playerViewController = ReadMember(source, "PlayerViewController");
		var avPlayer = ReadMember(playerViewController, "Player");
		if (LooksLikeNativePlayer(avPlayer))
		{
			return avPlayer;
		}

		var playerView = ReadMember(source, "PlayerView") ?? FindMemberByTypeName(source, "StyledPlayerView");
		if (playerView is null)
		{
			return null;
		}

		var player = ReadMember(playerView, "Player") ?? ReadMember(playerView, "player");
		return LooksLikeNativePlayer(player) ? player : null;
	}

	private static bool LooksLikeNativePlayer(object? value)
	{
		if (value is null)
		{
			return false;
		}

		var typeName = value.GetType().FullName ?? "";
		var shortName = value.GetType().Name;
		return shortName.Equals("AVPlayer", StringComparison.OrdinalIgnoreCase)
			|| ReadMember(value, "TimeControlStatus") is not null
			|| (typeName.Contains("Player", StringComparison.OrdinalIgnoreCase)
				&& ReadMember(value, "PlaybackState") is not null);
	}

	private static object? FindMemberByTypeName(object? source, string typeNamePart)
	{
		if (source is null)
		{
			return null;
		}

		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		foreach (var property in source.GetType().GetProperties(flags))
		{
			if (property.GetIndexParameters().Length > 0)
			{
				continue;
			}

			if (property.PropertyType.FullName?.Contains(typeNamePart, StringComparison.OrdinalIgnoreCase) == true)
			{
				try
				{
					return property.GetValue(source);
				}
				catch
				{
					continue;
				}
			}
		}

		foreach (var field in source.GetType().GetFields(flags))
		{
			if (field.FieldType.FullName?.Contains(typeNamePart, StringComparison.OrdinalIgnoreCase) == true)
			{
				try
				{
					return field.GetValue(source);
				}
				catch
				{
					continue;
				}
			}
		}

		return null;
	}

	private static object? ReadMember(object? source, string name)
	{
		if (source is null)
		{
			return null;
		}

		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		var type = source.GetType();
		var property = type.GetProperty(name, flags);
		if (property is not null && property.GetIndexParameters().Length == 0)
		{
			try
			{
				return property.GetValue(source);
			}
			catch
			{
				return null;
			}
		}

		var field = type.GetField(name, flags);
		if (field is not null)
		{
			try
			{
				return field.GetValue(source);
			}
			catch
			{
				return null;
			}
		}

		var getter = type.GetMethod("Get" + name, flags, null, Type.EmptyTypes, null);
		if (getter is not null)
		{
			try
			{
				return getter.Invoke(source, null);
			}
			catch
			{
				return null;
			}
		}

		var method = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
		if (method is null)
		{
			return null;
		}

		try
		{
			return method.Invoke(source, null);
		}
		catch
		{
			return null;
		}
	}

	private static int ReadIntMember(object source, string name, int fallback)
	{
		var value = ReadMember(source, name);
		try
		{
			return value is null ? fallback : Convert.ToInt32(value);
		}
		catch
		{
			return fallback;
		}
	}

	private static long ReadLongMember(object? source, string name, long fallback)
	{
		var value = ReadMember(source, name);
		try
		{
			return value is null ? fallback : Convert.ToInt64(value);
		}
		catch
		{
			return fallback;
		}
	}

	private static double ReadDoubleMember(object? source, string name, double fallback)
	{
		var value = ReadMember(source, name);
		return TryReadDouble(value, out var result) ? result : fallback;
	}

	private static bool ReadBoolMember(object? source, string name)
	{
		var value = ReadMember(source, name);
		if (value is null)
		{
			return false;
		}

		if (value is bool boolValue)
		{
			return boolValue;
		}

		var booleanValue = value.GetType().GetMethod("BooleanValue", BindingFlags.Instance | BindingFlags.Public);
		try
		{
			if (booleanValue?.Invoke(value, null) is bool javaBool)
			{
				return javaBool;
			}
		}
		catch
		{
		}

		try
		{
			return Convert.ToBoolean(value);
		}
		catch
		{
			return false;
		}
	}

	private static long ReadTimeMilliseconds(object? timeValue)
	{
		if (timeValue is null)
		{
			return 0;
		}

		if (timeValue is TimeSpan timeSpan)
		{
			return Math.Max(0, (long)timeSpan.TotalMilliseconds);
		}

		var secondsValue = ReadMember(timeValue, "Seconds");
		if (TryReadDouble(secondsValue, out var seconds) && double.IsFinite(seconds))
		{
			return Math.Max(0, (long)(seconds * 1000));
		}

		var value = ReadMember(timeValue, "Value");
		var timeScale = ReadMember(timeValue, "TimeScale") ?? ReadMember(timeValue, "Timescale");
		if (TryReadDouble(value, out var rawValue)
			&& TryReadDouble(timeScale, out var rawScale)
			&& rawScale > 0)
		{
			return Math.Max(0, (long)((rawValue / rawScale) * 1000));
		}

		return 0;
	}

	private static long ReadBufferedPositionMilliseconds(object item)
	{
		var ranges = ReadMember(item, "LoadedTimeRanges") as System.Collections.IEnumerable;
		if (ranges is null)
		{
			return 0;
		}

		var best = 0L;
		foreach (var rangeValue in ranges)
		{
			var range = ReadMember(rangeValue, "CMTimeRangeValue") ?? rangeValue;
			var endMs = ReadTimeMilliseconds(ReadMember(range, "End"));
			if (endMs <= 0)
			{
				var startMs = ReadTimeMilliseconds(ReadMember(range, "Start"));
				var durationMs = ReadTimeMilliseconds(ReadMember(range, "Duration"));
				endMs = startMs + durationMs;
			}

			best = Math.Max(best, endMs);
		}

		return best;
	}

	private static bool TryReadDouble(object? value, out double result)
	{
		result = 0;
		if (value is null)
		{
			return false;
		}

		try
		{
			result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool ContainsIgnoreCase(string value, string match) =>
		value.Contains(match, StringComparison.OrdinalIgnoreCase);

	private sealed class NativePlaybackSnapshot
	{
		public string NativeKind { get; init; } = "";
		public int PlaybackState { get; init; }
		public long CurrentPositionMs { get; init; }
		public long BufferedPositionMs { get; init; }
		public long TotalBufferedDurationMs { get; init; }
		public bool PlayWhenReady { get; init; }
		public bool IsPlaying { get; init; }
		public bool IsLoading { get; init; }
		public string TimeControlStatus { get; init; } = "";
		public string ItemStatus { get; init; } = "";
		public double Rate { get; init; }
		public bool PlaybackBufferEmpty { get; init; }
		public bool PlaybackLikelyToKeepUp { get; init; }
		public bool PlaybackBufferFull { get; init; }

		public bool IsApplePlayer => string.Equals(NativeKind, "AVPlayer", StringComparison.OrdinalIgnoreCase);
		public bool HasFailed => IsApplePlayer && ContainsIgnoreCase(ItemStatus, "Failed");
		public bool IsWaitingForBuffer =>
			IsApplePlayer
			&& ContainsIgnoreCase(TimeControlStatus, "Waiting")
			&& (PlaybackBufferEmpty || !PlaybackLikelyToKeepUp);
	}
}
