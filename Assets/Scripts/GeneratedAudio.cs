using UnityEngine;

public static class GeneratedAudio
{
    static AudioClip hum;
    static AudioClip whisper;
    static AudioClip drone;
    static AudioClip notification;
    static AudioClip crack;

    public static AudioClip Hum => hum != null ? hum : hum = NoiseLoop("pc-hum", 22050, 1.4f, 0.035f);
    public static AudioClip Whisper => whisper != null ? whisper : whisper = NoiseLoop("whisper", 22050, 1.8f, 0.22f);
    public static AudioClip Drone => drone != null ? drone : drone = Tone("drone", 22050, 2f);
    public static AudioClip Notification => notification != null ? notification : notification = Blip();
    public static AudioClip Crack => crack != null ? crack : crack = Burst();

    static AudioClip NoiseLoop(string name, int rate, float seconds, float tone)
    {
        int count = Mathf.CeilToInt(rate * seconds);
        float[] data = new float[count];
        float hold = 0f;
        var rng = new System.Random(name.GetHashCode());
        for (int i = 0; i < count; i++)
        {
            float white = (float)(rng.NextDouble() * 2.0 - 1.0);
            hold = Mathf.Lerp(hold, white, tone);
            data[i] = hold * 0.35f;
        }
        return Clip(name, rate, data);
    }

    static AudioClip Tone(string name, int rate, float seconds)
    {
        int count = Mathf.CeilToInt(rate * seconds);
        float[] data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Sin(Mathf.PI * (i / (float)count));
            float sample = Mathf.Sin(2f * Mathf.PI * 110f * t) * 0.45f;
            sample += Mathf.Sin(2f * Mathf.PI * 164.8f * t) * 0.22f;
            data[i] = sample * env * 0.25f;
        }
        return Clip(name, rate, data);
    }

    static AudioClip Blip()
    {
        int rate = 22050;
        int count = Mathf.CeilToInt(rate * 0.16f);
        float[] data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 18f);
            data[i] = Mathf.Sin(2f * Mathf.PI * 880f * t) * env * 0.45f;
        }
        return Clip("notify", rate, data);
    }

    static AudioClip Burst()
    {
        int rate = 22050;
        int count = Mathf.CeilToInt(rate * 0.35f);
        float[] data = new float[count];
        var rng = new System.Random(7);
        for (int i = 0; i < count; i++)
        {
            float env = Mathf.Exp(-i / (float)count * 7f);
            data[i] = ((float)rng.NextDouble() * 2f - 1f) * env * 0.6f;
        }
        return Clip("crack", rate, data);
    }

    static AudioClip Clip(string name, int rate, float[] data)
    {
        var clip = AudioClip.Create(name, data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
