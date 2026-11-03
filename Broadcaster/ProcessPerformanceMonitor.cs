using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Broadcaster
{
    internal sealed class ProcessPerformanceSnapshot
    {
        // Pourcentage CPU de CE processus rapporté à tous les processeurs logiques.
        public double CpuPercent { get; set; } = -1;
        // Maximum des moteurs GPU de CE processus. Pas le taux de toute la carte.
        public double GpuEnginePercent { get; set; } = -1;
    }

    /// <summary>
    /// Sonde désactivée tant que l'overlay est masqué. Les compteurs GPU Engine
    /// n'existent pas sur tous les systèmes ; aucune erreur ne doit arrêter la capture.
    /// Toutes les interrogations de Windows s'exécutent hors du thread WinForms.
    /// </summary>
    internal sealed class ProcessPerformanceMonitor : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private volatile ProcessPerformanceSnapshot _latest = new ProcessPerformanceSnapshot();
        private Task _task;
        public ProcessPerformanceSnapshot Latest => _latest;

        public void Start()
        {
            if (_task == null)
                _task = Task.Run(() => SampleLoop(_cancellation.Token));
        }

        private void SampleLoop(CancellationToken cancel)
        {
            var counters = new List<PerformanceCounter>();
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    TimeSpan previousCpu = process.TotalProcessorTime;
                    long previousTicks = Stopwatch.GetTimestamp();
                    long lastDiscoveryTicks = 0;
                    int pid = process.Id;

                    while (!cancel.WaitHandle.WaitOne(1000))
                    {
                        var sample = new ProcessPerformanceSnapshot();
                        try
                        {
                            process.Refresh();
                            long now = Stopwatch.GetTimestamp();
                            TimeSpan cpu = process.TotalProcessorTime;
                            double seconds = (now - previousTicks) / (double)Stopwatch.Frequency;
                            if (seconds > 0)
                                sample.CpuPercent = Math.Max(0, Math.Min(100,
                                    (cpu - previousCpu).TotalSeconds / seconds /
                                    Math.Max(1, Environment.ProcessorCount) * 100));
                            previousCpu = cpu;
                            previousTicks = now;
                        }
                        catch { sample.CpuPercent = -1; }

                        // GPU Engine / Utilization Percentage : exposé par WDDM (Windows 10+).
                        // Relire la liste périodiquement : les instances apparaissent après
                        // l'ouverture de la fenêtre D3D et peuvent disparaître au reload.
                        long timestamp = Stopwatch.GetTimestamp();
                        if (lastDiscoveryTicks == 0 ||
                            (timestamp - lastDiscoveryTicks) / (double)Stopwatch.Frequency >= 15)
                        {
                            lastDiscoveryTicks = timestamp;
                            foreach (var counter in counters) { try { counter.Dispose(); } catch { } }
                            counters.Clear();
                            try
                            {
                                if (PerformanceCounterCategory.Exists("GPU Engine"))
                                {
                                    var category = new PerformanceCounterCategory("GPU Engine");
                                    string prefix = "pid_" + pid + "_";
                                    foreach (string instance in category.GetInstanceNames()
                                        .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                        .Take(48))
                                    {
                                        try
                                        {
                                            var counter = new PerformanceCounter("GPU Engine",
                                                "Utilization Percentage", instance, true);
                                            counter.NextValue(); // amorce le compteur par intervalle
                                            counters.Add(counter);
                                        }
                                        catch { /* compteur absent, droits ou pilote */ }
                                    }
                                }
                            }
                            catch { /* certains pilotes ne publient aucun moteur GPU */ }
                        }

                        double highestEngine = -1;
                        foreach (var counter in counters)
                        {
                            try
                            {
                                double value = counter.NextValue();
                                if (!double.IsNaN(value) && !double.IsInfinity(value))
                                    highestEngine = Math.Max(highestEngine, Math.Max(0, value));
                            }
                            catch { /* instance momentanément disparue */ }
                        }
                        // Le pourcentage d'un moteur est normalement borné à 100%.
                        sample.GpuEnginePercent = highestEngine < 0 ? -1 : Math.Min(100, highestEngine);
                        _latest = sample;
                    }
                }
            }
            catch { /* diagnostic optionnel, jamais une erreur de capture */ }
            finally
            {
                foreach (var counter in counters) { try { counter.Dispose(); } catch { } }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            // Task.Run finira dès le réveil du thread; ne jamais bloquer l'UI sur
            // l'énumération Windows des moteurs GPU au moment de fermer la fenêtre.
        }
    }
}
