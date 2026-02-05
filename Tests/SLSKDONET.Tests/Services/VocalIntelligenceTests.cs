using System;
using System.Collections.Generic;
using Xunit;
using SLSKDONET.Services.Musical;
using SLSKDONET.Services;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace SLSKDONET.Tests.Services
{
    public class VocalIntelligenceTests
    {
        private readonly VocalIntelligenceService _vocalService;

        public VocalIntelligenceTests()
        {
            _vocalService = new VocalIntelligenceService();
        }

        [Fact]
        public void AnalyzeVocalDensity_IdentifiesInstrumental()
        {
            float[] data = new float[] { 0, 0, 0, 0, 0 };
            var result = _vocalService.AnalyzeVocalDensity(data, 100);
            Assert.Equal(VocalType.Instrumental, result.Type);
            Assert.Equal(0, result.Intensity);
        }

        [Fact]
        public void CalculateOverlapHazard_DetectsHighConflict()
        {
            // Both tracks have high density in the overlap zone
            float[] curveA = new float[100];
            float[] curveB = new float[100];
            for (int i = 80; i < 100; i++) curveA[i] = 1.0f;
            for (int i = 0; i < 20; i++) curveB[i] = 1.0f;

            double hazard = _vocalService.CalculateOverlapHazard(curveA, curveB, 80, 100, 100);
            Assert.True(hazard > 0.8, $"Hazard should be high, was {hazard}");
        }
    }

    public class TransitionAdvisorTests
    {
        private readonly TransitionAdvisorService _advisor;
        private readonly Mock<VocalIntelligenceService> _vocalMock;
        private readonly Mock<HarmonicMatchService> _harmonicMock;

        public TransitionAdvisorTests()
        {
            _vocalMock = new Mock<VocalIntelligenceService>();
            _harmonicMock = new Mock<HarmonicMatchService>(new Mock<ILogger<HarmonicMatchService>>().Object, null);
            _advisor = new TransitionAdvisorService(
                new Mock<ILogger<TransitionAdvisorService>>().Object,
                _harmonicMock.Object,
                _vocalMock.Object);
        }

        [Fact]
        public void AdviseTransition_SuggestsVocalToInstrumental()
        {
            var trackA = new LibraryEntryEntity { VocalType = VocalType.FullLyrics, VocalEndSeconds = 200 };
            var trackB = new LibraryEntryEntity { VocalType = VocalType.Instrumental };

            var suggestion = _advisor.AdviseTransition(trackA, trackB);
            
            Assert.Equal(TransitionArchetype.VocalToInstrumental, suggestion.Archetype);
            Assert.Contains("Instrumental", suggestion.Reasoning);
            Assert.Contains("Track A is 'FullLyrics'", suggestion.Reasoning);
        }
    }
}
