using System.Text;

namespace SLSKDONET.Services.Musical
{
    /// <summary>
    /// Fluent builder for DJ-mentor-style transition reasoning.
    /// Creates structured output with sections, bullets, and a final verdict.
    /// </summary>
    public sealed class MentorReasoningBuilder
    {
        private readonly StringBuilder _sb = new();

        /// <summary>
        /// Adds a section header (e.g., [BREAKDOWN ANALYSIS]).
        /// </summary>
        public MentorReasoningBuilder AddSection(string title)
        {
            if (_sb.Length > 0)
                _sb.AppendLine();

            _sb.AppendLine($"▓ {title.ToUpperInvariant()}");
            return this;
        }

        /// <summary>
        /// Adds a bullet point within the current section.
        /// </summary>
        public MentorReasoningBuilder AddBullet(string text)
        {
            _sb.AppendLine($"  • {text}");
            return this;
        }

        /// <summary>
        /// Adds an indented sub-detail under the previous bullet.
        /// </summary>
        public MentorReasoningBuilder AddDetail(string text)
        {
            _sb.AppendLine($"    → {text}");
            return this;
        }

        /// <summary>
        /// Adds a warning bullet with emphasis.
        /// </summary>
        public MentorReasoningBuilder AddWarning(string text)
        {
            _sb.AppendLine($"  ⚠ {text}");
            return this;
        }

        /// <summary>
        /// Adds a success bullet with emphasis.
        /// </summary>
        public MentorReasoningBuilder AddSuccess(string text)
        {
            _sb.AppendLine($"  ✓ {text}");
            return this;
        }

        /// <summary>
        /// Adds the final verdict section with visual separators.
        /// </summary>
        public MentorReasoningBuilder AddVerdict(string text)
        {
            _sb.AppendLine();
            _sb.AppendLine("═══════════════════════════════════════════");
            _sb.AppendLine("▓ FINAL VERDICT");
            _sb.AppendLine($"  {text}");
            _sb.AppendLine("═══════════════════════════════════════════");
            return this;
        }

        /// <summary>
        /// Adds an optimal moment callout.
        /// </summary>
        public MentorReasoningBuilder AddOptimalMoment(double time, string reason)
        {
            _sb.AppendLine();
            _sb.AppendLine($"  🎯 OPTIMAL MOMENT: {time:F1}s");
            _sb.AppendLine($"     Reason: {reason}");
            return this;
        }

        public override string ToString() => _sb.ToString().TrimEnd();
    }
}
