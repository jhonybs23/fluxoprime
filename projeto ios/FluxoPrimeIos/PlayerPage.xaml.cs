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
			: $"nativeState={native.PlaybackState}; playWhenReady={native.PlayWhenReady}; isPlaying={native.IsPlaying}; pos={native.CurrentPositionMs}; buf={native.BufferedPositionMs}; totalBuf={native.TotalBufferedDurationMs}; loading={native.IsLoading}";
		AppLog.Info($"LIVE health {PlayerPlatformLabel} {_vm.Stream.Id}: maui={state}; semAvanco={sinceProgress.TotalSeconds:0}s; pos={MediaPlayer.Position.TotalSeconds:0}s; {nativeText}");
	}

	private NativePlaybackSnapshot? TryGetNativePlaybackSnapshot()
	{
		return null;
	}

	private object? TryGetNativePlayer()
	{
		return null;
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
		return typeName.Contains("Player", StringComparison.OrdinalIgnoreCase)
			&& ReadMember(value, "PlaybackState") is not null;
	}

	private static object? FindMemberByTypeName(object source, string typeNamePart)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		foreach (var property in source.GetType().GetProperties(flags))
		{
			if (property.GetIndexParameters().Length > 0)
			{
				continue;
			}

			if (property.PropertyType.FullName?.Contains(typeNamePart, StringComparison.OrdinalIgnoreCase) == true)
			{
				return property.GetValue(source);
			}
		}

		foreach (var field in source.GetType().GetFields(flags))
		{
			if (field.FieldType.FullName?.Contains(typeNamePart, StringComparison.OrdinalIgnoreCase) == true)
			{
				return field.GetValue(source);
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
			return property.GetValue(source);
		}

		var field = type.GetField(name, flags);
		if (field is not null)
		{
			return field.GetValue(source);
		}

		var getter = type.GetMethod("Get" + name, flags, null, Type.EmptyTypes, null);
		return getter?.Invoke(source, null);
	}

	private static int ReadIntMember(object source, string name, int fallback)
	{
		var value = ReadMember(source, name);
		return value is null ? fallback : Convert.ToInt32(value);
	}

	private static long ReadLongMember(object source, string name, long fallback)
	{
		var value = ReadMember(source, name);
		return value is null ? fallback : Convert.ToInt64(value);
	}

	private static bool ReadBoolMember(object source, string name)
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
		if (booleanValue?.Invoke(value, null) is bool javaBool)
		{
			return javaBool;
		}

		return Convert.ToBoolean(value);
	}

	private sealed class NativePlaybackSnapshot
	{
		public int PlaybackState { get; init; }
		public long CurrentPositionMs { get; init; }
		public long BufferedPositionMs { get; init; }
		public long TotalBufferedDurationMs { get; init; }
		public bool PlayWhenReady { get; init; }
		public bool IsPlaying { get; init; }
		public bool IsLoading { get; init; }
	}
}
