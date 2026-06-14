// recipes — the full sound-effect registry, ported from Audio.swift.
// Each recipe is a closure that, given a VoiceSink (start time / pan / gain /
// reverbSend already set), a pitch and a deterministic rng, appends voices.

using System;
using System.Collections.Generic;

namespace Pebble.Audio;

/// Builder a recipe appends voices to. Mirrors the Swift `VoiceSink`.
internal sealed class VoiceSink
{
    public readonly List<Voice> voices = new();
    private readonly double start;
    private readonly double pan;
    private readonly double gain;
    private readonly double reverbSend;

    public VoiceSink(double start, double pan, double gain, double reverbSend)
    {
        this.start = start;
        this.pan = pan;
        this.gain = gain;
        this.reverbSend = reverbSend;
    }

    public void NoiseBurst(double dur, double freq, double q = 1, bool lowpass = false,
                           double vol = 0.8, double attack = 0.005, double pitch = 1, double delay = 0)
    {
        var v = Voice.Make();
        v.isNoise = true;
        v.dur = dur;
        v.filterFreq = freq * pitch;
        v.filterQ = q;
        v.lowpassFilter = lowpass;
        v.vol = vol;
        v.attack = attack;
        v.pitchRate = pitch;
        v.start = start + delay;
        v.pan = pan;
        v.gain = gain;
        v.reverbSend = reverbSend;
        voices.Add(v);
    }

    public void Tone(double freq, double endFreq = 0, double dur = 0.2, OscType type = OscType.Sine,
                     double vol = 0.3, double attack = 0.01, double vibrato = 0, double delay = 0)
    {
        var v = Voice.Make();
        v.freq = freq;
        v.endFreq = endFreq;
        v.dur = dur;
        v.type = type;
        v.vol = vol;
        v.attack = attack;
        v.vibrato = vibrato;
        v.start = start + delay;
        v.pan = pan;
        v.gain = gain;
        v.reverbSend = reverbSend;
        voices.Add(v);
    }
}

internal sealed class SoundRecipe
{
    public readonly string cat;
    public readonly string? subtitle;
    public readonly Action<VoiceSink, double, Func<double>> build;
    public SoundRecipe(string cat, string? subtitle, Action<VoiceSink, double, Func<double>> build)
    {
        this.cat = cat;
        this.subtitle = subtitle;
        this.build = build;
    }
}

internal static class AudioRecipes
{
    private static readonly Dictionary<string, SoundRecipe> RECIPES = new();
    private static bool built;

    private static void R(string name, string cat, string? subtitle,
                          Action<VoiceSink, double, Func<double>> build)
        => RECIPES[name] = new SoundRecipe(cat, subtitle, build);

    private static void MobVoice(string name, string cat, string? subtitle, double bas,
                                 OscType type, double dur, double slide, double vib = 0)
        => R(name, cat, subtitle, (s, pitch, _) =>
            s.Tone(freq: bas * pitch, endFreq: bas * slide * pitch, dur: dur, type: type, vol: 0.35, vibrato: vib));

    private static void BuildRecipes()
    {
        if (built) return;
        built = true;

        // material step/dig sounds
        var MATERIALS = new (string mat, double freq, double q, double dur)[]
        {
            ("stone", 800, 0.8, 0.1), ("deepslate", 600, 0.8, 0.11),
            ("wood", 450, 1.2, 0.12), ("grass", 1400, 0.5, 0.09),
            ("gravel", 1100, 0.4, 0.12), ("sand", 2200, 0.4, 0.1),
            ("snow", 1800, 0.4, 0.12), ("cloth", 900, 0.4, 0.1),
            ("glass", 2600, 3, 0.12), ("metal", 1300, 4, 0.14),
            ("slime", 500, 1, 0.18), ("honey", 400, 1, 0.2),
            ("netherrack", 700, 0.7, 0.1), ("soulsand", 500, 0.5, 0.16),
            ("bone", 1000, 2, 0.1), ("amethyst", 2200, 6, 0.25),
            ("sculk", 350, 1.5, 0.2), ("mud", 450, 0.5, 0.16),
            ("water", 1200, 0.6, 0.2), ("lava", 300, 0.6, 0.3),
            ("bamboo", 600, 2, 0.1), ("cherry", 500, 1.2, 0.12),
            ("copper", 1500, 3.5, 0.13), ("chain", 1700, 5, 0.16),
        };
        foreach (var (mat, freq, q, dur) in MATERIALS)
        {
            R($"block.{mat}.break", "blocks", "Block broken", (s, pitch, _) =>
                s.NoiseBurst(dur: dur * 1.8, freq: freq * 0.8, q: q, vol: 0.7, pitch: pitch));
            R($"block.{mat}.place", "blocks", "Block placed", (s, pitch, _) =>
                s.NoiseBurst(dur: dur, freq: freq, q: q, vol: 0.6, pitch: pitch));
            R($"block.{mat}.step", "blocks", null, (s, pitch, _) =>
                s.NoiseBurst(dur: dur * 0.7, freq: freq * 1.1, q: q, vol: 0.25, pitch: pitch));
            R($"block.{mat}.hit", "blocks", null, (s, pitch, _) =>
                s.NoiseBurst(dur: dur * 0.5, freq: freq, q: q, vol: 0.3, pitch: pitch));
        }

        // note block instruments
        var NOTE_BASE = new (string inst, OscType type, double mult)[]
        {
            ("harp", OscType.Sine, 1), ("bass", OscType.Triangle, 0.25), ("basedrum", OscType.Sine, 0.3),
            ("snare", OscType.Sine, 1), ("hat", OscType.Square, 2), ("guitar", OscType.Triangle, 0.5),
            ("flute", OscType.Sine, 2), ("bell", OscType.Sine, 4), ("chime", OscType.Sine, 4),
            ("xylophone", OscType.Sine, 2), ("iron_xylophone", OscType.Triangle, 1), ("cow_bell", OscType.Square, 1.5),
            ("didgeridoo", OscType.Sawtooth, 0.25), ("bit", OscType.Square, 1), ("banjo", OscType.Sawtooth, 1), ("pling", OscType.Sine, 1),
        };
        for (int note = 0; note < 25; note++)
        {
            int noteLocal = note;
            foreach (var (inst, type, mult) in NOTE_BASE)
            {
                string instLocal = inst; OscType typeLocal = type; double multLocal = mult;
                R($"note.{inst}.{note}", "records", null, (s, _, _) =>
                {
                    double freq = 185 * multLocal * Math.Pow(2, (noteLocal - 12) / 12.0);
                    if (instLocal == "snare")
                        s.NoiseBurst(dur: 0.15, freq: 2000, q: 0.5, vol: 0.5);
                    else if (instLocal == "basedrum")
                        s.Tone(freq: 90, endFreq: 40, dur: 0.2, vol: 0.7);
                    else if (instLocal == "hat")
                        s.NoiseBurst(dur: 0.05, freq: 6000, q: 1, vol: 0.3);
                    else
                    {
                        s.Tone(freq: freq, dur: instLocal == "bell" || instLocal == "chime" ? 1.2 : 0.6, type: typeLocal, vol: 0.4);
                        if (instLocal == "pling" || instLocal == "harp") s.Tone(freq: freq * 2, dur: 0.3, vol: 0.1);
                    }
                });
            }
        }

        // generic events
        R("entity.item.pickup", "players", null, (s, pitch, _) =>
            s.Tone(freq: 600 * pitch, endFreq: 1200 * pitch, dur: 0.1, vol: 0.25));
        R("entity.player.levelup", "players", null, (s, _, _) =>
        {
            for (int i = 0; i < 4; i++) s.Tone(freq: 500 + i * 200, dur: 0.3, vol: 0.2, delay: i * 0.07);
        });
        R("entity.experience_orb.pickup", "players", null, (s, pitch, _) =>
            s.Tone(freq: 1200 * pitch, endFreq: 2200 * pitch, dur: 0.12, vol: 0.18));
        R("entity.item.break", "players", "Item breaks", (s, _, _) =>
        {
            s.NoiseBurst(dur: 0.18, freq: 1400, q: 2, vol: 0.5);
            s.Tone(freq: 400, endFreq: 150, dur: 0.2, type: OscType.Square, vol: 0.2);
        });
        R("entity.generic.eat", "players", "Eating", (s, _, rng) =>
        {
            for (int i = 0; i < 3; i++) s.NoiseBurst(dur: 0.08, freq: 600 + rng() * 400, q: 1.5, vol: 0.4, delay: i * 0.09);
        });
        R("entity.generic.drink", "players", "Drinking", (s, _, _) =>
        {
            for (int i = 0; i < 4; i++) s.Tone(freq: 400 + i * 80, endFreq: 300, dur: 0.07, vol: 0.2, delay: i * 0.08);
        });
        R("entity.player.burp", "players", "Burp", (s, _, _) =>
            s.Tone(freq: 180, endFreq: 90, dur: 0.25, type: OscType.Sawtooth, vol: 0.3));
        R("entity.generic.explode", "blocks", "Explosion", (s, pitch, _) =>
        {
            s.NoiseBurst(dur: 0.8, freq: 150, q: 0.3, lowpass: true, vol: 1.2, pitch: pitch);
            s.Tone(freq: 100, endFreq: 30, dur: 0.6, vol: 0.8);
        });
        R("entity.generic.big_fall", "players", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 500, q: 0.6, vol: 0.5));
        R("entity.generic.small_fall", "players", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.08, freq: 700, q: 0.6, vol: 0.3));
        R("entity.player.attack.strong", "players", null, (s, pitch, _) =>
            s.NoiseBurst(dur: 0.12, freq: 900, q: 0.6, vol: 0.4, pitch: pitch));
        R("entity.player.attack.weak", "players", null, (s, pitch, _) =>
            s.NoiseBurst(dur: 0.08, freq: 1200, q: 0.8, vol: 0.25, pitch: pitch));
        R("entity.player.attack.crit", "players", "Critical hit", (s, _, _) =>
        {
            s.NoiseBurst(dur: 0.15, freq: 1600, q: 1.5, vol: 0.45);
            s.Tone(freq: 800, endFreq: 1400, dur: 0.1, vol: 0.2);
        });
        R("entity.player.attack.sweep", "players", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 2400, q: 0.7, vol: 0.3, attack: 0.04));
        R("entity.player.death", "players", "Player dies", (s, _, _) =>
            s.Tone(freq: 400, endFreq: 100, dur: 0.5, type: OscType.Square, vol: 0.3));
        R("entity.player.hurt", "players", "Player hurts", (s, _, _) =>
            s.Tone(freq: 350, endFreq: 220, dur: 0.18, type: OscType.Square, vol: 0.25));

        // mob voices
        MobVoice("entity.cow.ambient", "friendly", "Cow moos", 160, OscType.Sawtooth, 0.6, 0.75, 5);
        MobVoice("entity.cow.hurt", "friendly", "Cow hurts", 200, OscType.Sawtooth, 0.3, 0.7);
        MobVoice("entity.cow.death", "friendly", "Cow dies", 180, OscType.Sawtooth, 0.5, 0.5);
        R("entity.cow.milk", "friendly", "Cow gets milked", (s, _, _) =>
        {
            for (int i = 0; i < 2; i++) s.NoiseBurst(dur: 0.1, freq: 900, q: 1, vol: 0.3, delay: i * 0.12);
        });
        MobVoice("entity.pig.ambient", "friendly", "Pig oinks", 320, OscType.Square, 0.18, 1.3);
        MobVoice("entity.pig.hurt", "friendly", "Pig hurts", 380, OscType.Square, 0.25, 1.4);
        MobVoice("entity.pig.death", "friendly", "Pig dies", 350, OscType.Square, 0.4, 0.6);
        MobVoice("entity.sheep.ambient", "friendly", "Sheep baahs", 420, OscType.Sawtooth, 0.5, 0.95, 9);
        MobVoice("entity.sheep.hurt", "friendly", "Sheep hurts", 460, OscType.Sawtooth, 0.3, 0.9, 9);
        MobVoice("entity.sheep.death", "friendly", "Sheep dies", 420, OscType.Sawtooth, 0.45, 0.6, 9);
        R("entity.sheep.shear", "friendly", "Shears click", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 3000, q: 2, vol: 0.4));
        MobVoice("entity.chicken.ambient", "friendly", "Chicken clucks", 700, OscType.Square, 0.12, 1.25);
        MobVoice("entity.chicken.hurt", "friendly", "Chicken hurts", 800, OscType.Square, 0.15, 1.3);
        MobVoice("entity.chicken.death", "friendly", "Chicken dies", 750, OscType.Square, 0.3, 0.6);
        R("entity.chicken.egg", "friendly", "Chicken plops", (s, _, _) =>
            s.Tone(freq: 500, endFreq: 900, dur: 0.1, vol: 0.25));
        MobVoice("entity.zombie.ambient", "hostile", "Zombie groans", 140, OscType.Sawtooth, 0.7, 0.8, 3);
        MobVoice("entity.zombie.hurt", "hostile", "Zombie hurts", 160, OscType.Sawtooth, 0.3, 0.75, 3);
        MobVoice("entity.zombie.death", "hostile", "Zombie dies", 150, OscType.Sawtooth, 0.6, 0.5, 3);
        MobVoice("entity.husk.ambient", "hostile", "Husk groans", 120, OscType.Sawtooth, 0.7, 0.8, 3);
        MobVoice("entity.drowned.ambient", "hostile", "Drowned gurgles", 130, OscType.Sawtooth, 0.6, 0.7, 8);
        R("entity.skeleton.ambient", "hostile", "Skeleton rattles", (s, _, rng) =>
        {
            for (int i = 0; i < 4; i++) s.NoiseBurst(dur: 0.05, freq: 1800 + rng() * 800, q: 4, vol: 0.3, delay: i * 0.06);
        });
        R("entity.skeleton.hurt", "hostile", "Skeleton hurts", (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 1500, q: 3, vol: 0.4));
        R("entity.skeleton.death", "hostile", "Skeleton dies", (s, _, rng) =>
        {
            for (int i = 0; i < 6; i++) s.NoiseBurst(dur: 0.06, freq: 1400 + rng() * 1000, q: 4, vol: 0.35, delay: i * 0.05);
        });
        R("entity.skeleton.shoot", "hostile", "Arrow fired", (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 2000, q: 1, vol: 0.35));
        R("entity.creeper.primed", "hostile", "Creeper hisses", (s, _, _) =>
            s.NoiseBurst(dur: 1.4, freq: 3500, q: 0.4, vol: 0.5, attack: 0.3));
        MobVoice("entity.creeper.hurt", "hostile", "Creeper hurts", 300, OscType.Triangle, 0.2, 0.7);
        MobVoice("entity.creeper.death", "hostile", "Creeper dies", 280, OscType.Triangle, 0.4, 0.5);
        R("entity.spider.ambient", "hostile", "Spider hisses", (s, _, _) =>
            s.NoiseBurst(dur: 0.3, freq: 2600, q: 1.2, vol: 0.3));
        MobVoice("entity.spider.hurt", "hostile", "Spider hurts", 900, OscType.Square, 0.15, 0.8);
        MobVoice("entity.spider.death", "hostile", "Spider dies", 800, OscType.Square, 0.35, 0.5);
        R("entity.enderman.ambient", "hostile", "Enderman vwoops", (s, pitch, _) =>
        {
            s.Tone(freq: 90 * pitch, endFreq: 50 * pitch, dur: 0.8, vol: 0.4, vibrato: 2);
            s.Tone(freq: 180 * pitch, endFreq: 80, dur: 0.6, type: OscType.Triangle, vol: 0.15);
        });
        R("entity.enderman.teleport", "hostile", "Enderman teleports", (s, _, _) =>
            s.Tone(freq: 1800, endFreq: 200, dur: 0.3, vol: 0.35));
        R("entity.enderman.stare", "hostile", "Enderman cries out", (s, _, _) =>
            s.Tone(freq: 600, endFreq: 100, dur: 1.0, type: OscType.Sawtooth, vol: 0.4, vibrato: 6));
        MobVoice("entity.enderman.hurt", "hostile", "Enderman hurts", 200, OscType.Sine, 0.3, 0.5);
        MobVoice("entity.enderman.death", "hostile", "Enderman dies", 160, OscType.Sine, 0.7, 0.3);
        R("entity.ghast.ambient", "hostile", "Ghast cries", (s, pitch, _) =>
            s.Tone(freq: 700 * pitch, endFreq: 400 * pitch, dur: 1.2, vol: 0.35, vibrato: 4));
        R("entity.ghast.warn", "hostile", "Ghast shrieks", (s, _, _) =>
            s.Tone(freq: 500, endFreq: 1200, dur: 0.5, type: OscType.Sawtooth, vol: 0.4));
        R("entity.ghast.shoot", "hostile", "Fireball whooshes", (s, _, _) =>
            s.NoiseBurst(dur: 0.4, freq: 800, q: 0.5, vol: 0.5));
        MobVoice("entity.ghast.hurt", "hostile", "Ghast hurts", 800, OscType.Sine, 0.4, 1.3, 6);
        MobVoice("entity.ghast.death", "hostile", "Ghast dies", 700, OscType.Sine, 1.0, 0.4, 6);
        MobVoice("entity.blaze.ambient", "hostile", "Blaze breathes", 200, OscType.Sawtooth, 0.5, 1.1, 12);
        R("entity.blaze.shoot", "hostile", "Blaze shoots", (s, _, _) =>
            s.NoiseBurst(dur: 0.25, freq: 1200, q: 0.6, vol: 0.45));
        MobVoice("entity.blaze.hurt", "hostile", "Blaze hurts", 300, OscType.Sawtooth, 0.25, 0.8, 10);
        MobVoice("entity.blaze.death", "hostile", "Blaze dies", 260, OscType.Sawtooth, 0.5, 0.4, 10);
        MobVoice("entity.slime.jump", "hostile", "Slime squishes", 250, OscType.Sine, 0.15, 0.6);
        MobVoice("entity.slime.hurt", "hostile", "Slime hurts", 280, OscType.Sine, 0.15, 0.5);
        MobVoice("entity.slime.death", "hostile", "Slime dies", 240, OscType.Sine, 0.25, 0.4);
        MobVoice("entity.witch.ambient", "hostile", "Witch giggles", 500, OscType.Square, 0.4, 1.4, 10);
        R("entity.witch.throw", "hostile", "Witch throws", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 1400, q: 1, vol: 0.3));
        R("entity.witch.drink", "hostile", "Witch drinks", (s, _, _) =>
        {
            for (int i = 0; i < 3; i++) s.Tone(freq: 450, endFreq: 350, dur: 0.07, vol: 0.2, delay: i * 0.09);
        });
        MobVoice("entity.witch.hurt", "hostile", "Witch hurts", 550, OscType.Square, 0.25, 0.8);
        MobVoice("entity.witch.death", "hostile", "Witch dies", 500, OscType.Square, 0.5, 0.4);
        MobVoice("entity.wolf.ambient", "friendly", "Wolf pants", 600, OscType.Sawtooth, 0.12, 1.1);
        MobVoice("entity.wolf.hurt", "friendly", "Wolf yelps", 800, OscType.Sawtooth, 0.2, 1.5);
        MobVoice("entity.wolf.death", "friendly", "Wolf whines", 700, OscType.Sawtooth, 0.6, 0.4, 6);
        MobVoice("entity.cat.ambient", "friendly", "Cat meows", 700, OscType.Sine, 0.45, 1.3, 7);
        MobVoice("entity.cat.hurt", "friendly", "Cat hisses", 900, OscType.Sawtooth, 0.25, 1.1);
        MobVoice("entity.villager.ambient", "friendly", "Villager mumbles", 280, OscType.Sawtooth, 0.35, 1.2, 4);
        MobVoice("entity.villager.yes", "friendly", "Villager agrees", 280, OscType.Sawtooth, 0.3, 1.5, 4);
        MobVoice("entity.villager.no", "friendly", "Villager disagrees", 300, OscType.Sawtooth, 0.35, 0.7, 4);
        MobVoice("entity.villager.trade", "friendly", null, 280, OscType.Sawtooth, 0.3, 1.3, 4);
        MobVoice("entity.villager.hurt", "friendly", "Villager hurts", 320, OscType.Sawtooth, 0.25, 0.8, 4);
        MobVoice("entity.villager.death", "friendly", "Villager dies", 280, OscType.Sawtooth, 0.5, 0.5, 4);
        MobVoice("entity.villager.work", "friendly", null, 350, OscType.Square, 0.1, 1.1);
        MobVoice("entity.iron_golem.attack", "friendly", "Iron Golem attacks", 150, OscType.Square, 0.25, 0.6);
        R("entity.iron_golem.repair", "friendly", "Iron Golem repaired", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1600, q: 3, vol: 0.4));
        MobVoice("entity.zombified_piglin.angry", "hostile", "Zombified Piglin angers", 350, OscType.Sawtooth, 0.4, 1.4, 8);
        MobVoice("entity.piglin.admiring_item", "hostile", "Piglin admires item", 400, OscType.Square, 0.3, 1.3, 5);
        MobVoice("entity.warden.ambient", "hostile", "Warden whines", 60, OscType.Sawtooth, 1.0, 0.8, 2);
        R("entity.warden.heartbeat", "hostile", "Warden's heart beats", (s, _, _) =>
        {
            s.Tone(freq: 55, endFreq: 35, dur: 0.18, vol: 0.7);
            s.Tone(freq: 50, endFreq: 32, dur: 0.15, vol: 0.5, delay: 0.2);
        });
        R("entity.warden.sonic_boom", "hostile", "Sonic boom", (s, _, _) =>
        {
            s.Tone(freq: 120, endFreq: 60, dur: 0.7, type: OscType.Sawtooth, vol: 0.8);
            s.NoiseBurst(dur: 0.5, freq: 400, q: 0.4, vol: 0.6);
        });
        R("entity.warden.sonic_charge", "hostile", "Warden charges", (s, _, _) =>
            s.Tone(freq: 80, endFreq: 300, dur: 0.5, type: OscType.Sawtooth, vol: 0.4));
        R("entity.warden.emerge", "hostile", "Warden emerges", (s, _, _) =>
        {
            s.NoiseBurst(dur: 1.2, freq: 200, q: 0.4, lowpass: true, vol: 0.7);
            s.Tone(freq: 50, endFreq: 90, dur: 1.2, type: OscType.Sawtooth, vol: 0.5);
        });
        R("entity.warden.dig", "hostile", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.3, freq: 350, q: 0.5, vol: 0.4));
        R("entity.warden.sniff", "hostile", "Warden sniffs", (s, _, _) =>
            s.NoiseBurst(dur: 0.4, freq: 500, q: 0.6, vol: 0.3, attack: 0.15));
        R("entity.warden.listening", "hostile", "Warden takes notice", (s, _, _) =>
            s.Tone(freq: 200, endFreq: 150, dur: 0.4, vol: 0.3, vibrato: 4));
        R("entity.warden.angry", "hostile", "Warden roars", (s, _, _) =>
            s.Tone(freq: 90, endFreq: 140, dur: 0.9, type: OscType.Sawtooth, vol: 0.7, vibrato: 8));
        R("entity.ender_dragon.growl", "hostile", "Dragon roars", (s, _, _) =>
        {
            s.Tone(freq: 110, endFreq: 70, dur: 1.4, type: OscType.Sawtooth, vol: 0.8, vibrato: 6);
            s.NoiseBurst(dur: 1.2, freq: 350, q: 0.5, vol: 0.4);
        });
        MobVoice("entity.ender_dragon.hurt", "hostile", "Dragon hurts", 180, OscType.Sawtooth, 0.5, 0.6, 6);
        R("entity.ender_dragon.death", "hostile", "Dragon dies", (s, _, _) =>
            s.Tone(freq: 200, endFreq: 40, dur: 3.0, type: OscType.Sawtooth, vol: 0.8, vibrato: 4));
        R("entity.ender_dragon.shoot", "hostile", "Dragon shoots", (s, _, _) =>
            s.NoiseBurst(dur: 0.3, freq: 900, q: 0.6, vol: 0.5));
        R("entity.wither.spawn", "hostile", "Wither released", (s, _, _) =>
        {
            s.Tone(freq: 60, endFreq: 220, dur: 1.5, type: OscType.Sawtooth, vol: 0.7);
            s.NoiseBurst(dur: 1.5, freq: 300, q: 0.4, vol: 0.5);
        });
        MobVoice("entity.wither.ambient", "hostile", "Wither angers", 220, OscType.Sawtooth, 0.5, 0.7, 10);
        R("entity.wither.shoot", "hostile", "Wither attacks", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1000, q: 0.8, vol: 0.45));
        R("entity.wither.break_block", "hostile", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.25, freq: 600, q: 0.6, vol: 0.4));
        MobVoice("entity.bat.ambient", "ambient", "Bat squeaks", 2400, OscType.Square, 0.08, 1.3);
        MobVoice("entity.bee.ambient", "friendly", "Bee buzzes", 220, OscType.Sawtooth, 0.4, 1.05, 25);
        MobVoice("entity.fox.ambient", "friendly", "Fox squeaks", 800, OscType.Square, 0.2, 1.4);
        MobVoice("entity.frog.ambient", "friendly", "Frog croaks", 180, OscType.Square, 0.2, 0.8, 14);
        R("entity.frog.eat", "friendly", "Frog eats", (s, _, _) =>
            s.Tone(freq: 500, endFreq: 200, dur: 0.15, vol: 0.3));
        MobVoice("entity.goat.ambient", "friendly", "Goat bleats", 500, OscType.Sawtooth, 0.4, 1.1, 12);
        R("entity.goat.ram_impact", "friendly", "Goat rams", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 500, q: 0.6, vol: 0.5));
        MobVoice("entity.horse.ambient", "friendly", "Horse neighs", 400, OscType.Sawtooth, 0.6, 1.3, 10);
        MobVoice("entity.parrot.ambient", "friendly", "Parrot talks", 1200, OscType.Square, 0.2, 1.4, 8);
        MobVoice("entity.dolphin.ambient", "friendly", "Dolphin chirps", 1800, OscType.Sine, 0.2, 1.6, 12);
        MobVoice("entity.axolotl.ambient", "friendly", "Axolotl chirps", 900, OscType.Sine, 0.12, 1.3);
        MobVoice("entity.allay.ambient", "friendly", "Allay hums", 900, OscType.Sine, 0.4, 1.2, 6);
        R("entity.allay.item_given", "friendly", "Allay chortles", (s, _, _) =>
        {
            for (int i = 0; i < 3; i++) s.Tone(freq: 800 + i * 200, dur: 0.15, vol: 0.2, delay: i * 0.08);
        });
        R("entity.allay.item_taken", "friendly", null, (s, _, _) =>
            s.Tone(freq: 1000, endFreq: 1400, dur: 0.15, vol: 0.2));
        MobVoice("entity.shulker.shoot", "hostile", "Shulker shoots", 600, OscType.Sine, 0.2, 1.4);
        MobVoice("entity.shulker.teleport", "hostile", "Shulker teleports", 1200, OscType.Sine, 0.25, 0.4);
        MobVoice("entity.phantom.swoop", "hostile", "Phantom swoops", 1400, OscType.Sawtooth, 0.4, 0.5);
        MobVoice("entity.guardian.attack", "hostile", "Guardian charges", 300, OscType.Sawtooth, 0.8, 1.6, 4);
        MobVoice("entity.elder_guardian.curse", "hostile", "Elder Guardian curses", 200, OscType.Sine, 1.2, 0.5, 3);
        MobVoice("entity.ravager.roar", "hostile", "Ravager roars", 130, OscType.Sawtooth, 0.8, 0.7, 7);
        MobVoice("entity.evoker.cast_spell", "hostile", "Evoker casts spell", 600, OscType.Square, 0.4, 0.8, 9);
        MobVoice("entity.evoker.prepare_summon", "hostile", "Evoker prepares summoning", 400, OscType.Square, 0.6, 1.4, 9);
        R("entity.evoker_fangs.attack", "hostile", "Fangs snap", (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 800, q: 1.5, vol: 0.4));
        MobVoice("entity.vex.ambient", "hostile", "Vex vexes", 1000, OscType.Square, 0.25, 1.3, 14);
        MobVoice("entity.pillager.ambient", "hostile", "Pillager murmurs", 250, OscType.Sawtooth, 0.4, 0.9, 3);
        MobVoice("entity.vindicator.ambient", "hostile", "Vindicator mutters", 230, OscType.Sawtooth, 0.4, 0.85, 3);
        R("entity.turtle.egg_crack", "friendly", "Egg cracks", (s, _, _) =>
            s.NoiseBurst(dur: 0.08, freq: 2000, q: 3, vol: 0.3));
        R("entity.turtle.egg_hatch", "friendly", "Egg hatches", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 1800, q: 2, vol: 0.35));
        R("entity.sniffer.digging", "friendly", "Sniffer digs", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 400, q: 0.5, vol: 0.35));
        R("entity.sniffer.egg_hatch", "friendly", "Sniffer hatches", (s, _, _) =>
            s.NoiseBurst(dur: 0.3, freq: 1200, q: 1.5, vol: 0.4));
        R("entity.sniffer.egg_crack", "friendly", "Egg cracks", (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 1800, q: 2, vol: 0.3));
        MobVoice("entity.camel.dash", "friendly", "Camel dashes", 300, OscType.Sawtooth, 0.3, 1.3, 6);
        MobVoice("entity.goat.milk", "friendly", null, 500, OscType.Sine, 0.2, 1.1);
        MobVoice("entity.mooshroom.milk", "friendly", null, 400, OscType.Sine, 0.25, 1.1);
        MobVoice("entity.mooshroom.shear", "friendly", "Mooshroom transforms", 600, OscType.Sine, 0.3, 1.4);
        MobVoice("entity.cod.flop", "friendly", "Fish flops", 700, OscType.Sine, 0.1, 1.4);
        MobVoice("entity.llama.spit", "friendly", "Llama spits", 900, OscType.Square, 0.12, 0.7);
        MobVoice("entity.strider.ambient", "friendly", "Strider chirps", 500, OscType.Square, 0.3, 1.5, 18);
        MobVoice("entity.zombie_villager.cure", "friendly", "Zombie Villager sizzles", 300, OscType.Sawtooth, 0.8, 1.6, 12);
        MobVoice("entity.hoglin.ambient", "hostile", "Hoglin growls", 200, OscType.Sawtooth, 0.4, 0.8, 8);
        MobVoice("entity.horse.angry", "friendly", "Horse neighs", 450, OscType.Sawtooth, 0.5, 1.5, 12);
        R("entity.horse.saddle", "friendly", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 1100, q: 1, vol: 0.3));
        R("entity.pig.saddle", "friendly", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 1100, q: 1, vol: 0.3));

        // projectiles & misc
        R("entity.arrow.shoot", "players", "Arrow fired", (s, pitch, _) =>
            s.NoiseBurst(dur: 0.12, freq: 1800, q: 1, vol: 0.35, pitch: pitch));
        R("entity.arrow.hit", "players", "Arrow hits", (s, _, _) =>
            s.NoiseBurst(dur: 0.06, freq: 2400, q: 2, vol: 0.3));
        R("entity.arrow.hit_player", "players", "Arrow hits", (s, _, _) =>
            s.Tone(freq: 1200, endFreq: 600, dur: 0.08, vol: 0.3));
        R("entity.snowball.throw", "players", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 1500, q: 0.8, vol: 0.25));
        R("entity.fishing_bobber.throw", "players", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 1400, q: 1, vol: 0.25));
        R("entity.fishing_bobber.splash", "players", "Fish bites", (s, _, _) =>
            s.NoiseBurst(dur: 0.25, freq: 1000, q: 0.5, vol: 0.45));
        R("entity.tnt.primed", "blocks", "TNT fizzes", (s, _, _) =>
            s.NoiseBurst(dur: 0.5, freq: 3000, q: 0.4, vol: 0.4));
        R("entity.lightning_bolt.thunder", "ambient", "Thunder roars", (s, pitch, _) =>
            s.NoiseBurst(dur: 2.2, freq: 120, q: 0.3, lowpass: true, vol: 1.1, attack: 0.02, pitch: pitch));
        R("entity.lightning_bolt.impact", "ambient", "Lightning strikes", (s, _, _) =>
            s.NoiseBurst(dur: 0.4, freq: 4000, q: 0.3, vol: 0.7));
        R("entity.firework_rocket.launch", "ambient", "Firework launches", (s, _, _) =>
            s.NoiseBurst(dur: 0.5, freq: 2200, q: 0.5, vol: 0.4, attack: 0.05));
        R("entity.firework_rocket.blast", "ambient", "Firework blasts", (s, _, _) =>
        {
            s.NoiseBurst(dur: 0.4, freq: 600, q: 0.5, vol: 0.6);
            s.NoiseBurst(dur: 0.3, freq: 2400, q: 1, vol: 0.3, delay: 0.06);
        });
        R("entity.ender_eye.launch", "players", "Eye of Ender shoots", (s, _, _) =>
            s.Tone(freq: 600, endFreq: 1400, dur: 0.4, vol: 0.3));
        R("entity.ender_eye.death", "players", "Eye of Ender breaks", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 2200, q: 2, vol: 0.35));
        R("item.totem.use", "players", "Totem activates", (s, _, _) =>
        {
            for (int i = 0; i < 5; i++) s.Tone(freq: 600 + i * 150, dur: 0.4, vol: 0.25, delay: i * 0.06);
        });
        R("item.trident.throw", "players", "Trident clangs", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1600, q: 2, vol: 0.4));
        R("item.trident.hit_ground", "players", "Trident vibrates", (s, _, _) =>
            s.Tone(freq: 800, endFreq: 600, dur: 0.3, type: OscType.Triangle, vol: 0.3, vibrato: 20));
        R("item.trident.riptide_1", "players", "Trident zooms", (s, _, _) =>
            s.NoiseBurst(dur: 0.5, freq: 1200, q: 0.5, vol: 0.5));
        R("item.flintandsteel.use", "blocks", "Flint and Steel click", (s, _, _) =>
            s.NoiseBurst(dur: 0.07, freq: 3000, q: 3, vol: 0.4));
        R("item.bucket.fill", "blocks", "Bucket fills", (s, _, _) =>
        {
            s.Tone(freq: 500, endFreq: 900, dur: 0.25, vol: 0.3);
            s.NoiseBurst(dur: 0.2, freq: 1200, q: 0.7, vol: 0.25);
        });
        R("item.bucket.empty", "blocks", "Bucket empties", (s, _, _) =>
        {
            s.Tone(freq: 900, endFreq: 400, dur: 0.3, vol: 0.3);
            s.NoiseBurst(dur: 0.3, freq: 1000, q: 0.6, vol: 0.3);
        });
        R("item.bucket.fill_lava", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.3, freq: 400, q: 0.6, vol: 0.4));
        R("item.bucket.empty_lava", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.35, freq: 350, q: 0.6, vol: 0.4));
        R("item.bucket.fill_fish", "friendly", "Fish captured", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1100, q: 0.7, vol: 0.35));
        R("item.bottle.fill", "blocks", "Bottle fills", (s, _, _) =>
            s.Tone(freq: 700, endFreq: 1300, dur: 0.2, vol: 0.25));
        R("item.hoe.till", "blocks", "Hoe tills", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 900, q: 0.6, vol: 0.4));
        R("item.shovel.flatten", "blocks", "Shovel flattens", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 1000, q: 0.6, vol: 0.4));
        R("item.axe.strip", "blocks", "Axe strips", (s, _, _) =>
            s.NoiseBurst(dur: 0.18, freq: 700, q: 0.9, vol: 0.45));
        R("item.axe.scrape", "blocks", "Axe scrapes", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1800, q: 1.5, vol: 0.4));
        R("item.armor.equip_generic", "players", "Gear equips", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 1100, q: 1, vol: 0.3));
        R("item.armor.equip_elytra", "players", "Gear equips", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 1100, q: 1, vol: 0.3));
        R("item.chorus_fruit.teleport", "players", "Player teleports", (s, _, _) =>
            s.Tone(freq: 1600, endFreq: 300, dur: 0.3, vol: 0.3));
        R("item.goat_horn.sound", "records", "Goat horn sounds", (s, _, _) =>
        {
            s.Tone(freq: 311, dur: 1.8, type: OscType.Sawtooth, vol: 0.35, vibrato: 3);
            s.Tone(freq: 466, dur: 1.8, vol: 0.15);
        });
        R("item.brush.brushing", "blocks", "Brushing", (s, _, _) =>
            s.NoiseBurst(dur: 0.25, freq: 1900, q: 0.7, vol: 0.3));

        // blocks & machines
        R("block.chest.open", "blocks", "Chest opens", (s, _, _) =>
        {
            s.Tone(freq: 200, endFreq: 350, dur: 0.3, type: OscType.Triangle, vol: 0.3);
            s.NoiseBurst(dur: 0.2, freq: 600, q: 0.8, vol: 0.2);
        });
        R("block.chest.close", "blocks", "Chest closes", (s, _, _) =>
            s.Tone(freq: 350, endFreq: 180, dur: 0.2, type: OscType.Triangle, vol: 0.3));
        R("block.barrel.open", "blocks", "Barrel opens", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 500, q: 1, vol: 0.3));
        R("block.ender_chest.open", "blocks", "Ender Chest opens", (s, _, _) =>
            s.Tone(freq: 300, endFreq: 600, dur: 0.4, vol: 0.3));
        R("block.wooden_door.open", "blocks", "Door creaks", (s, _, rng) =>
            s.NoiseBurst(dur: 0.15, freq: 400 + rng() * 200, q: 2, vol: 0.4));
        R("block.wooden_door.close", "blocks", "Door slams", (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 350, q: 1.5, vol: 0.4));
        R("block.wooden_trapdoor.open", "blocks", "Trapdoor creaks", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 450, q: 2, vol: 0.4));
        R("block.wooden_trapdoor.close", "blocks", "Trapdoor slams", (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 380, q: 1.5, vol: 0.4));
        R("block.fence_gate.open", "blocks", "Gate creaks", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 500, q: 2, vol: 0.35));
        R("block.fence_gate.close", "blocks", "Gate slams", (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 420, q: 1.5, vol: 0.35));
        R("block.lever.click", "blocks", "Lever clicks", (s, pitch, _) =>
            s.NoiseBurst(dur: 0.05, freq: 1400 * pitch, q: 4, vol: 0.35));
        R("block.stone_button.click_on", "blocks", "Button clicks", (s, _, _) =>
            s.NoiseBurst(dur: 0.05, freq: 1600, q: 4, vol: 0.3));
        R("block.stone_button.click_off", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.05, freq: 1300, q: 4, vol: 0.25));
        R("block.stone_pressure_plate.click_on", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.05, freq: 1200, q: 3, vol: 0.25));
        R("block.stone_pressure_plate.click_off", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.05, freq: 1000, q: 3, vol: 0.2));
        R("block.piston.extend", "blocks", "Piston moves", (s, _, _) =>
        {
            s.NoiseBurst(dur: 0.18, freq: 700, q: 0.8, vol: 0.4);
            s.Tone(freq: 300, endFreq: 500, dur: 0.15, type: OscType.Triangle, vol: 0.2);
        });
        R("block.piston.contract", "blocks", "Piston moves", (s, _, _) =>
        {
            s.NoiseBurst(dur: 0.18, freq: 600, q: 0.8, vol: 0.4);
            s.Tone(freq: 500, endFreq: 300, dur: 0.15, type: OscType.Triangle, vol: 0.2);
        });
        R("block.comparator.click", "blocks", null, (s, pitch, _) =>
            s.NoiseBurst(dur: 0.04, freq: 1800 * pitch, q: 5, vol: 0.25));
        R("block.dispenser.dispense", "blocks", "Dispensed item", (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 1100, q: 1, vol: 0.35));
        R("block.dispenser.fail", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.08, freq: 700, q: 2, vol: 0.3));
        R("block.furnace.fire_crackle", "blocks", "Fire crackles", (s, _, rng) =>
        {
            for (int i = 0; i < 3; i++) s.NoiseBurst(dur: 0.05, freq: 2500 + rng() * 2000, q: 2, vol: 0.15, delay: rng() * 0.2);
        });
        R("block.portal.trigger", "blocks", "Portal whooshes", (s, _, _) =>
            s.Tone(freq: 150, endFreq: 700, dur: 1.2, vol: 0.4, vibrato: 5));
        R("block.portal.ambient", "ambient", "Portal whooshes", (s, _, rng) =>
            s.Tone(freq: 100 + rng() * 100, endFreq: 250, dur: 1.5, vol: 0.2, vibrato: 3));
        R("block.portal.travel", "ambient", "Portal noise fades", (s, _, _) =>
            s.Tone(freq: 700, endFreq: 120, dur: 1.0, vol: 0.4, vibrato: 6));
        R("block.end_portal.spawn", "ambient", "End portal opens", (s, _, _) =>
            s.Tone(freq: 80, endFreq: 800, dur: 2, vol: 0.5));
        R("block.end_portal_frame.fill", "blocks", "Eye of Ender attaches", (s, _, _) =>
            s.Tone(freq: 800, endFreq: 1400, dur: 0.3, vol: 0.35));
        R("block.brewing_stand.brew", "blocks", "Brewing Stand bubbles", (s, _, rng) =>
        {
            for (int i = 0; i < 4; i++) s.Tone(freq: 600 + rng() * 600, endFreq: 1200, dur: 0.1, vol: 0.2, delay: i * 0.08);
        });
        R("block.enchantment_table.use", "blocks", "Enchanting", (s, _, rng) =>
        {
            for (int i = 0; i < 4; i++) s.Tone(freq: 900 + rng() * 800, dur: 0.3, vol: 0.2, delay: i * 0.09);
        });
        R("block.anvil.use", "blocks", "Anvil used", (s, _, _) =>
        {
            s.Tone(freq: 1100, dur: 0.5, type: OscType.Square, vol: 0.3);
            s.NoiseBurst(dur: 0.15, freq: 1800, q: 4, vol: 0.4);
        });
        R("block.anvil.land", "blocks", "Anvil lands", (s, _, _) =>
            s.Tone(freq: 900, dur: 0.6, type: OscType.Square, vol: 0.4));
        R("block.anvil.destroy", "blocks", "Anvil destroyed", (s, _, _) =>
            s.NoiseBurst(dur: 0.4, freq: 800, q: 0.7, vol: 0.5));
        R("block.grindstone.use", "blocks", "Grindstone used", (s, _, _) =>
            s.NoiseBurst(dur: 0.4, freq: 1500, q: 1.5, vol: 0.4));
        R("block.smithing_table.use", "blocks", "Smithing Table used", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 1300, q: 2, vol: 0.4));
        R("ui.stonecutter.take_result", "blocks", "Stonecutter used", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 2400, q: 1.5, vol: 0.35));
        R("ui.stonecutter.select_recipe", "ui", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.05, freq: 1600, q: 3, vol: 0.2));
        R("block.bell.use", "blocks", "Bell rings", (s, _, _) =>
        {
            s.Tone(freq: 932, dur: 2.2, vol: 0.4);
            s.Tone(freq: 1864, dur: 1.4, vol: 0.15);
        });
        R("block.campfire.crackle", "blocks", "Campfire crackles", (s, _, rng) =>
            s.NoiseBurst(dur: 0.06, freq: 3000 + rng() * 1500, q: 2, vol: 0.2));
        R("block.fire.extinguish", "blocks", "Fire extinguishes", (s, _, _) =>
            s.NoiseBurst(dur: 0.25, freq: 3500, q: 0.5, vol: 0.4));
        R("block.respawn_anchor.charge", "blocks", "Respawn Anchor charges", (s, _, _) =>
            s.Tone(freq: 400, endFreq: 900, dur: 0.4, vol: 0.35));
        R("block.respawn_anchor.set_spawn", "blocks", "Respawn point set", (s, _, _) =>
            s.Tone(freq: 600, endFreq: 1200, dur: 0.3, vol: 0.3));
        R("block.amethyst_cluster.step", "blocks", "Amethyst chimes", (s, _, rng) =>
            s.Tone(freq: 1800 + rng() * 1200, dur: 0.4, vol: 0.2));
        R("block.sculk_sensor.clicking", "blocks", "Sculk Sensor clicks", (s, _, _) =>
        {
            for (int i = 0; i < 3; i++) s.Tone(freq: 500 - i * 80, dur: 0.06, type: OscType.Square, vol: 0.25, delay: i * 0.05);
        });
        R("block.sculk_shrieker.shriek", "hostile", "Sculk Shrieker shrieks", (s, _, _) =>
            s.Tone(freq: 300, endFreq: 900, dur: 0.8, type: OscType.Sawtooth, vol: 0.5, vibrato: 15));
        R("block.sculk_catalyst.bloom", "blocks", "Sculk blooms", (s, _, _) =>
            s.Tone(freq: 250, endFreq: 120, dur: 0.6, vol: 0.3, vibrato: 8));
        R("block.beacon.power_select", "blocks", "Beacon hums", (s, _, _) =>
            s.Tone(freq: 500, endFreq: 1000, dur: 0.8, vol: 0.3));
        R("block.conduit.ambient", "ambient", "Conduit pulses", (s, _, _) =>
            s.Tone(freq: 250, endFreq: 350, dur: 0.6, vol: 0.15, vibrato: 4));
        R("block.spawner.spawn", "hostile", "Mob spawns", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 900, q: 1.2, vol: 0.35));
        R("block.composter.fill", "blocks", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.12, freq: 800, q: 0.7, vol: 0.3));
        R("block.composter.fill_success", "blocks", "Composter fills", (s, _, _) =>
            s.NoiseBurst(dur: 0.15, freq: 900, q: 0.7, vol: 0.35));
        R("block.composter.empty", "blocks", "Composter empties", (s, _, _) =>
            s.NoiseBurst(dur: 0.18, freq: 700, q: 0.7, vol: 0.35));
        R("block.beehive.shear", "blocks", "Scraping", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 2200, q: 1.5, vol: 0.4));
        R("block.pumpkin.carve", "blocks", "Carving", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1000, q: 1, vol: 0.4));
        R("block.chiseled_bookshelf.insert", "blocks", "Book placed", (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 600, q: 1, vol: 0.3));
        R("block.chiseled_bookshelf.pickup", "blocks", "Book taken", (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 700, q: 1, vol: 0.3));
        R("block.suspicious_sand.break", "blocks", "Block broken", (s, _, _) =>
            s.NoiseBurst(dur: 0.2, freq: 1800, q: 0.4, vol: 0.6));
        R("jukebox.stop", "records", null, (s, _, _) =>
            s.Tone(freq: 400, endFreq: 200, dur: 0.15, vol: 0.2));
        R("event.raid.horn", "hostile", "Raid horn sounds", (s, _, _) =>
        {
            s.Tone(freq: 220, dur: 1.6, type: OscType.Sawtooth, vol: 0.5, vibrato: 2);
            s.Tone(freq: 330, dur: 1.6, type: OscType.Sawtooth, vol: 0.25);
        });
        R("ui.toast.challenge_complete", "ui", null, (s, _, _) =>
        {
            for (int i = 0; i < 5; i++) s.Tone(freq: 520 + i * 130, dur: 0.4, vol: 0.2, delay: i * 0.09);
        });
        R("ui.button.click", "ui", null, (s, _, _) =>
            s.Tone(freq: 500, dur: 0.06, type: OscType.Square, vol: 0.15));
        R("ui.toast.in", "ui", null, (s, _, _) =>
            s.Tone(freq: 800, endFreq: 1200, dur: 0.15, vol: 0.2));
        R("entity.fishing_bobber.retrieve", "players", null, (s, _, _) =>
            s.NoiseBurst(dur: 0.1, freq: 1300, q: 1, vol: 0.3));
    }

    private static readonly string[] HostileNames =
    {
        "zombie", "skeleton", "creeper", "spider", "blaze", "ghast", "witch", "pillager",
        "vindicator", "evoker", "vex", "ravager", "guardian", "shulker", "phantom", "wither",
        "warden", "hoglin", "piglin", "silverfish", "endermite", "stray", "husk", "drowned",
        "magma", "slime", "zoglin",
    };

    public static SoundRecipe? Resolve(string name)
    {
        BuildRecipes();
        if (RECIPES.TryGetValue(name, out var r)) return r;
        if (name.StartsWith("jukebox.play.")) return null;  // handled via PlayDisc
        // block sound fallbacks: block.<material>.<action>
        var parts = name.Split('.');
        if (parts.Length == 3 && parts[0] == "block" &&
            (parts[2] == "break" || parts[2] == "place" || parts[2] == "step" || parts[2] == "hit" || parts[2] == "fall"))
        {
            string action = parts[2] == "fall" ? "step" : parts[2];
            return RECIPES.TryGetValue($"block.stone.{action}", out var br) ? br : null;
        }
        // entity fallbacks
        if (parts.Length == 3 && parts[0] == "entity" &&
            (parts[2] == "ambient" || parts[2] == "hurt" || parts[2] == "death"))
        {
            bool hostile = Array.Exists(HostileNames, h => parts[1].Contains(h));
            string key = hostile ? $"entity.zombie.{parts[2]}" : $"entity.pig.{parts[2]}";
            return RECIPES.TryGetValue(key, out var er) ? er : null;
        }
        return null;
    }
}
