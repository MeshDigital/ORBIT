using System;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Events;

// NOTE: TrackMetadataUpdatedEvent moved to Models/Events.cs to avoid duplication
// This namespace intentionally left minimal

/// <summary>
/// Published when a track's status, progress, or metadata changes.
/// </summary>
public class TrackStatusChangedEvent
{
    public Guid PlaylistId { get; }
    public string TrackUniqueHash { get; }
    public TrackStatus? NewStatus { get; }
    public double? Progress { get; }
    public string? ErrorMessage { get; }

    public TrackStatusChangedEvent(Guid playlistId, string trackUniqueHash, TrackStatus? newStatus = null, double? progress = null, string? errorMessage = null)
    {
        PlaylistId = playlistId;
        TrackUniqueHash = trackUniqueHash;
        NewStatus = newStatus;
        Progress = progress;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Published when a search produces new results for a track.
/// </summary>
public class TrackSearchStartedEvent
{
    public Guid PlaylistId { get; }
    public string TrackUniqueHash { get; }
    
    public TrackSearchStartedEvent(Guid playlistId, string trackUniqueHash)
    {
        PlaylistId = playlistId;
        TrackUniqueHash = trackUniqueHash;
    }
}



public class TrackAddedEvent
{
    public PlaylistTrack TrackModel { get; }
    public TrackAddedEvent(PlaylistTrack track) => TrackModel = track;
}

public class TrackRemovedEvent
{
    public string TrackGlobalId { get; }
    public TrackRemovedEvent(string globalId) => TrackGlobalId = globalId;
}

public class TrackProgressChangedEvent
{
    public string TrackGlobalId { get; }
    public double Progress { get; }
    public TrackProgressChangedEvent(string globalId, double progress)
    {
        TrackGlobalId = globalId;
        Progress = progress;
    }
}

public class TrackStateChangedEvent
{
    public string TrackGlobalId { get; }
    public PlaylistTrackState NewState { get; }
    public string? ErrorMessage { get; }
    
    public TrackStateChangedEvent(string globalId, PlaylistTrackState newState, string? errorMessage = null)
    {
        TrackGlobalId = globalId;
        NewState = newState;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Published for significant library changes (e.g. project reload required)
/// </summary>
public class LibraryUpdatedEvent
{
    public string Reason { get; }

    public LibraryUpdatedEvent(string reason)
    {
        Reason = reason;
    }
}
