// Audio — a voice-based synthesizer ported from Audio.swift (the macOS app's
// AVAudioSourceNode graph). Oscillator/noise voices with RBJ biquads, envelopes,
// vibrato and scheduled sub-sounds; positional stereo, underwater lowpass,
// cave feedback-delay reverb, generative music and jukebox discs.
//
// The Swift "AVAudioSourceNode render callback at 48 kHz" becomes Render(), pulled
// by an NAudio ISampleProvider feeding WasapiOut. Render() is the audio thread; the
// main thread communicates by appending to a locked inbox (never touching `voices`,
// which the render thread owns after pickup — matching the Swift design).

using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Pebble.Audio;

internal enum OscType { Sine, Square, Sawtooth, Triangle }

/// One synth voice. A value type (struct) like the Swift `Voice`; the render
/// thread copies the list, mutates runtime state per block, and writes it back.
internal struct Voice
{
    public bool isNoise;
    public OscType type;
    public double freq;
    public double endFreq;       // 0 = constant
    public double vibrato;       // Hz, depth = freq*0.04
    public double dur;
    public double attack;
    public double vol;
    public double pitchRate;     // noise playback rate
    public double filterFreq;    // noise: biquad center
    public double filterQ;
    public bool lowpassFilter;   // else bandpass
    public double start;         // engine-time seconds
    public double pan;           // -1..1
    public double gain;          // category × distance gain at spawn
    public double reverbSend;
    // runtime state
    public double phase;
    public double lfoPhase;
    public double noisePos;
    public double z1, z2;        // biquad state (transposed direct form II)
    public bool done;
    public bool isDisc;          // jukebox voices — stopDisc cuts ONLY these

    public static Voice Make() => new Voice
    {
        type = OscType.Sine,
        freq = 440.0,
        dur = 0.2,
        attack = 0.01,
        vol = 0.3,
        pitchRate = 1.0,
        filterQ = 1.0,
        gain = 1.0,
    };
}

public sealed class AudioEngine : IDisposable
{
    private readonly double sampleRate;
    private readonly object gate = new();

    /// owned by the render thread after pickup — main thread only appends to
    /// the inbox (a snapshot/write-back scheme silently dropped any voice
    /// added while the render callback was running).
    private List<Voice> voices = new();
    private List<Voice> voiceInbox = new();
    private double? discCutAt;
    private double engineTime;            // seconds, advanced by the render thread

    // shared noise table
    private readonly float[] noise = new float[1 << 16];

    // env / mix state (written main thread, read audio thread — doubles, benign races)
    private double masterGain = 0.8;
    private readonly Dictionary<string, double> catGains = new();
    private double lowpassTarget = 20000.0;
    private double lowpassCur = 20000.0;
    private double lpZ1, lpZ2;
    private double reverbAmt;
    private readonly float[] delayL = new float[24000];
    private readonly float[] delayR = new float[26000];
    private int delayPos;
    private int delayPosR;               // R line is longer — its own wrap

    public double listenerX, listenerY, listenerZ, listenerYaw;
    public bool underwater;
    public double caveFactor;
    public Dictionary<string, double> volumes = new();
    public Action<string>? onSubtitle;
    private int musicTimer = 600;
    private double musicPlayingUntil;
    private double discUntil;
    private bool inited;

    private IWavePlayer? player;
    private readonly Random spawnRng = new();

    public AudioEngine(double sampleRate = 48000.0)
    {
        this.sampleRate = sampleRate;
    }

    /// Build the noise table and (unless offline) open the WASAPI output device.
    /// `openDevice: false` is used by --audiotest to render to an offline buffer.
    public void InitEngine(bool openDevice = true)
    {
        if (inited) return;
        inited = true;
        var r = new Random(unchecked((int)0x9E3779B9) ^ 12345);
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(r.NextDouble() * 2.0 - 1.0);
        ApplyVolumes(volumes);

        if (openDevice)
        {
            var provider = new SynthSampleProvider(this, (int)sampleRate);
            var wasapi = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
            wasapi.Init(provider);
            wasapi.Play();
            player = wasapi;
        }
    }

    public void ApplyVolumes(Dictionary<string, double> v)
    {
        volumes = v;
        masterGain = v.TryGetValue("master", out var m) ? m : 1;
        foreach (var cat in new[] { "music", "blocks", "hostile", "friendly", "players", "ambient", "records", "ui" })
            catGains[cat] = v.TryGetValue(cat, out var g) ? g : 1;
    }

    public void SetEnvironment(bool underwater, double caveFactor)
    {
        this.underwater = underwater;
        this.caveFactor = caveFactor;
        lowpassTarget = underwater ? 700 : 20000;
        reverbAmt = caveFactor * 0.35;
    }

    public void SetListener(double x, double y, double z, double yaw)
    {
        listenerX = x; listenerY = y; listenerZ = z; listenerYaw = yaw;
    }

    /// play a positional game sound
    public void Play(string name, double x, double y, double z, double volume = 1, double pitch = 1)
    {
        if (!inited) return;
        double dx = x - listenerX, dy = y - listenerY, dz = z - listenerZ;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double maxDist = 18 * Math.Max(1, volume);
        if (dist > maxDist) return;
        double atten = Math.Min(1, Math.Max(0, 1 - dist / maxDist));
        double angle = Math.Atan2(-dx, dz) - listenerYaw;
        double pan = Math.Min(1, Math.Max(-1, -Math.Sin(angle))) * Math.Min(1, Math.Max(0, dist / 4));
        PlayRecipe(name, volume * atten * atten, pitch, pan, true);
    }

    public void PlayUI(string name) => PlayRecipe(name, 0.8, 1, 0, false);

    private void PlayRecipe(string name, double volume, double pitch, double pan, bool allowReverb)
    {
        if (!inited || volume <= 0.001) return;
        var recipe = AudioRecipes.Resolve(name);
        if (recipe == null) return;
        // deterministic-per-call LCG, same constants as the Swift version
        uint seed = unchecked((uint)spawnRng.Next() + 12345u);
        Func<double> rng = () =>
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            return seed / 4294967296.0;
        };
        double gain = Math.Min(1.5, volume) * (catGains.TryGetValue(recipe.cat, out var cg) ? cg : 1);
        double reverbSend = allowReverb && caveFactor > 0.05 ? caveFactor * 0.6 : 0;
        var sink = new VoiceSink(Now(), pan, gain, reverbSend);
        recipe.build(sink, pitch, rng);
        AddVoices(sink.voices);
        if (recipe.subtitle != null) onSubtitle?.Invoke(recipe.subtitle);
    }

    private double Now()
    {
        lock (gate) { return engineTime; }
    }

    private void AddVoices(List<Voice> vs)
    {
        lock (gate) { voiceInbox.AddRange(vs); }
    }

    // ---- generative music ------------------------------------------------------
    public void TickMusic(string mood, bool enabled)
    {
        if (!inited) return;
        if (!enabled || Now() < musicPlayingUntil)
        {
            if (musicTimer > 0) musicTimer -= 1;
            return;
        }
        musicTimer -= 1;
        if (musicTimer <= 0)
        {
            musicTimer = 2400 + spawnRng.Next(0, 3600);
            PlayGenerativeTrack(mood);
        }
    }

    private static readonly Dictionary<string, double[]> Scales = new()
    {
        ["overworld"] = new double[] { 0, 2, 4, 7, 9, 12, 14, 16 },
        ["lush"] = new double[] { 0, 2, 4, 7, 9, 12, 14, 16 },
        ["water"] = new double[] { 0, 3, 5, 7, 10, 12, 15 },
        ["menu"] = new double[] { 0, 2, 4, 7, 9, 12 },
        ["nether"] = new double[] { 0, 1, 4, 5, 8, 12, 13 },
        ["dark"] = new double[] { 0, 3, 5, 6, 10, 12 },
        ["end"] = new double[] { 0, 2, 3, 7, 8, 12, 14 },
    };

    private void PlayGenerativeTrack(string mood)
    {
        double[] scale = Scales.TryGetValue(mood, out var sc) ? sc : Scales["overworld"];
        double baseFreq = mood == "nether" ? 110.0 : mood == "end" ? 130.8 : 220.0;
        double length = 60 + spawnRng.NextDouble() * 30;
        double start = Now();
        musicPlayingUntil = start + length;
        double gain = 0.32 * (catGains.TryGetValue("music", out var mg) ? mg : 1);
        var vs = new List<Voice>();
        bool dark = mood == "nether" || mood == "dark" || mood == "end";

        // chord progression: slow pad triads over scale degrees, 8s per chord,
        // with a sub-octave bass root under each
        var progs = dark
            ? new[] { new[] { 0, 3, 5, 4 }, new[] { 0, 5, 1, 4 } }
            : new[] { new[] { 0, 5, 3, 4 }, new[] { 0, 4, 5, 3 } };
        var prog = progs[spawnRng.Next(0, progs.Length)];
        double chordDur = 8.0;
        double ct = start;
        int ci = 0;
        var triad = new (int di, double vol)[] { (0, 0.05), (2, 0.038), (4, 0.03) };
        while (ct < start + length - 4)
        {
            int rootDeg = prog[ci % prog.Length];
            foreach (var (di, vol) in triad)
            {
                int deg = (rootDeg + di) % scale.Length;
                var v = Voice.Make();
                v.freq = baseFreq / 2 * Math.Pow(2, scale[deg] / 12);
                v.dur = chordDur + 2;
                v.attack = 2.5;
                v.vol = vol;
                v.vibrato = 0.15;
                v.reverbSend = 0.12;
                v.start = ct;
                v.gain = gain;
                vs.Add(v);
            }
            var b = Voice.Make();
            b.freq = baseFreq / 4 * Math.Pow(2, scale[rootDeg] / 12);
            b.dur = chordDur;
            b.attack = 0.8;
            b.vol = 0.06;
            b.start = ct;
            b.gain = gain;
            vs.Add(b);
            ct += chordDur;
            ci += 1;
        }

        // melody: random-walk over the scale in short phrases with rests
        int[] steps = { -2, -1, -1, -1, 0, 1, 1, 1, 2 };
        double t = start + 4;
        int degIdx = scale.Length / 2;
        double[] noteGaps = { 0.5, 0.75, 1, 1, 1.5 };
        while (t < start + length - 6)
        {
            int phraseLen = 6 + spawnRng.Next(0, 5);
            for (int p = 0; p < phraseLen; p++)
            {
                if (t >= start + length - 6) break;
                degIdx = Math.Max(0, Math.Min(scale.Length - 1, degIdx + steps[spawnRng.Next(0, steps.Length)]));
                double freq = baseFreq * Math.Pow(2, scale[degIdx] / 12);
                vs.AddRange(Pluck(freq, t, 2.4, 0.15, gain));
                if (spawnRng.NextDouble() < 0.22)
                {
                    // sparkle: a high slow-vibrato bell shimmering over the note
                    var sp = Voice.Make();
                    sp.freq = freq * 2;
                    sp.dur = 3.0;
                    sp.attack = 0.05;
                    sp.vol = 0.035;
                    sp.vibrato = 0.4;
                    sp.reverbSend = 0.2;
                    sp.start = t + 0.04;
                    sp.gain = gain;
                    vs.Add(sp);
                }
                t += noteGaps[spawnRng.Next(0, noteGaps.Length)] * (dark ? 1.5 : 1.15);
            }
            t += 1.5 + spawnRng.NextDouble() * 2;
        }
        AddVoices(vs);
    }

    private static List<Voice> Pluck(double freq, double when, double decay, double vol, double gain)
    {
        var outv = new List<Voice>();
        foreach (var (mult, v) in new[] { (1.0, 1.0), (2.0, 0.4), (3.0, 0.15) })
        {
            var voice = Voice.Make();
            voice.freq = freq * mult;
            voice.dur = decay;
            voice.attack = 0.02;
            voice.vol = vol * v;
            voice.start = when;
            voice.gain = gain;
            outv.Add(voice);
        }
        return outv;
    }

    /// jukebox discs: longer structured generative pieces
    public void PlayDisc(string discName, double x, double y, double z)
    {
        if (!inited) return;
        StopDisc();
        (double[] scale, double bas, double bpm, bool bass) cfg = discName.Contains("wander")
            ? (new double[] { 0, 2, 4, 7, 9, 12, 14 }, 220, 100, true)
            : discName.Contains("aurora")
                ? (new double[] { 0, 3, 5, 7, 10, 12, 15 }, 261.6, 70, false)
                : (new double[] { 0, 1, 4, 5, 8, 12 }, 146.8, 120, true);
        double beat = 60 / cfg.bpm;
        double length = 60.0;
        double start = Now();
        lock (gate) { discUntil = start + length; }
        double gain = 0.5 * (catGains.TryGetValue("records", out var rg) ? rg : 1);
        var vs = new List<Voice>();
        double t = start + 0.2;
        int step = 0;
        while (t < start + length)
        {
            double note = cfg.scale[(step * 3 + spawnRng.Next(0, 3)) % cfg.scale.Length];
            vs.AddRange(Pluck(cfg.bas * Math.Pow(2, note / 12), t, 1.4, 0.2, gain));
            if (cfg.bass && step % 4 == 0)
                vs.AddRange(Pluck(cfg.bas / 2 * Math.Pow(2, cfg.scale[step % cfg.scale.Length] / 12), t, 2.2, 0.22, gain));
            if (step % 8 == 4) vs.AddRange(Pluck(cfg.bas * 2, t, 0.8, 0.08, gain));
            t += beat / 2;
            step += 1;
        }
        for (int i = 0; i < vs.Count; i++) { var v = vs[i]; v.isDisc = true; vs[i] = v; }
        AddVoices(vs);
    }

    public void StopDisc()
    {
        if (!inited) return;
        // request the cut — the render thread owns `voices`, so we only flag the
        // cut time here and let the render thread silence the disc voices.
        lock (gate)
        {
            if (discUntil > engineTime) discCutAt = engineTime;
            discUntil = 0;
        }
    }

    // ---- render ---------------------------------------------------------------
    // Fills `n` interleaved stereo frames into `outBuf` starting at `offset`.
    // This is the audio thread. Real-time safety: the only lock held is the short
    // `gate` critical section that swaps the inbox into a thread-local list; the
    // heavy per-sample synthesis runs lock-free on that local copy, and the result
    // is written back under the lock at the end. No allocation in the hot loop
    // except the per-call List copy (matching the Swift `var local = voices`).
    internal void Render(float[] outBuf, int offset, int n)
    {
        double dt = 1.0 / sampleRate;

        List<Voice> local;
        double t;
        lock (gate)
        {
            t = engineTime;
            engineTime += n * dt;
            local = new List<Voice>(voices);
            if (voiceInbox.Count > 0)
            {
                local.AddRange(voiceInbox);
                voiceInbox.Clear();
                // generous cap — a full generative track schedules ~250 voices up front
                if (local.Count > 512) local.RemoveRange(0, local.Count - 512);
            }
            if (discCutAt is double cut)
            {
                discCutAt = null;
                for (int i = 0; i < local.Count; i++)
                    if (local[i].isDisc && local[i].start > cut)
                    {
                        var v = local[i]; v.done = true; local[i] = v;
                    }
            }
        }

        // clear the output window
        for (int i = 0; i < n; i++)
        {
            outBuf[offset + 2 * i] = 0;
            outBuf[offset + 2 * i + 1] = 0;
        }
        int noiseMask = noise.Length - 1;

        for (int vi = 0; vi < local.Count; vi++)
        {
            var v = local[vi];
            double end = v.start + v.dur + 0.05;
            if (t >= end) { var d = local[vi]; d.done = true; local[vi] = d; continue; }
            // scheduled in a future block — skip the whole sample loop
            if (v.start >= t + n * dt) continue;

            // biquad coefficients (recomputed per block — voices are short)
            double b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0;
            if (v.isNoise)
            {
                double f0 = Math.Min(sampleRate * 0.45, Math.Max(20, v.filterFreq));
                double w0 = 2 * Math.PI * f0 / sampleRate;
                double alpha = Math.Sin(w0) / (2 * Math.Max(0.01, v.filterQ));
                double cw = Math.Cos(w0);
                double a0;
                if (v.lowpassFilter)
                {
                    b0 = (1 - cw) / 2;
                    b1 = 1 - cw;
                    b2 = (1 - cw) / 2;
                    a0 = 1 + alpha;
                }
                else
                {
                    b0 = alpha;
                    b1 = 0;
                    b2 = -alpha;
                    a0 = 1 + alpha;
                }
                b0 /= a0; b1 /= a0; b2 /= a0;
                a1 = -2 * cw / a0;
                a2 = (1 - alpha) / a0;
            }

            double lt = t;
            float panL = (float)(Math.Min(1, 1 - v.pan) * v.gain);
            float panR = (float)(Math.Min(1, 1 + v.pan) * v.gain);
            for (int i = 0; i < n; i++)
            {
                double rel = lt - v.start;
                lt += dt;
                if (rel < 0) continue;
                if (rel > v.dur + 0.05) break;
                // envelope: linear attack → exponential decay to 0.001 at dur
                double env;
                if (rel < v.attack)
                    env = rel / v.attack * v.vol;
                else if (rel >= v.dur)
                    env = 0;
                else
                {
                    double f = (rel - v.attack) / Math.Max(0.001, v.dur - v.attack);
                    env = v.vol * Math.Pow(0.001 / Math.Max(0.001, v.vol), f);
                }
                if (env <= 0) continue;

                double sample;
                if (v.isNoise)
                {
                    v.noisePos += v.pitchRate;
                    double s2 = noise[(int)v.noisePos & noiseMask];
                    // TDF-II biquad
                    double y = b0 * s2 + v.z1;
                    v.z1 = b1 * s2 - a1 * y + v.z2;
                    v.z2 = b2 * s2 - a2 * y;
                    sample = y;
                }
                else
                {
                    double f = v.freq;
                    if (v.endFreq > 0 && v.dur > 0)
                        f = v.freq * Math.Pow(Math.Max(20, v.endFreq) / v.freq, Math.Min(1, rel / v.dur));
                    if (v.vibrato > 0)
                    {
                        v.lfoPhase += v.vibrato * dt;
                        f += Math.Sin(v.lfoPhase * 2 * Math.PI) * v.freq * 0.04;
                    }
                    v.phase += f * dt;
                    double ph = v.phase - Math.Floor(v.phase);
                    switch (v.type)
                    {
                        case OscType.Sine: sample = Math.Sin(ph * 2 * Math.PI); break;
                        case OscType.Square: sample = ph < 0.5 ? 1 : -1; break;
                        case OscType.Sawtooth: sample = ph * 2 - 1; break;
                        default: sample = ph < 0.5 ? ph * 4 - 1 : 3 - ph * 4; break; // triangle
                    }
                }

                float s = (float)(sample * env);
                outBuf[offset + 2 * i] += s * panL;
                outBuf[offset + 2 * i + 1] += s * panR;
                // reverb send into the delay lines
                if (v.reverbSend > 0)
                {
                    delayL[(delayPos + i) % delayL.Length] += s * (float)v.reverbSend;
                    delayR[(delayPosR + i) % delayR.Length] += s * (float)v.reverbSend;
                }
            }
            local[vi] = v;
        }

        // cave reverb: two feedback delay taps (each line wraps on its OWN length)
        if (reverbAmt > 0.001 || delayL[delayPos] != 0)
        {
            float fb = 0.45f;
            float amt = (float)reverbAmt;
            for (int i = 0; i < n; i++)
            {
                int li = (delayPos + i) % delayL.Length;
                int ri = (delayPosR + i) % delayR.Length;
                float l = delayL[li];
                float rr = delayR[ri];
                outBuf[offset + 2 * i] += l * amt;
                outBuf[offset + 2 * i + 1] += rr * amt;
                delayL[li] = 0;
                delayR[ri] = 0;
                int fl = (li + 11200) % delayL.Length;
                int fr = (ri + 13900) % delayR.Length;
                delayL[fl] += l * fb;
                delayR[fr] += rr * fb;
            }
        }
        delayPos = (delayPos + n) % delayL.Length;
        delayPosR = (delayPosR + n) % delayR.Length;

        // master gain + underwater one-pole-ish lowpass (smoothed biquad)
        lowpassCur += (lowpassTarget - lowpassCur) * Math.Min(1, n / sampleRate * 10);
        float master = (float)masterGain;
        if (lowpassCur < 19000)
        {
            double w0 = 2 * Math.PI * Math.Min(sampleRate * 0.45, lowpassCur) / sampleRate;
            double alpha = Math.Sin(w0) / 1.4142;
            double cw = Math.Cos(w0);
            double a0 = 1 + alpha;
            double b0 = (1 - cw) / 2 / a0, b1 = (1 - cw) / a0, b2 = (1 - cw) / 2 / a0;
            double a1 = -2 * cw / a0, a2 = (1 - alpha) / a0;
            for (int i = 0; i < n; i++)
            {
                double s = (outBuf[offset + 2 * i] + outBuf[offset + 2 * i + 1]) * 0.5;
                double y = b0 * s + lpZ1;
                lpZ1 = b1 * s - a1 * y + lpZ2;
                lpZ2 = b2 * s - a2 * y;
                outBuf[offset + 2 * i] = (float)y * master;
                outBuf[offset + 2 * i + 1] = (float)y * master;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                outBuf[offset + 2 * i] *= master;
                outBuf[offset + 2 * i + 1] *= master;
            }
        }

        // write back voice state, prune finished
        lock (gate)
        {
            voices = local.FindAll(static x => !x.done);
        }
    }

    public void Dispose()
    {
        try { player?.Stop(); } catch { }
        player?.Dispose();
        player = null;
    }
}

/// NAudio bridge: pulls float32 stereo @ sampleRate from the engine's Render().
internal sealed class SynthSampleProvider : ISampleProvider
{
    private readonly AudioEngine engine;
    public WaveFormat WaveFormat { get; }

    public SynthSampleProvider(AudioEngine engine, int sampleRate)
    {
        this.engine = engine;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;     // interleaved stereo
        engine.Render(buffer, offset, frames);
        return frames * 2;
    }
}
