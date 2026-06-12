using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using NAudio.Wave;
using SoundBoard.PluginApi;
using Xunit;

namespace DuckingPlugin.Tests;

// ── Test doubles ─────────────────────────────────────────────────────────

/// <summary>Deterministic infinite source. <paramref name="fn"/> maps the
/// running interleaved-sample index to a sample value.</summary>
sealed class SignalProvider(WaveFormat fmt, Func<long, float> fn) : ISampleProvider
{
    long _n;
    public WaveFormat WaveFormat => fmt;
    public int Read(float[] buf, int off, int count)
    {
        for (int i = 0; i < count; i++) buf[off + i] = fn(_n++);
        return count;
    }
}

/// <summary>Fake sidechain source. Captures the most-recently registered
/// push callback so a test can drive trigger buffers by hand.</summary>
sealed class FakeSidechainSource(string id, string name, int channels) : ISidechainSource
{
    public string Id => id;
    public string DisplayName => name;
    public int SampleRate => 48000;
    public int Channels => channels;

    public Action<float[], int>? Latest { get; private set; }

    public IDisposable Subscribe(Action<float[], int> onSamples)
    {
        Latest = onSamples;
        return new Unsub(this, onSamples);
    }

    /// <summary>Push one trigger buffer of constant amplitude to the live
    /// callback (count = frames × channels, interleaved).</summary>
    public void Pump(float amplitude, int frames)
    {
        var cb = Latest;
        if (cb == null) return;
        int count = frames * channels;
        var buf = new float[count];
        for (int i = 0; i < count; i++) buf[i] = amplitude;
        cb(buf, count);
    }

    sealed class Unsub(FakeSidechainSource owner, Action<float[], int> cb) : IDisposable
    {
        public void Dispose() { if (owner.Latest == cb) owner.Latest = null; }
    }
}

sealed class FakeRegistry : ISidechainRegistry
{
    readonly List<ISidechainSource> _sources = new();
    public event EventHandler? SourcesChanged;

    public void Add(ISidechainSource s) { _sources.Add(s); SourcesChanged?.Invoke(this, EventArgs.Empty); }
    public IReadOnlyList<ISidechainSource> GetSources() => _sources;
    public ISidechainSource? GetSourceById(string id) => _sources.Find(s => s.Id == id);
}

sealed class FakeContext(ISidechainRegistry? sidechain) : IPluginContext
{
    public IWindowService? WindowService => null;
    public string PluginDataPath => ".";
    public IAudioCodecRegistry? CodecRegistry => null;
    public ISidechainRegistry? Sidechain => sidechain;
}

// ── Tests ────────────────────────────────────────────────────────────────

public class DuckingTests
{
    static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    const int BufFrames = 480;                 // ~10 ms at 48 kHz
    const int BufSamples = BufFrames * 2;      // interleaved stereo

    static Func<long, float> Sine(float amp, float freq = 1000f)
        => n => amp * MathF.Sin(2f * MathF.PI * freq * n / 48000f);

    static double Rms(float[] buf, int n)
    {
        double sum = 0;
        for (int i = 0; i < n; i++) sum += (double)buf[i] * buf[i];
        return Math.Sqrt(sum / n);
    }

    /// <summary>Run <paramref name="buffers"/> Read cycles, returning the RMS
    /// of the final buffer (after the gain smoother has settled).</summary>
    static double RunToSettle(ISampleProvider fx, int buffers, Action? beforeEach = null)
    {
        var buf = new float[BufSamples];
        for (int b = 0; b < buffers; b++)
        {
            beforeEach?.Invoke();
            Array.Clear(buf);
            fx.Read(buf, 0, BufSamples);
        }
        return Rms(buf, BufSamples);
    }

    static string Config(double threshold, double depth, double attack, double release, string? source = null)
        => JsonSerializer.Serialize(new
        {
            ThresholdDb = threshold,
            DuckDepthDb = depth,
            AttackMs = attack,
            ReleaseMs = release,
            SidechainSourceId = source,
        });

    [Fact]
    public void Factory_instances_are_independent()
    {
        var plugin = new DuckingPlugin();
        var a = plugin.CreateInstance();
        var b = plugin.CreateInstance();

        a.Should().NotBeSameAs(b);

        // Mutating one's config must not bleed into the other.
        a.DeserializeConfig(Config(-40, -30, 5, 80));
        a.SerializeConfig().Should().NotBe(b.SerializeConfig());
    }

    [Fact]
    public void Supported_attachments_are_master_and_bus_only()
    {
        var plugin = new DuckingPlugin();
        plugin.SupportedAttachments.Should()
            .Be(SamplerAttachmentPoints.Master | SamplerAttachmentPoints.Bus);
    }

    [Fact]
    public void Config_round_trips_every_knob()
    {
        var plugin = new DuckingPlugin();
        var inst = plugin.CreateInstance();
        inst.DeserializeConfig(Config(-33, -9, 15, 250, "bus:sfx"));

        var restored = plugin.CreateInstance();
        restored.DeserializeConfig(inst.SerializeConfig());

        restored.SerializeConfig().Should().Be(inst.SerializeConfig());
    }

    [Fact]
    public void Config_clamps_out_of_range_values()
    {
        var plugin = new DuckingPlugin();
        var inst = plugin.CreateInstance();
        // Everything past its documented extreme.
        inst.DeserializeConfig(Config(threshold: 999, depth: -999, attack: 0, release: 99999));

        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        var r = doc.RootElement;
        r.GetProperty("ThresholdDb").GetSingle().Should().Be(0f);     // max
        r.GetProperty("DuckDepthDb").GetSingle().Should().Be(-30f);   // min
        r.GetProperty("AttackMs").GetSingle().Should().Be(1f);        // min
        r.GetProperty("ReleaseMs").GetSingle().Should().Be(2000f);    // max
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ \"ThresholdDb\": ")]
    [InlineData("null")]
    public void Malformed_config_never_throws(string json)
    {
        var inst = new DuckingPlugin().CreateInstance();
        var act = () => inst.DeserializeConfig(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void Effect_preserves_wave_format()
    {
        var inst = new DuckingPlugin().CreateInstance();
        var src = new SignalProvider(Fmt, Sine(0.5f));
        inst.CreateEffect(src).WaveFormat.Should().Be(Fmt);
    }

    [Fact]
    public void SelfDuck_attenuates_loud_signal()
    {
        var inst = new DuckingPlugin().CreateInstance();
        inst.DeserializeConfig(Config(-20, -12, 20, 500));
        var fx = inst.CreateEffect(new SignalProvider(Fmt, Sine(1.0f)));

        double outRms = RunToSettle(fx, buffers: 200);
        double inRms = 1.0 / Math.Sqrt(2);   // RMS of a unit sine

        // -12 dB ≈ 0.25× linear. Allow headroom for detector ripple.
        outRms.Should().BeLessThan(inRms * 0.5);
    }

    [Fact]
    public void SelfDuck_passes_quiet_signal_unchanged()
    {
        var inst = new DuckingPlugin().CreateInstance();
        inst.DeserializeConfig(Config(-20, -12, 20, 500));
        var fx = inst.CreateEffect(new SignalProvider(Fmt, Sine(0.001f)));  // ~-63 dBFS

        double outRms = RunToSettle(fx, buffers: 50);
        double inRms = 0.001 / Math.Sqrt(2);

        outRms.Should().BeApproximately(inRms, inRms * 0.02);
    }

    [Fact]
    public void Sidechain_trigger_ducks_carrier()
    {
        var src = new FakeSidechainSource("bus:sfx", "SFX", channels: 2);
        var reg = new FakeRegistry();
        reg.Add(src);
        var plugin = new DuckingPlugin();
        plugin.Initialize(new FakeContext(reg));

        var inst = plugin.CreateInstance();
        inst.DeserializeConfig(Config(-20, -12, 20, 500, source: "bus:sfx"));
        var fx = inst.CreateEffect(new SignalProvider(Fmt, Sine(0.5f)));

        // Loud trigger on every cycle keeps the detector envelope pinned high.
        double outRms = RunToSettle(fx, buffers: 200,
            beforeEach: () => src.Pump(amplitude: 1.0f, frames: BufFrames));
        double inRms = 0.5 / Math.Sqrt(2);

        outRms.Should().BeLessThan(inRms * 0.5);
    }

    [Fact]
    public void Sidechain_silence_leaves_carrier_untouched()
    {
        var src = new FakeSidechainSource("bus:sfx", "SFX", channels: 2);
        var reg = new FakeRegistry();
        reg.Add(src);
        var plugin = new DuckingPlugin();
        plugin.Initialize(new FakeContext(reg));

        var inst = plugin.CreateInstance();
        inst.DeserializeConfig(Config(-20, -12, 20, 500, source: "bus:sfx"));
        var fx = inst.CreateEffect(new SignalProvider(Fmt, Sine(0.5f)));

        double outRms = RunToSettle(fx, buffers: 50,
            beforeEach: () => src.Pump(amplitude: 0f, frames: BufFrames));
        double inRms = 0.5 / Math.Sqrt(2);

        outRms.Should().BeApproximately(inRms, inRms * 0.02);
    }

    [Fact]
    public void Unknown_source_id_falls_back_to_self_detect()
    {
        var reg = new FakeRegistry();   // empty — id won't resolve
        var plugin = new DuckingPlugin();
        plugin.Initialize(new FakeContext(reg));

        var inst = plugin.CreateInstance();
        inst.DeserializeConfig(Config(-20, -12, 20, 500, source: "bus:ghost"));
        var fx = inst.CreateEffect(new SignalProvider(Fmt, Sine(1.0f)));

        // No subscription was made, so the carrier's own loud signal must
        // drive the inline detector and duck it.
        double outRms = RunToSettle(fx, buffers: 200);
        double inRms = 1.0 / Math.Sqrt(2);

        outRms.Should().BeLessThan(inRms * 0.5);
    }

    [Fact]
    public void Live_config_push_is_thread_safe()
    {
        var inst = new DuckingPlugin().CreateInstance();
        var fx = inst.CreateEffect(new SignalProvider(Fmt, Sine(0.7f)));

        var stop = false;
        // A plain Thread (rather than Task) keeps this off xunit's
        // blocking-task-op / CancellationToken analyzers and is exactly
        // the contended-writer scenario we want.
        var writer = new Thread(() =>
        {
            var rng = new Random(1234);
            while (!Volatile.Read(ref stop))
            {
                inst.DeserializeConfig(Config(
                    threshold: rng.Next(-60, 1),
                    depth: rng.Next(-30, 1),
                    attack: rng.Next(1, 200),
                    release: rng.Next(50, 2000)));
                inst.DeserializeConfig("garbage");   // exercise the failure path too
            }
        });
        writer.Start();

        var buf = new float[BufSamples];
        var act = () =>
        {
            for (int b = 0; b < 5000; b++) fx.Read(buf, 0, BufSamples);
        };
        act.Should().NotThrow();

        Volatile.Write(ref stop, true);
        writer.Join();
    }
}
