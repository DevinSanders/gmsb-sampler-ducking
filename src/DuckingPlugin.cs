using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace DuckingPlugin;

/// <summary>
/// <see cref="IAudioSamplerPlugin"/> implementing a sidechain-style ducker.
/// Auto-attenuates the carrier signal when a trigger signal exceeds a
/// threshold, then releases back to unity once the trigger falls below it.
/// The classic radio-DJ effect — dialog or SFX automatically push music
/// down without manual fader work.
///
/// <para><b>Sidechain trigger via <see cref="ISidechainSource"/>.</b>
/// Plugin instances pick a trigger source (typically a different bus —
/// "duck Music when SFX plays") from the dropdown in the editor. The
/// detector envelope is updated from the trigger bus's post-FX signal
/// via push callbacks; the carrier (the bus the ducker is attached to)
/// just has gain applied. When no trigger source is selected the plugin
/// falls back to self-detection — useful as a single-bus transient
/// tamer / gain rider.</para>
///
/// <para><b>Attachment.</b> Ducking is meaningful at <see cref="SamplerAttachmentPoints.Master"/>
/// (every-bus output) and <see cref="SamplerAttachmentPoints.Bus"/>
/// (single-bus). Per-shortcut / per-preset attachment isn't offered —
/// those are leaf playbacks where the carrier and trigger would be the
/// same, defeating the point.</para>
/// </summary>
public sealed class DuckingPlugin : IAudioSamplerPlugin
{
    public string Id => "sampler.ducking";
    public string Name => "Ducking";
    public string Description =>
        "Auto-attenuates audio when loud transients (e.g. SFX) hit the chosen sidechain bus. " +
        "Pick a trigger bus in the editor; without one, falls back to self-ducking.";
    public string Version => PluginVersion.OfAssembly(typeof(DuckingPlugin));
    public string Author => "Devin Sanders";

    /// <summary>Master and Bus tiers — see class summary for the
    /// rationale on excluding leaf tiers (Shortcut / Preset / Playlist).</summary>
    public SamplerAttachmentPoints SupportedAttachments =>
        SamplerAttachmentPoints.Master | SamplerAttachmentPoints.Bus;

    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
    }

    public void Shutdown() { }

    public ISamplerInstance CreateInstance() => new DuckingInstance(_context);
}

/// <summary>
/// One configured ducker node. Owns four knob values plus the selected
/// sidechain source id, the per-instance envelope + gain-smoother state,
/// and the active sidechain subscription. The DSP work happens in the
/// nested <see cref="DuckingEffect"/> sample provider.
/// </summary>
internal sealed class DuckingInstance : ISamplerInstance
{
    // ── Defaults ─────────────────────────────────────────────────────
    private const float DefaultThresholdDb = -20f;
    private const float DefaultDuckDepthDb = -12f;
    private const float DefaultAttackMs    =  20f;
    private const float DefaultReleaseMs   = 500f;

    // ── Ranges ────────────────────────────────────────────────────────
    private const float MinThresholdDb = -60f, MaxThresholdDb =   0f;
    private const float MinDuckDepthDb = -30f, MaxDuckDepthDb =   0f;
    private const float MinAttackMs    =   1f, MaxAttackMs    = 200f;
    private const float MinReleaseMs   =  50f, MaxReleaseMs   = 2000f;

    private const float SampleRate = 48000f;
    private const float LevelEpsilon = 1e-12f;

    // ── Parameter state ──────────────────────────────────────────────
    // Volatile bit-pattern reads/writes (see DeserializeConfig for the
    // thread-safety contract).
    private int _thresholdDbBits = BitConverter.SingleToInt32Bits(DefaultThresholdDb);
    private int _duckDepthDbBits = BitConverter.SingleToInt32Bits(DefaultDuckDepthDb);
    private int _attackMsBits    = BitConverter.SingleToInt32Bits(DefaultAttackMs);
    private int _releaseMsBits   = BitConverter.SingleToInt32Bits(DefaultReleaseMs);

    // Sidechain source id. Null = no source = self-duck fallback.
    // Stored in a separate _sourceLock-guarded reference so the UI and
    // audio threads see consistent values when the user picks a new one.
    private string? _sidechainSourceId;
    private readonly object _sourceLock = new();
    private IDisposable? _sidechainSubscription;

    // Channel count of the currently-subscribed source. Captured at
    // subscribe time so OnSidechainBuffer can convert interleaved sample
    // count to frame count without round-tripping through the registry
    // (Subscribe hands us a flat float[] with no channel layout hint).
    // Defaults to 1 so the math is safe if we somehow get a callback
    // without a fresh subscribe — degrades to slightly faster envelope
    // decay, never a divide-by-zero.
    private int _sidechainChannels = 1;

    private readonly IPluginContext? _context;
    private EventHandler? _registryHandler;

    // ── DSP state ────────────────────────────────────────────────────
    // _envBits   — smoothed peak envelope (float as Volatile.Int32
    //              bit-pattern). Updated either from the sidechain
    //              callback (true sidechain) or from the carrier
    //              (self-duck fallback). The host serialises Read calls
    //              today so the single-writer-single-reader invariant
    //              holds, but if buses are ever parallelised the writer
    //              and reader are different audio threads — Volatile
    //              gives the cross-thread visibility the plain float
    //              field lacked on weakly-ordered CPUs (ARM).
    // _gainDbBits — current gain reduction (same idiom). Written and
    //              read only on the carrier's Read thread today.
    private int _envBits = BitConverter.SingleToInt32Bits(0f);
    private int _gainDbBits = BitConverter.SingleToInt32Bits(0f);

    private float ReadEnv() => BitConverter.Int32BitsToSingle(Volatile.Read(ref _envBits));
    private void WriteEnv(float v) => Volatile.Write(ref _envBits, BitConverter.SingleToInt32Bits(v));
    private float ReadGainDb() => BitConverter.Int32BitsToSingle(Volatile.Read(ref _gainDbBits));
    private void WriteGainDb(float v) => Volatile.Write(ref _gainDbBits, BitConverter.SingleToInt32Bits(v));

    public float ThresholdDb
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _thresholdDbBits));
        set => Volatile.Write(ref _thresholdDbBits, BitConverter.SingleToInt32Bits(value));
    }
    public float DuckDepthDb
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _duckDepthDbBits));
        set => Volatile.Write(ref _duckDepthDbBits, BitConverter.SingleToInt32Bits(value));
    }
    public float AttackMs
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _attackMsBits));
        set => Volatile.Write(ref _attackMsBits, BitConverter.SingleToInt32Bits(value));
    }
    public float ReleaseMs
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _releaseMsBits));
        set => Volatile.Write(ref _releaseMsBits, BitConverter.SingleToInt32Bits(value));
    }

    /// <summary>Currently-selected trigger source id, or null for
    /// self-detection. Setting it (re-)subscribes via the host's
    /// sidechain registry.</summary>
    public string? SidechainSourceId
    {
        get { lock (_sourceLock) return _sidechainSourceId; }
        set
        {
            lock (_sourceLock)
            {
                if (_sidechainSourceId == value) return;
                _sidechainSourceId = value;
                ResubscribeLocked();
            }
        }
    }

    public DuckingInstance(IPluginContext? context)
    {
        _context = context;

        // Listen for bus add/remove/rename. Two scenarios this catches:
        //   1. Late bus-service init — per the SDK docs, reading the
        //      registry inside Initialize may return a partial list. A
        //      persisted SidechainSourceId that wasn't resolvable when
        //      CreateEffect first ran will silently fall back to self-
        //      duck unless we retry once buses are loaded.
        //   2. User adds/recreates the trigger bus mid-session — same
        //      retry path resolves the now-existing source id.
        // The handler runs on an arbitrary thread (SDK contract); it
        // takes _sourceLock before mutating subscription state, mirroring
        // the SidechainSourceId setter.
        if (_context?.Sidechain != null)
        {
            _registryHandler = (_, _) =>
            {
                lock (_sourceLock)
                {
                    // Only retry if we have a target id we haven't
                    // successfully resolved yet. Cheap idempotent
                    // resubscribe is also fine — registry-change events
                    // are infrequent.
                    if (_sidechainSourceId != null && _sidechainSubscription == null)
                        ResubscribeLocked();
                }
            };
            _context.Sidechain.SourcesChanged += _registryHandler;
        }
    }

    /// <summary>Caller must hold <see cref="_sourceLock"/>. Detaches the
    /// previous subscription (if any) and attaches a new one for
    /// <see cref="_sidechainSourceId"/>. Logs but doesn't throw if the
    /// source id no longer resolves — the source may have been deleted
    /// in Settings → Buses between the user picking it and the next
    /// edit.</summary>
    private void ResubscribeLocked()
    {
        _sidechainSubscription?.Dispose();
        _sidechainSubscription = null;
        _sidechainChannels = 1;
        if (_sidechainSourceId == null || _context?.Sidechain == null) return;
        var source = _context.Sidechain.GetSourceById(_sidechainSourceId);
        if (source == null) return;
        _sidechainChannels = Math.Max(1, source.Channels);
        _sidechainSubscription = source.Subscribe(OnSidechainBuffer);
    }

    /// <summary>Called on the trigger bus's audio thread once per Read
    /// cycle. Updates <see cref="_envBits"/> from the trigger samples. Does
    /// NOT touch <see cref="_gainDbBits"/> — that's the carrier-thread
    /// smoother and lives in <see cref="DuckingEffect.Read"/>.</summary>
    private void OnSidechainBuffer(float[] buffer, int count)
    {
        if (count <= 0) return;
        // Peak detection across all samples in the buffer. Channel
        // interleaving doesn't matter for peak — we'd peak across
        // channels per-frame anyway.
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float s = buffer[i];
            float abs = s < 0f ? -s : s;
            if (abs > peak) peak = abs;
        }

        // Smooth at the buffer rate. For a 10 ms buffer at 48 kHz the
        // detector still tracks transients (one buffer ≈ one envelope
        // step is fine for ducking — humans don't perceive ducking
        // detection latency below ~20 ms). Detector time constant is
        // fixed at 5 ms; convert to per-buffer alpha based on the
        // buffer's effective duration.
        //
        // `count` is INTERLEAVED samples per the ISidechainSource contract,
        // so divide by the captured channel count to get frame count
        // before multiplying by the per-frame period. For stereo this
        // halves the computed bufferMs vs. treating count as frames.
        // Volatile read of _sidechainChannels is unnecessary — it's only
        // written under _sourceLock from the UI thread before the
        // subscribe call that arms THIS callback, so by the time we run
        // the value is already established.
        int frames = count / _sidechainChannels;
        float bufferMs = 1000f * frames / SampleRate;
        float alpha = MathF.Exp(-bufferMs / 5f);
        // Volatile read-modify-write. Single-writer (this callback)
        // means the RMW window has no concurrent writer; the Volatile
        // pair gives the *carrier* thread visibility into the update
        // promptly.
        float oldEnv = ReadEnv();
        WriteEnv(alpha * oldEnv + (1f - alpha) * peak);
    }

    public ISampleProvider CreateEffect(ISampleProvider source)
    {
        // Materialise the subscription lazily — DeserializeConfig may
        // have set SidechainSourceId before CreateEffect ran. Resubscribe
        // here to be sure.
        lock (_sourceLock) ResubscribeLocked();
        return new DuckingEffect(source, this);
    }

    // ── Persistence ───────────────────────────────────────────────────
    public string SerializeConfig()
    {
        return JsonSerializer.Serialize(new ConfigDto
        {
            ThresholdDb       = ThresholdDb,
            DuckDepthDb       = DuckDepthDb,
            AttackMs          = AttackMs,
            ReleaseMs         = ReleaseMs,
            SidechainSourceId = SidechainSourceId,
        });
    }

    public void DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        ConfigDto? c;
        try { c = JsonSerializer.Deserialize<ConfigDto>(json); }
        catch (JsonException) { return; }
        if (c is null) return;

        ThresholdDb       = Clamp(c.ThresholdDb, MinThresholdDb, MaxThresholdDb);
        DuckDepthDb       = Clamp(c.DuckDepthDb, MinDuckDepthDb, MaxDuckDepthDb);
        AttackMs          = Clamp(c.AttackMs,    MinAttackMs,    MaxAttackMs);
        ReleaseMs         = Clamp(c.ReleaseMs,   MinReleaseMs,   MaxReleaseMs);
        SidechainSourceId = string.IsNullOrEmpty(c.SidechainSourceId) ? null : c.SidechainSourceId;
    }

    // ── Editor UI ────────────────────────────────────────────────────
    public object? CreateControl()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        // Sidechain source picker. "(Self — no sidechain)" is the null
        // entry that maps to single-bus self-ducking.
        var sourceLabel = new TextBlock { Text = "Sidechain trigger:" };
        var sourceCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        BuildSourceList(sourceCombo);

        // SourcesChanged: re-populate the dropdown so renames and bus
        // adds/deletes surface. Marshal to the UI thread; the event
        // fires on whatever thread called Refresh.
        //
        // Detach the handler when the control leaves the visual tree
        // (window close / editor swap). The previous version leaked the
        // handler on every open — the host's SidechainRegistry is a
        // process-lifetime singleton, so each "open editor → close
        // editor" cycle would grow the event invocation list by one,
        // each invocation capturing a stale ComboBox that's been
        // replaced with a fresh one on the next open.
        EventHandler? handler = null;
        var registry = _context?.Sidechain;
        if (registry != null)
        {
            handler = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                BuildSourceList(sourceCombo);
            });
            registry.SourcesChanged += handler;
            sourceCombo.DetachedFromVisualTree += (_, _) =>
            {
                if (handler != null)
                {
                    registry.SourcesChanged -= handler;
                    handler = null;
                }
            };
        }

        sourceCombo.SelectionChanged += (_, _) =>
        {
            if (sourceCombo.SelectedItem is SourceItem item)
                SidechainSourceId = item.Id;
        };

        panel.Children.Add(sourceLabel);
        panel.Children.Add(sourceCombo);

        panel.Children.Add(BuildSlider("Threshold (dB)",  MinThresholdDb, MaxThresholdDb, ThresholdDb, "F1", v => ThresholdDb = v));
        panel.Children.Add(BuildSlider("Duck depth (dB)", MinDuckDepthDb, MaxDuckDepthDb, DuckDepthDb, "F1", v => DuckDepthDb = v));
        panel.Children.Add(BuildSlider("Attack (ms)",     MinAttackMs,    MaxAttackMs,    AttackMs,    "F1", v => AttackMs    = v));
        panel.Children.Add(BuildSlider("Release (ms)",    MinReleaseMs,   MaxReleaseMs,   ReleaseMs,   "F0", v => ReleaseMs   = v));
        return panel;
    }

    private void BuildSourceList(ComboBox combo)
    {
        var items = new List<SourceItem>
        {
            new SourceItem(null, "(Self — no sidechain)")
        };
        if (_context?.Sidechain != null)
        {
            foreach (var s in _context.Sidechain.GetSources())
                items.Add(new SourceItem(s.Id, s.DisplayName));
        }
        combo.ItemsSource = items;
        // Select the current SidechainSourceId, or the first ("self") entry.
        var current = SidechainSourceId;
        int idx = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Id == current) { idx = i; break; }
        }
        combo.SelectedIndex = idx;
    }

    private sealed record SourceItem(string? Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private static Control BuildSlider(string label, double min, double max, double initial, string fmt, Action<float> setter)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        var caption = new TextBlock
        {
            Text = $"{label}: {initial.ToString(fmt, CultureInfo.InvariantCulture)}"
        };
        var slider = new Slider { Minimum = min, Maximum = max, Value = initial };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                var v = (float)slider.Value;
                setter(v);
                caption.Text = $"{label}: {v.ToString(fmt, CultureInfo.InvariantCulture)}";
            }
        };
        stack.Children.Add(caption);
        stack.Children.Add(slider);
        return stack;
    }

    public void Dispose()
    {
        // Detach the registry handler FIRST so a late SourcesChanged
        // event can't race the subscription teardown. The registry is a
        // process-lifetime singleton; leaving the handler attached would
        // keep this DuckingInstance pinned past Dispose.
        if (_registryHandler != null && _context?.Sidechain != null)
        {
            _context.Sidechain.SourcesChanged -= _registryHandler;
            _registryHandler = null;
        }
        lock (_sourceLock)
        {
            _sidechainSubscription?.Dispose();
            _sidechainSubscription = null;
        }
    }

    private static float Clamp(float v, float min, float max)
        => v < min ? min : (v > max ? max : v);

    private sealed class ConfigDto
    {
        public float ThresholdDb { get; set; } = DefaultThresholdDb;
        public float DuckDepthDb { get; set; } = DefaultDuckDepthDb;
        public float AttackMs    { get; set; } = DefaultAttackMs;
        public float ReleaseMs   { get; set; } = DefaultReleaseMs;
        /// <summary>Stable id of the chosen sidechain trigger source, or
        /// null = self-duck. <see cref="ISidechainSource.Id"/> values use
        /// a <c>"bus:&lt;id&gt;"</c> shape today; the plugin treats the
        /// string as opaque.</summary>
        public string? SidechainSourceId { get; set; }
    }

    /// <summary>
    /// Per-sample carrier processor. Reads source samples, applies the
    /// current gain reduction (driven by <see cref="DuckingInstance._gainDbBits"/>),
    /// and smooths gainDb toward the threshold-determined target.
    /// <para>When the instance is in self-duck mode (no sidechain
    /// subscription), this also runs the detector inline on the carrier
    /// samples.</para>
    /// </summary>
    private sealed class DuckingEffect : ISampleProvider
    {
        // Self-duck detector time constant (only used when there's no
        // sidechain subscription). Sidechain mode uses the buffer-rate
        // smoother in OnSidechainBuffer instead.
        private const float DetectorTimeConstantMs = 5f;

        private readonly ISampleProvider _source;
        private readonly DuckingInstance _owner;

        public DuckingEffect(ISampleProvider source, DuckingInstance owner)
        {
            _source = source;
            _owner = owner;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            if (n <= 0) return n;

            float thresholdDb = _owner.ThresholdDb;
            float duckDepthDb = _owner.DuckDepthDb;
            float attackMs    = MathF.Max(0.01f, _owner.AttackMs);
            float releaseMs   = MathF.Max(0.01f, _owner.ReleaseMs);
            bool hasSidechain = _owner._sidechainSubscription != null;

            float detectorAlpha = MathF.Exp(-1f / (DetectorTimeConstantMs * 0.001f * SampleRate));
            float attackAlpha   = MathF.Exp(-1f / (attackMs               * 0.001f * SampleRate));
            float releaseAlpha  = MathF.Exp(-1f / (releaseMs              * 0.001f * SampleRate));

            int channels = WaveFormat.Channels;
            float env    = _owner.ReadEnv();
            float gainDb = _owner.ReadGainDb();

            for (int i = 0; i < n; i += channels)
            {
                // Detector: self-duck mode runs inline; sidechain mode
                // skips this branch — env was already updated by the
                // trigger bus's OnSidechainBuffer callback above the
                // mixer combine.
                if (!hasSidechain)
                {
                    float level = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        float s = buffer[offset + i + c];
                        float abs = s < 0f ? -s : s;
                        if (abs > level) level = abs;
                    }
                    env = detectorAlpha * env + (1f - detectorAlpha) * level;
                }

                float envDb = 20f * MathF.Log10(env + LevelEpsilon);
                float targetGainDb = (envDb >= thresholdDb) ? duckDepthDb : 0f;

                float alpha = (targetGainDb < gainDb) ? attackAlpha : releaseAlpha;
                gainDb = alpha * gainDb + (1f - alpha) * targetGainDb;

                float gainLin = MathF.Pow(10f, gainDb / 20f);
                for (int c = 0; c < channels; c++)
                {
                    buffer[offset + i + c] *= gainLin;
                }
            }

            // env is only owned by THIS thread in self-duck mode. In
            // sidechain mode we don't write back — the sidechain
            // callback is the writer.
            if (!hasSidechain) _owner.WriteEnv(env);
            _owner.WriteGainDb(gainDb);
            return n;
        }
    }
}
