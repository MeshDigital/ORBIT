using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Surgical
{
    /// <summary>
    /// Manages a collection of PhraseSegments, enforcing surgical constraints:
    /// 1. No overlapping segments.
    /// 2. Chronological order must be maintained.
    /// 3. Durations and bar/beat metadata are updated automatically.
    /// </summary>
    public class SegmentManager
    {
        private readonly ObservableCollection<PhraseSegment> _segments;
        private readonly float _bpm;

        public SegmentManager(ObservableCollection<PhraseSegment> segments, float bpm)
        {
            _segments = segments;
            _bpm = bpm;
        }

        public void MoveBoundary(PhraseSegment segment, bool isStart, float newTime)
        {
            int index = _segments.IndexOf(segment);
            if (index < 0) return;

            if (isStart)
            {
                // Moving start
                // Boundary: Cannot go past previous segment's start + epsilon, 
                // but in this model, previous segment's end IS its last start + duration.
                float minStart = (index > 0) ? _segments[index - 1].Start + 0.1f : 0;
                float maxStart = segment.Start + segment.Duration - 0.1f;
                
                float clampedStart = Math.Clamp(newTime, minStart, maxStart);
                float delta = clampedStart - segment.Start;
                
                segment.Start = clampedStart;
                segment.Duration -= delta;
                
                // Neighbor Adjustment: Update previous segment's duration
                if (index > 0)
                {
                    _segments[index - 1].Duration = segment.Start - _segments[index - 1].Start;
                    UpdateSegmentMetadata(_segments[index - 1]);
                }
                
                UpdateSegmentMetadata(segment);
            }
            else
            {
                // Moving end
                float minEnd = segment.Start + 0.1f;
                // Cannot go past next segment's end - epsilon
                float maxEnd = (index < _segments.Count - 1) ? _segments[index + 1].Start + _segments[index + 1].Duration - 0.1f : float.MaxValue;
                
                float clampedEnd = Math.Clamp(newTime, minEnd, maxEnd);
                float oldEnd = segment.Start + segment.Duration;
                float delta = clampedEnd - oldEnd;
                
                segment.Duration = clampedEnd - segment.Start;
                
                // Neighbor Adjustment: Update next segment's start and duration
                if (index < _segments.Count - 1)
                {
                    _segments[index + 1].Start = clampedEnd;
                    _segments[index + 1].Duration -= delta;
                    UpdateSegmentMetadata(_segments[index + 1]);
                }
                
                UpdateSegmentMetadata(segment);
            }
        }

        public void AddSegment(float startTime, string label, string color)
        {
            // Find insertion point
            int insertIndex = 0;
            while (insertIndex < _segments.Count && _segments[insertIndex].Start < startTime)
            {
                insertIndex++;
            }

            // Check for overlap with existing segments
            if (insertIndex > 0 && _segments[insertIndex - 1].Start + _segments[insertIndex - 1].Duration > startTime)
            {
                return; // Overlaps previous
            }

            var segment = new PhraseSegment
            {
                Label = label,
                Start = startTime,
                Duration = 5.0f, // Default 5s
                Color = color,
                Confidence = 1.0f
            };

            // Clamp duration if it hits next segment
            if (insertIndex < _segments.Count)
            {
                 float gap = _segments[insertIndex].Start - startTime;
                 segment.Duration = Math.Min(segment.Duration, gap);
            }

            _segments.Insert(insertIndex, segment);
            UpdateSegmentMetadata(segment);
        }

        public void RemoveSegment(PhraseSegment segment)
        {
            _segments.Remove(segment);
        }

        private void UpdateSegmentMetadata(PhraseSegment segment)
        {
            if (_bpm > 0)
            {
                float beats = (segment.Duration * _bpm) / 60f;
                segment.Beats = (int)Math.Round(beats);
                segment.Bars = (int)Math.Round(beats / 4f);
            }
        }
        
        public IEnumerable<PhraseSegment> GetSortedSegments() => _segments.OrderBy(s => s.Start);
    }
}
