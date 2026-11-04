// Performance meter: time spent by each part of the mod (Quality > PerformanceMeter).
// GPU times come from Unity's GPU recorders on the profiling samplers the passes already use; when the game build
// or the graphics API does not provide them, only the frame time is shown (compare by toggling options).
using UnityEngine;
using UnityEngine.Rendering;

namespace NoAtmoFX
{
    static class Perf
    {
        internal static readonly ProfilingSampler Clouds = new ProfilingSampler("NOClouds");
        internal static readonly ProfilingSampler Weather = new ProfilingSampler("NOClouds weather maps");
        internal static readonly ProfilingSampler March = new ProfilingSampler("NOClouds raymarch");
        internal static readonly ProfilingSampler Refl = new ProfilingSampler("NOClouds reflection");
        internal static readonly ProfilingSampler Shadow = new ProfilingSampler("NOClouds shadow");
        internal static readonly ProfilingSampler Water = new ProfilingSampler("NOClouds water");
        internal static readonly ProfilingSampler Atmos = new ProfilingSampler("NOClouds atmosphere");
        internal static readonly ProfilingSampler Glass = new ProfilingSampler("NOClouds wet glass");

        // label (en, pt), sampler; 'Clouds' includes weather maps (when built there) and the raymarch
        static readonly (string en, string pt, ProfilingSampler s)[] Rows =
        {
            ("Clouds pass (camera)", "Passe das nuvens (câmera)", Clouds),
            ("  raymarch", "  raymarch", March),
            ("  weather maps", "  mapas de tempo", Weather),
            ("Shadows", "Sombras", Shadow),
            ("Atmosphere (rays, shafts, rain)", "Atmosfera (raios, feixes, chuva)", Atmos),
            ("Sea reflection (every ~5 s)", "Reflexo do mar (a cada ~5 s)", Refl),
            ("Water recomposite", "Recomposição da água", Water),
            ("Wet glass", "Vidro molhado", Glass),
        };
        static readonly float[] Gpu = new float[Rows.Length], Cpu = new float[Rows.Length], Peak = new float[Rows.Length];
        static bool _on;
        static float _frame, _anyGpuT;

        internal static void SetEnabled(bool on)
        {
            if (on == _on) return;
            _on = on;
            foreach (var r in Rows) r.s.enableRecording = on;
            if (on) { System.Array.Clear(Gpu, 0, Gpu.Length); System.Array.Clear(Cpu, 0, Cpu.Length); System.Array.Clear(Peak, 0, Peak.Length); _anyGpuT = Time.unscaledTime; }
        }

        // once per frame (Update): smoothed values; the reflection runs every ~5 s, so its peak is kept instead
        internal static void Tick()
        {
            if (!_on) return;
            float dt = Time.unscaledDeltaTime;
            _frame = Mathf.Lerp(_frame, dt * 1000f, 0.05f);
            for (int i = 0; i < Rows.Length; i++)
            {
                var s = Rows[i].s;
                float g = s.gpuElapsedTime, c = s.cpuElapsedTime;
                if (g > 0) _anyGpuT = Time.unscaledTime + 1e6f;
                if (s == Refl)
                {
                    if (s.gpuSampleCount > 0 || s.cpuSampleCount > 0) { Gpu[i] = g; Cpu[i] = c; }
                    continue;
                }
                Gpu[i] = Mathf.Lerp(Gpu[i], g, 0.05f);
                Cpu[i] = Mathf.Lerp(Cpu[i], c, 0.05f);
                Peak[i] = Mathf.Max(Peak[i] * 0.995f, g);
            }
        }

        internal static void Draw(System.Func<string, string, string> T)
        {
            GUILayout.Label(T($"Frame: {_frame:F1} ms ({(_frame > 0 ? 1000f / _frame : 0):F0} fps), GPU {SystemInfo.graphicsDeviceName}, {Screen.width}x{Screen.height}",
                              $"Quadro: {_frame:F1} ms ({(_frame > 0 ? 1000f / _frame : 0):F0} fps), GPU {SystemInfo.graphicsDeviceName}, {Screen.width}x{Screen.height}"));
            bool gpuOk = Time.unscaledTime < _anyGpuT + 3f || _anyGpuT > 1e5f;
            for (int i = 0; i < Rows.Length; i++)
            {
                var r = Rows[i];
                string name = T(r.en, r.pt);
                string g = gpuOk ? $"GPU {Gpu[i]:F2} ms" + (r.s != Refl ? $" ({T("peak", "pico")} {Peak[i]:F2})" : "") : "GPU n/a";
                GUILayout.Label($"{name}: {g}, CPU {Cpu[i]:F2} ms");
            }
            if (!gpuOk)
                GUILayout.Label(T("This game build/graphics API gives no GPU timers to mods. Compare the frame time with the options above on/off instead.",
                                  "Esta build do jogo/API gráfica não dá timers de GPU pros mods. Compare o tempo de quadro ligando/desligando as opções acima."));
        }
    }
}
