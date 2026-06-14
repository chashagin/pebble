// --audiotest: offline (no device) proof that the synthesizer produces sound.
// Renders representative recipes to an in-memory interleaved-stereo float buffer
// and asserts non-silence: peak amplitude > 0 and energy varies across the clip.

using System;
using NAudio.Wave;

namespace Pebble.Audio;

internal static class AudioTest
{
    private const int SampleRate = 48000;
    private const int Block = 512;          // frames per Render() call, like a real audio quantum

    public static int Run()
    {
        Console.WriteLine("[audiotest] offline synthesis test (no output device)");
        Console.WriteLine($"[audiotest] {SampleRate} Hz stereo float32, ~0.5 s per recipe, {Block}-frame blocks");

        // sounds with a deliberately silent name to prove the silence path is real
        bool allOk = true;
        allOk &= RenderAndCheck("dig.stone (block.stone.hit)", e => e.Play("block.stone.hit", 0, 0, 0, 1, 1), expectSilent: false);
        allOk &= RenderAndCheck("random.click (ui.button.click)", e => e.PlayUI("ui.button.click"), expectSilent: false);
        allOk &= RenderAndCheck("block break (block.stone.break)", e => e.Play("block.stone.break", 0, 0, 0, 1, 1), expectSilent: false);
        allOk &= RenderAndCheck("block break via fallback (block.cobblestone.break)", e => e.Play("block.cobblestone.break", 0, 0, 0, 1, 1), expectSilent: false);
        allOk &= RenderAndCheck("note harp (note.harp.12)", e => e.Play("note.harp.12", 0, 0, 0, 1, 1), expectSilent: false);
        allOk &= RenderAndCheck("explosion (entity.generic.explode)", e => e.Play("entity.generic.explode", 0, 0, 0, 1, 1), expectSilent: false);
        allOk &= RenderAndCheck("zombie ambient (entity.zombie.ambient)", e => e.Play("entity.zombie.ambient", 0, 0, 0, 1, 1), expectSilent: false);
        // jukebox disc: PlayDisc schedules ~831 voices up front; the engine's 512-voice
        // cap (faithful to Swift's `removeFirst`) keeps the LATEST voices, so the disc
        // becomes audible ~23 s in. Render a 30 s window so the kept voices are heard.
        allOk &= RenderAndCheck("jukebox disc (PlayDisc wander)", e => e.PlayDisc("music_disc_wander", 0, 0, 0), expectSilent: false, seconds: 30.0);
        // control: an unknown recipe must produce pure silence
        allOk &= RenderAndCheck("unknown recipe (no.such.sound)", e => e.Play("no.such.sound", 0, 0, 0, 1, 1), expectSilent: true);

        Console.WriteLine(allOk
            ? "[audiotest] PASS — all non-silent recipes produced audible signal; silent control was silent."
            : "[audiotest] FAIL — see lines above.");
        return allOk ? 0 : 1;
    }

    private static bool RenderAndCheck(string label, Action<AudioEngine> trigger, bool expectSilent, double seconds = 0.5)
    {
        var engine = new AudioEngine(SampleRate);
        engine.InitEngine(openDevice: false);
        // listener at origin so positional sounds at (0,0,0) are full-volume
        engine.SetListener(0, 0, 0, 0);
        trigger(engine);

        int totalFrames = (int)(SampleRate * seconds);
        var buf = new float[Block * 2];

        float peak = 0;
        double sumSq = 0;
        long samples = 0;
        // window energies over 10 equal slices, to show energy varies over time
        const int Windows = 10;
        var winEnergy = new double[Windows];
        var winCount = new long[Windows];
        int framesDone = 0;

        while (framesDone < totalFrames)
        {
            int n = Math.Min(Block, totalFrames - framesDone);
            Array.Clear(buf, 0, buf.Length);
            engine.Render(buf, 0, n);
            for (int i = 0; i < n; i++)
            {
                float l = buf[2 * i], rr = buf[2 * i + 1];
                float a = Math.Max(Math.Abs(l), Math.Abs(rr));
                if (a > peak) peak = a;
                double e = (double)l * l + (double)rr * rr;
                sumSq += e;
                samples += 2;
                int w = Math.Min(Windows - 1, (framesDone + i) * Windows / totalFrames);
                winEnergy[w] += e;
                winCount[w] += 1;
            }
            framesDone += n;
        }

        double rms = Math.Sqrt(sumSq / Math.Max(1, samples));
        // energy spread: max window RMS over min (nonzero) window RMS — proves the
        // signal is not a constant DC level (which would also have peak>0).
        double maxW = 0, minW = double.MaxValue;
        int nonzeroWindows = 0;
        for (int w = 0; w < Windows; w++)
        {
            double wr = winCount[w] > 0 ? Math.Sqrt(winEnergy[w] / winCount[w]) : 0;
            if (wr > 0) { nonzeroWindows++; maxW = Math.Max(maxW, wr); minW = Math.Min(minW, wr); }
        }
        bool variesOverTime = nonzeroWindows >= 1 && maxW > 0 && (nonzeroWindows < Windows || maxW > minW * 1.05);

        engine.Dispose();

        bool nonSilent = peak > 1e-5 && rms > 1e-6;
        string verdict;
        bool ok;
        if (expectSilent)
        {
            ok = !nonSilent;
            verdict = ok ? "OK (silent as expected)" : "FAIL (expected silence, got signal)";
        }
        else
        {
            ok = nonSilent && variesOverTime;
            verdict = ok ? "OK (non-silent, energy varies)" : "FAIL (silent or constant)";
        }

        Console.WriteLine($"[audiotest] {label,-44}  peak={peak:F4}  rms={rms:F5}  windows={nonzeroWindows}/{Windows}  -> {verdict}");
        return ok;
    }
}
