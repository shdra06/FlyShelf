using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FlyShelf.ViewModels
{
    public partial class ClipboardItem
    {
        // Shared static player to ensure only ONE audio plays globally across all cards (no overlapping sounds)
        // [FIX M-8]: Lazy init to ensure MediaPlayer is created on UI thread
        private static MediaPlayer? _sharedPlayer;
        private static MediaPlayer SharedPlayer => _sharedPlayer ??= InitSharedPlayer();

        private static MediaPlayer InitSharedPlayer()
        {
            var player = System.Windows.Application.Current?.Dispatcher?.Invoke(() => new MediaPlayer()) ?? new MediaPlayer();
            player.MediaOpened += SharedPlayer_MediaOpened;
            player.MediaEnded += SharedPlayer_MediaEnded;
            player.MediaFailed += SharedPlayer_MediaFailed;
            return player;
        }
        private static ClipboardItem? _playingItem;
        private static DispatcherTimer? _playbackTimer;
        private static bool _isUpdatingPositionFromTimer = false;

        static ClipboardItem()
        {
            // Event hookup is now done in InitSharedPlayer() during lazy initialization
        }

        // --- PROPERTIES ---

        private bool _isAudioPlaying;
        [JsonIgnore]
        public bool IsAudioPlaying
        {
            get => _isAudioPlaying;
            set
            {
                if (_isAudioPlaying != value)
                {
                    _isAudioPlaying = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioPlaying)));
                }
            }
        }

        private double _audioPosition;
        [JsonIgnore]
        public double AudioPosition
        {
            get => _audioPosition;
            set
            {
                if (Math.Abs(_audioPosition - value) > 0.01)
                {
                    _audioPosition = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioPosition)));
                    
                    // If the user seeks from the UI, update the media player position
                    if (!_isUpdatingPositionFromTimer && _playingItem == this)
                    {
                        SharedPlayer.Position = TimeSpan.FromSeconds(value);
                        UpdatePlaybackText();
                    }
                }
            }
        }

        private double _audioDuration;
        [JsonIgnore]
        public double AudioDuration
        {
            get => _audioDuration;
            set
            {
                if (Math.Abs(_audioDuration - value) > 0.01)
                {
                    _audioDuration = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioDuration)));
                }
            }
        }

        private string _audioPlaybackText = "0:00 / 0:00";
        [JsonIgnore]
        public string AudioPlaybackText
        {
            get => _audioPlaybackText;
            set
            {
                if (_audioPlaybackText != value)
                {
                    _audioPlaybackText = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioPlaybackText)));
                }
            }
        }

        private ICommand? _playPauseAudioCommand;
        [JsonIgnore]
        public ICommand PlayPauseAudioCommand
        {
            get
            {
                if (_playPauseAudioCommand == null)
                {
                    _playPauseAudioCommand = new RelayCommand(ToggleAudioPlayback);
                }
                return _playPauseAudioCommand;
            }
        }

        // --- PLAYBACK CONTROLS ---

        private void ToggleAudioPlayback()
        {
            if (IsAudioPlaying)
            {
                PauseAudio();
            }
            else
            {
                PlayAudio();
            }
        }

        private void PlayAudio()
        {
            try
            {
                // Stop any other currently playing item cleanly
                if (_playingItem != null && _playingItem != this)
                {
                    _playingItem.StopAudioInternal();
                }

                _playingItem = this;

                // Determine if we need to load/open the audio source
                bool needsLoad = true;
                try
                {
                    if (SharedPlayer.Source != null)
                    {
                        // Compare sources to allow resuming
                        string currentSrc = SharedPlayer.Source.IsFile ? SharedPlayer.Source.LocalPath : SharedPlayer.Source.AbsoluteUri;
                        string targetSrc = !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath) ? Path.GetFullPath(FilePath) : RawContent;
                        
                        if (string.Equals(currentSrc, targetSrc, StringComparison.OrdinalIgnoreCase))
                        {
                            needsLoad = false;
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable

                if (needsLoad)
                {
                    Uri? sourceUri = null;
                    if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                    {
                        sourceUri = new Uri(Path.GetFullPath(FilePath));
                    }
                    else if (!string.IsNullOrEmpty(RawContent) && RawContent.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Uri.TryCreate(RawContent, UriKind.Absolute, out var parsedUri) &&
                            (parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps))
                        {
                            sourceUri = parsedUri;
                        }
                    }

                    if (sourceUri == null)
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("Audio file is offline or unavailable ⚠️");
                        return;
                    }

                    SharedPlayer.Open(sourceUri);
                }

                SharedPlayer.Play();
                IsAudioPlaying = true;

                // Start the polling timer
                if (_playbackTimer == null)
                {
                    _playbackTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    _playbackTimer.Tick += PlaybackTimer_Tick;
                }
                _playbackTimer.Start();
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("AUDIO_PLAY_ERR", ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast("Failed to start audio playback ⚠️");
            }
        }

        private void PauseAudio()
        {
            try
            {
                if (_playingItem == this)
                {
                    SharedPlayer.Pause();
                    IsAudioPlaying = false;
                    _playbackTimer?.Stop();
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void StopAudioInternal()
        {
            IsAudioPlaying = false;
            AudioPosition = 0;
            AudioPlaybackText = "0:00 / " + FormatTime(AudioDuration);
        }

        public static void StopActivePlayback()
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.InvokeAsync(() => StopActivePlayback());
                    return;
                }

                _playbackTimer?.Stop();
                SharedPlayer.Stop();
                SharedPlayer.Close();
                if (_playingItem != null)
                {
                    _playingItem.StopAudioInternal();
                    _playingItem = null;
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        // --- MEDIA EVENT HANDLERS ---

        private static void SharedPlayer_MediaOpened(object? sender, EventArgs e)
        {
            if (_playingItem != null)
            {
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    double durSeconds = SharedPlayer.NaturalDuration.HasTimeSpan ? SharedPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
                    _playingItem.AudioDuration = durSeconds;
                    _playingItem.UpdatePlaybackText();
                });
            }
        }

        private static void SharedPlayer_MediaEnded(object? sender, EventArgs e)
        {
            if (_playingItem != null)
            {
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    _playbackTimer?.Stop();
                    SharedPlayer.Stop();
                    _playingItem.StopAudioInternal();
                    _playingItem = null;
                });
            }
        }

        private static void SharedPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            if (_playingItem != null)
            {
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    _playbackTimer?.Stop();
                    FlyShelf.Windows.ToastWindow.ShowToast("Failed to play audio preview ⚠️");
                    _playingItem.StopAudioInternal();
                    _playingItem = null;
                    Classes.Logger.LogAction("AUDIO_MEDIA_FAILED", e.ErrorException?.Message ?? "Unknown player error");
                });
            }
        }

        private static void PlaybackTimer_Tick(object? sender, EventArgs e)
        {
            if (_playingItem != null)
            {
                _isUpdatingPositionFromTimer = true;
                try
                {
                    double pos = SharedPlayer.Position.TotalSeconds;
                    _playingItem.AudioPosition = pos;
                    _playingItem.UpdatePlaybackText();
                }
                finally
                {
                    _isUpdatingPositionFromTimer = false;
                }
            }
        }

        private void UpdatePlaybackText()
        {
            AudioPlaybackText = $"{FormatTime(AudioPosition)} / {FormatTime(AudioDuration)}";
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return "0:00";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1)
                return t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
            return t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }
}
