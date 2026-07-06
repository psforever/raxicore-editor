using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using RaxicoreEditor.Editor.Audio;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>A named, playable region within a WAV (from its RIFF cue points + region labels).</summary>
    public sealed class AudioClip
    {
        public required string Name { get; init; }
        public double StartMs { get; init; }
        public double EndMs { get; init; }
        public string Display => $"{Name}   ·   {(EndMs - StartMs) / 1000.0:0.0}s";
    }

    /// <summary>
    /// A WAV document with an in-tab transport (play / pause / stop / seek). Metadata is parsed directly
    /// from the RIFF header; playback is driven by <see cref="SoundFlowAudioPlayer"/>, which works across
    /// Windows, macOS and Linux. If audio hardware is unavailable (e.g. a headless machine) or the file
    /// can't be decoded, the tab shows the metadata and a note instead of the transport. Export returns the
    /// original bytes.
    /// </summary>
    public sealed class AudioDocument : DocumentBase, IDisposable
    {
        private readonly byte[] _data;
        private readonly SoundFlowAudioPlayer? _player;
        private readonly DispatcherTimer? _timer;
        private bool _suppressSeek;
        private uint _sampleRate;
        private double _clipStartMs; // where the current playback started (0 for whole-file)

        public AudioDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Audio)
        {
            _data = data;
            ParseWavHeader();
            ParseCues();

            try
            {
                var player = new SoundFlowAudioPlayer(data);
                if (player.Open(out string? error))
                {
                    _player = player;
                    CanPlay = true;
                    int lenMs = _player.LengthMs;
                    if (lenMs > 0)
                    {
                        DurationSeconds = lenMs / 1000.0;
                    }
                    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    _timer.Tick += OnTick;
                }
                else
                {
                    PlaybackNote = "Can't play this file: " + (error ?? "unsupported format");
                    player.Dispose();
                }
            }
            catch (Exception ex)
            {
                PlaybackNote = "Audio playback unavailable: " + ex.Message;
            }

            StopCommand = new RelayCommand(StopPlayback, () => CanPlay);
        }

        /// <summary>RIFF/WAVE magic check — used to route archive entries by content, not just extension.</summary>
        public static bool IsWav(byte[] b) =>
            b.Length >= 12 &&
            b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' &&
            b[8] == (byte)'W' && b[9] == (byte)'A' && b[10] == (byte)'V' && b[11] == (byte)'E';

        // ---- metadata --------------------------------------------------------------------------

        /// <summary>Human-readable format summary, e.g. "PCM · 44.1 kHz · 16-bit · stereo".</summary>
        public string AudioInfo { get; private set; } = "Unrecognized WAV header";

        /// <summary>True when the transport is usable; false shows <see cref="PlaybackNote"/> instead.</summary>
        public bool CanPlay { get; private set; }

        /// <summary>Why playback is unavailable (platform / unsupported format), when <see cref="CanPlay"/> is false.</summary>
        public string PlaybackNote { get; private set; } = "";

        public double DurationSeconds { get; private set; }
        public string DurationText => Format(DurationSeconds);

        /// <summary>Named regions embedded in the file (RIFF cue points). Empty for a plain single sound.</summary>
        public ObservableCollection<AudioClip> Clips { get; } = new();

        /// <summary>True when the file carries selectable snippets (drives the snippet list's visibility).</summary>
        public bool HasClips => Clips.Count > 0;

        // ---- transport -------------------------------------------------------------------------

        public RelayCommand? StopCommand { get; }

        /// <summary>Play just one embedded snippet (from its start to its end).</summary>
        public void PlayClip(AudioClip? clip)
        {
            if (_player == null || clip == null)
            {
                return;
            }
            _clipStartMs = clip.StartMs;
            _player.PlayRange((int)Math.Round(clip.StartMs), (int)Math.Round(clip.EndMs));
            SetProperty(ref _isPlaying, true, nameof(IsPlaying));
            SetPositionInternal(clip.StartMs / 1000.0);
            _timer?.Start();
        }

        private bool _isPlaying;
        /// <summary>Bound to the play/pause toggle. Setting it drives the player.</summary>
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    if (value) StartOrResume();
                    else Pause();
                }
            }
        }

        private double _positionSeconds;
        /// <summary>Playback position (seconds). Two-way bound to the seek slider.</summary>
        public double PositionSeconds
        {
            get => _positionSeconds;
            set
            {
                if (SetProperty(ref _positionSeconds, value))
                {
                    RaisePropertyChanged(nameof(PositionText));
                    if (!_suppressSeek && _player != null)
                    {
                        int ms = (int)Math.Round(value * 1000);
                        _player.Seek(ms);
                        if (_isPlaying) _player.PlayFrom(ms);
                    }
                }
            }
        }

        public string PositionText => Format(_positionSeconds);

        private void StartOrResume()
        {
            if (_player == null)
            {
                _isPlaying = false;
                return;
            }
            _clipStartMs = 0; // the main transport plays the whole file
            int fromMs = (int)Math.Round(_positionSeconds * 1000);
            int lenMs = (int)Math.Round(DurationSeconds * 1000);
            if (lenMs > 0 && fromMs >= lenMs - 20)
            {
                fromMs = 0; // at the end → play again from the start
            }
            _player.PlayFrom(fromMs);
            _timer?.Start();
        }

        private void Pause()
        {
            _player?.Pause();
            _timer?.Stop();
        }

        private void StopPlayback()
        {
            _player?.Stop();
            _timer?.Stop();
            _clipStartMs = 0;
            SetProperty(ref _isPlaying, false, nameof(IsPlaying));
            SetPositionInternal(0);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_player == null)
            {
                return;
            }
            // "stopped" while the timer runs means playback reached the end (of the file or the snippet).
            if (_player.Mode == "stopped")
            {
                _timer?.Stop();
                SetProperty(ref _isPlaying, false, nameof(IsPlaying));
                SetPositionInternal(_clipStartMs / 1000.0);
                return;
            }
            SetPositionInternal(_player.PositionMs / 1000.0);
        }

        // Update the position without triggering a seek back into the player.
        private void SetPositionInternal(double seconds)
        {
            _suppressSeek = true;
            PositionSeconds = seconds;
            _suppressSeek = false;
        }

        private static string Format(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private void ParseWavHeader()
        {
            try
            {
                if (!IsWav(_data))
                {
                    return;
                }
                ushort format = 0, channels = 0, bits = 0;
                uint sampleRate = 0, byteRate = 0, dataSize = 0;
                int pos = 12; // past "RIFF"<size>"WAVE"
                while (pos + 8 <= _data.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(_data, pos, 4);
                    uint size = BitConverter.ToUInt32(_data, pos + 4);
                    int body = pos + 8;
                    if (id == "fmt " && body + 16 <= _data.Length)
                    {
                        format = BitConverter.ToUInt16(_data, body);
                        channels = BitConverter.ToUInt16(_data, body + 2);
                        sampleRate = BitConverter.ToUInt32(_data, body + 4);
                        byteRate = BitConverter.ToUInt32(_data, body + 8);
                        bits = BitConverter.ToUInt16(_data, body + 14);
                    }
                    else if (id == "data")
                    {
                        dataSize = Math.Min(size, (uint)Math.Max(0, _data.Length - body));
                    }
                    long next = (long)body + size + (size & 1); // chunks are word-aligned
                    if (next <= pos)
                    {
                        break;
                    }
                    pos = (int)next;
                }

                _sampleRate = sampleRate;
                if (byteRate > 0)
                {
                    DurationSeconds = dataSize / (double)byteRate;
                }

                string formatName = format switch
                {
                    1 => "PCM",
                    2 => "ADPCM",
                    3 => "IEEE float",
                    17 => "IMA ADPCM",
                    0xFFFE => "extensible",
                    _ => $"format {format}",
                };
                string channelName = channels switch { 1 => "mono", 2 => "stereo", _ => $"{channels} ch" };
                AudioInfo = $"{formatName} · {sampleRate / 1000.0:0.#} kHz · {bits}-bit · {channelName}";
            }
            catch
            {
                AudioInfo = "Unrecognized WAV header";
            }
        }

        // Parse RIFF cue points + the associated-data list (labels / labelled-text region lengths) into
        // named, playable snippets. Standard chunks: "cue " (marker sample offsets), and a "LIST/adtl"
        // holding "labl" (a name per cue id) and "ltxt" (a region length in samples per cue id).
        private void ParseCues()
        {
            if (_sampleRate == 0 || !IsWav(_data))
            {
                return;
            }
            try
            {
                var cues = new List<(uint id, long sample)>();
                var labels = new Dictionary<uint, string>();
                var lengths = new Dictionary<uint, long>();

                int pos = 12;
                while (pos + 8 <= _data.Length)
                {
                    string id = Encoding.ASCII.GetString(_data, pos, 4);
                    uint size = BitConverter.ToUInt32(_data, pos + 4);
                    int body = pos + 8;
                    long avail = _data.Length - body;
                    if (size > avail) size = (uint)Math.Max(0, avail);

                    if (id == "cue " && size >= 4)
                    {
                        uint count = BitConverter.ToUInt32(_data, body);
                        int p = body + 4;
                        for (uint i = 0; i < count && p + 24 <= body + size; i++, p += 24)
                        {
                            uint cueId = BitConverter.ToUInt32(_data, p);
                            uint sampleOffset = BitConverter.ToUInt32(_data, p + 20);
                            cues.Add((cueId, sampleOffset));
                        }
                    }
                    else if (id == "LIST" && size >= 4 && Encoding.ASCII.GetString(_data, body, 4) == "adtl")
                    {
                        int p = body + 4;
                        int listEnd = body + (int)size;
                        while (p + 8 <= listEnd)
                        {
                            string sid = Encoding.ASCII.GetString(_data, p, 4);
                            uint ssize = BitConverter.ToUInt32(_data, p + 4);
                            int sbody = p + 8;
                            if (ssize > listEnd - sbody) ssize = (uint)Math.Max(0, listEnd - sbody);

                            if (sid == "labl" && ssize >= 4)
                            {
                                uint cueId = BitConverter.ToUInt32(_data, sbody);
                                string text = ReadZeroTerminated(sbody + 4, (int)ssize - 4);
                                if (text.Length > 0) labels[cueId] = text;
                            }
                            else if (sid == "ltxt" && ssize >= 8)
                            {
                                uint cueId = BitConverter.ToUInt32(_data, sbody);
                                lengths[cueId] = BitConverter.ToUInt32(_data, sbody + 4);
                            }

                            long snext = (long)sbody + ssize + (ssize & 1);
                            if (snext <= p) break;
                            p = (int)snext;
                        }
                    }

                    long next = (long)body + size + (size & 1);
                    if (next <= pos) break;
                    pos = (int)next;
                }

                if (cues.Count == 0)
                {
                    return;
                }
                cues.Sort((a, b) => a.sample.CompareTo(b.sample));
                double fileEndMs = DurationSeconds > 0 ? DurationSeconds * 1000.0 : double.MaxValue;

                for (int i = 0; i < cues.Count; i++)
                {
                    double startMs = cues[i].sample * 1000.0 / _sampleRate;
                    double endMs;
                    if (lengths.TryGetValue(cues[i].id, out long lenSamples) && lenSamples > 0)
                    {
                        endMs = startMs + lenSamples * 1000.0 / _sampleRate;
                    }
                    else if (i + 1 < cues.Count)
                    {
                        endMs = cues[i + 1].sample * 1000.0 / _sampleRate;
                    }
                    else
                    {
                        endMs = fileEndMs;
                    }
                    if (fileEndMs != double.MaxValue) endMs = Math.Min(endMs, fileEndMs);

                    string name = labels.TryGetValue(cues[i].id, out string? nm) ? nm : $"Snippet {i + 1}";
                    Clips.Add(new AudioClip { Name = name, StartMs = startMs, EndMs = endMs });
                }
            }
            catch
            {
                Clips.Clear(); // malformed marker data → just treat it as a plain single sound
            }
        }

        private string ReadZeroTerminated(int offset, int maxLen)
        {
            int end = offset;
            int limit = Math.Min(_data.Length, offset + Math.Max(0, maxLen));
            while (end < limit && _data[end] != 0) end++;
            return Encoding.Latin1.GetString(_data, offset, end - offset).Trim();
        }

        public override byte[] Export() => _data;

        public void Dispose()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
            }
            _player?.Dispose();
        }
    }
}
