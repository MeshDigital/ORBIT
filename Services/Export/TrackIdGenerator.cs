using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Generates collision-safe TrackIDs for Rekordbox export.
    /// Rekordbox requires integer TrackIDs that are unique per track.
    /// </summary>
    public class TrackIdGenerator
    {
        private readonly Dictionary<string, int> _hashToId = new();
        private readonly HashSet<int> _usedIds = new();
        private int _nextSequentialId = 1;

        /// <summary>
        /// Generates a deterministic integer TrackID from a TrackUniqueHash.
        /// Handles collisions by falling back to sequential IDs.
        /// </summary>
        public int GenerateTrackId(string trackUniqueHash)
        {
            // Check if we've already generated an ID for this hash
            if (_hashToId.TryGetValue(trackUniqueHash, out int existingId))
            {
                return existingId;
            }

            // Generate deterministic hash-based ID
            int hashedId = GetDeterministicHash(trackUniqueHash);

            // Check for collision
            if (_usedIds.Contains(hashedId))
            {
                // Collision detected - use sequential fallback
                hashedId = GetNextSequentialId();
            }

            // Register the ID
            _hashToId[trackUniqueHash] = hashedId;
            _usedIds.Add(hashedId);

            return hashedId;
        }

        /// <summary>
        /// Generates a deterministic 32-bit hash from a string.
        /// Uses SHA256 and takes the first 4 bytes as an integer.
        /// </summary>
        private int GetDeterministicHash(string input)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            
            // Take first 4 bytes and convert to int
            int hash = BitConverter.ToInt32(hashBytes, 0);
            
            // Ensure positive (Rekordbox expects positive integers)
            return Math.Abs(hash);
        }

        /// <summary>
        /// Gets the next available sequential ID (collision fallback).
        /// </summary>
        private int GetNextSequentialId()
        {
            while (_usedIds.Contains(_nextSequentialId))
            {
                _nextSequentialId++;
            }
            return _nextSequentialId++;
        }

        /// <summary>
        /// Resets the generator (useful for new export sessions).
        /// </summary>
        public void Reset()
        {
            _hashToId.Clear();
            _usedIds.Clear();
            _nextSequentialId = 1;
        }
    }
}
