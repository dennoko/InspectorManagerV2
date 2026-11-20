using System;
using System.Diagnostics;
using DennokoWorks.InspectorManagerV2.Application;
using DennokoWorks.InspectorManagerV2.Domain;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    public sealed class UnityMonotonicClock : IMonotonicClock
    {
        private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();

        public long NowMs => s_stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// Minimal deduplicating error logger (writes only unexpected errors to Unity console without spamming per frame).
    /// </summary>
    public sealed class UnityDiagnosticLogger : IDiagnosticLogger
    {
        private string _lastErrorSignature = string.Empty;

        public void Info(string operationId, long generation, WindowKey? windowKey, string stage, string message)
        {
            // Quiet in normal operation.
        }

        public void Error(string operationId, long generation, WindowKey? windowKey, string stage, string message)
        {
            string sig = generation + "|" + (windowKey.HasValue ? windowKey.Value.Value : "-") + "|" + stage + "|" + message;
            if (string.Equals(_lastErrorSignature, sig, StringComparison.Ordinal))
            {
                return;
            }
            _lastErrorSignature = sig;
            UnityEngine.Debug.LogWarning("[InspectorManagerV2] [" + stage + "] " + message);
        }
    }
}
