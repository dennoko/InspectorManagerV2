using System;
using System.Collections.Generic;
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
    /// Structured, bounded, deduplicating diagnostic logger (Section 11).
    /// Collapses consecutive identical log messages so errors never spam per frame.
    /// </summary>
    public sealed class UnityDiagnosticLogger : IDiagnosticLogger
    {
        private readonly IMonotonicClock _clock;
        private readonly int _maxEntries;
        private readonly List<DiagnosticLogEntry> _entries = new List<DiagnosticLogEntry>();
        private readonly object _lock = new object();

        public UnityDiagnosticLogger(IMonotonicClock clock, int maxEntries = 200)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _maxEntries = maxEntries > 10 ? maxEntries : 200;
        }

        public void Info(string operationId, long generation, WindowKey? windowKey, string stage, string message)
        {
            Append(operationId, generation, windowKey, stage, message, isError: false);
        }

        public void Error(string operationId, long generation, WindowKey? windowKey, string stage, string message)
        {
            Append(operationId, generation, windowKey, stage, message, isError: true);
        }

        public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries()
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        private void Append(string operationId, long generation, WindowKey? windowKey, string stage, string message, bool isError)
        {
            long now = _clock.NowMs;
            lock (_lock)
            {
                if (_entries.Count > 0)
                {
                    var last = _entries[_entries.Count - 1];
                    if (last.IsError == isError
                        && last.Generation == generation
                        && last.WindowKey == windowKey
                        && string.Equals(last.Stage, stage, StringComparison.Ordinal)
                        && string.Equals(last.Message, message, StringComparison.Ordinal))
                    {
                        _entries[_entries.Count - 1] = last.WithIncrementedRepeat(now);
                        return;
                    }
                }

                _entries.Add(new DiagnosticLogEntry(now, operationId, generation, windowKey, stage, message, isError));
                while (_entries.Count > _maxEntries)
                {
                    _entries.RemoveAt(0);
                }
            }
        }
    }
}
