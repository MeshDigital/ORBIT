using SLSKDONET.ViewModels;

namespace SLSKDONET.Models;

/// <summary>
/// Typed event records for the EventBus system.
/// These replace anonymous tuples and custom event handlers.
/// </summary>

// Download Manager Events
public record TrackUpdatedEvent(PlaylistTrackViewModel Track);
public record ProjectAddedEvent(Guid ProjectId);
public record ProjectUpdatedEvent(Guid ProjectId);
public record ProjectDeletedEvent(Guid ProjectId);

// Soulseek Adapter Events
public record SoulseekStateChangedEvent(string State, bool IsConnected);
public record SoulseekConnectionStatusEvent(string Status, string Username);
public record TransferProgressEvent(string Filename, string Username, long BytesTransferred, long TotalBytes);
public record TransferFinishedEvent(string Filename, string Username);
public record TransferCancelledEvent(string Filename, string Username);
public record TransferFailedEvent(string Filename, string Username, string Error);

// Library Service Events
public record LibraryEntryAddedEvent(LibraryEntry Entry);
public record LibraryEntryUpdatedEvent(LibraryEntry Entry);
public record LibraryEntryDeletedEvent(string UniqueHash);

// Player Events
public record TrackPlaybackStartedEvent(string FilePath, string Artist, string Title);
public record TrackPlaybackPausedEvent();
public record TrackPlaybackResumedEvent();
public record TrackPlaybackStoppedEvent();
public record PlaybackProgressEvent(TimeSpan Position, TimeSpan Duration);

// Navigation & Global UI Events
public record NavigationEvent(PageType PageType);
public record PlayTrackRequestEvent(PlaylistTrackViewModel Track);
public record PlayAlbumRequestEvent(System.Collections.Generic.IEnumerable<PlaylistTrack> Tracks);
public record DownloadAlbumRequestEvent(object Album); // object to handle AlbumNode or PlaylistJob

// Explicit Track Events (missing in record list but used in code)
public record TrackAddedEvent(PlaylistTrack TrackModel);
public record TrackRemovedEvent(string TrackGlobalId);
public record TrackMovedEvent(string TrackGlobalId, Guid OldProjectId, Guid NewProjectId);
public record TrackStateChangedEvent(string TrackGlobalId, Guid ProjectId, PlaylistTrackState State, string? Error);
// Phase 2.5: Enhanced with byte-level progress tracking
public record TrackProgressChangedEvent(string TrackGlobalId, double Progress, long BytesReceived, long TotalBytes);
public record TrackMetadataUpdatedEvent(string TrackGlobalId);

// Phase 8: Automation & Upgrade Events
public record AutoDownloadTrackEvent(string TrackGlobalId, Track BestMatch);
public record AutoDownloadUpgradeEvent(string TrackGlobalId, Track BestMatch);
public record UpgradeAvailableEvent(string TrackGlobalId, Track BestMatch);
