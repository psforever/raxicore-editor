using System;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace RaxicoreEditor.Editor.Audio
{
    /// <summary>
    /// A small cross-platform WAV player built on <c>SoundFlow</c> (MIT) over its <c>miniaudio</c> backend
    /// (public domain / MIT-0). Works on Windows, macOS and Linux with no per-platform build step — the
    /// NuGet ships the native binaries. Replaces the old Windows-only winmm/MCI player; the surface is kept
    /// deliberately identical (millisecond ints, a stringy <see cref="Mode"/>) so callers are unchanged.
    /// <para>
    /// One process-wide <see cref="MiniAudioEngine"/> is shared; each player owns a playback device opened
    /// at the file's own sample rate. Matching the device rate to the source keeps <c>Time</c>/<c>Duration</c>/
    /// <c>Seek</c> in real source seconds (SoundFlow otherwise expresses them in device-rate seconds), so the
    /// transport lines up with the RIFF-derived duration and cue-point clip times.
    /// </para>
    /// </summary>
    internal sealed class SoundFlowAudioPlayer : IDisposable
    {
        private static MiniAudioEngine? _engine;
        private static readonly object EngineLock = new();

        private readonly byte[] _wav;
        private AudioPlaybackDevice? _device;
        private AssetDataProvider? _provider;
        private SoundPlayer? _player;
        private float? _rangeEndSeconds; // when playing a single snippet, auto-stop at this point
        private bool _disposed;

        public SoundFlowAudioPlayer(byte[] wav) => _wav = wav;

        /// <summary>Decode the file and open an output device. Returns false (with a reason) if audio is
        /// unavailable on this machine (no device / headless) or the file can't be decoded.</summary>
        public bool Open(out string? error)
        {
            error = null;
            try
            {
                MiniAudioEngine engine = GetEngine();

                // Decode up front; the provider reports the source sample rate we open the device at.
                var provider = new AssetDataProvider(engine, _wav);
                int rate = provider.SampleRate > 0 ? provider.SampleRate : 44100;
                var format = new AudioFormat { Format = SampleFormat.F32, Channels = 2, SampleRate = rate };

                AudioPlaybackDevice device = engine.InitializePlaybackDevice(null, format);
                device.Start();

                var player = new SoundPlayer(engine, format, provider);
                device.MasterMixer.AddComponent(player);

                _provider = provider;
                _device = device;
                _player = player;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Cleanup();
                return false;
            }
        }

        public int LengthMs => _player != null ? (int)Math.Round(_player.Duration * 1000f) : 0;

        public int PositionMs
        {
            get
            {
                PollRangeEnd();
                return _player != null ? (int)Math.Round(_player.Time * 1000f) : 0;
            }
        }

        /// <summary>"playing", "paused" or "stopped" — mirrors the old MCI status strings the caller polls.</summary>
        public string Mode
        {
            get
            {
                if (_player == null) return "";
                PollRangeEnd();
                return _player.State switch
                {
                    PlaybackState.Playing => "playing",
                    PlaybackState.Paused => "paused",
                    _ => "stopped",
                };
            }
        }

        public void PlayFrom(int fromMs)
        {
            if (_player == null) return;
            _rangeEndSeconds = null;
            _player.Seek(fromMs / 1000f);
            _player.Play();
        }

        public void PlayRange(int fromMs, int toMs)
        {
            if (_player == null) return;
            _rangeEndSeconds = toMs / 1000f;
            _player.Seek(fromMs / 1000f);
            _player.Play();
        }

        public void Pause() => _player?.Pause();

        public void Stop()
        {
            if (_player == null) return;
            _rangeEndSeconds = null;
            _player.Stop();
            _player.Seek(0f);
        }

        public void Seek(int ms) => _player?.Seek(ms / 1000f);

        // A snippet plays from its start to its end; SoundFlow has no "play to" so we stop it ourselves once
        // the polled position reaches the range end (the transport polls Mode/PositionMs a few times a second).
        private void PollRangeEnd()
        {
            if (_player == null || _rangeEndSeconds is not { } end) return;
            if (_player.State == PlaybackState.Playing && _player.Time >= end)
            {
                _player.Stop();
                _rangeEndSeconds = null;
            }
        }

        private static MiniAudioEngine GetEngine()
        {
            lock (EngineLock)
            {
                return _engine ??= new MiniAudioEngine();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                if (_player != null)
                {
                    _device?.MasterMixer.RemoveComponent(_player);
                    _player.Dispose();
                }
            }
            catch { /* best-effort teardown */ }
            try { _device?.Stop(); _device?.Dispose(); } catch { /* best-effort */ }
            _player = null;
            _device = null;
            _provider = null;
        }
    }
}
