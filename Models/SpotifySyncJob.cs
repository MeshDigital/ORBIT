using System;
using ReactiveUI;

namespace SLSKDONET.Models
{
    public class SpotifySyncJob : ReactiveObject
    {
        private Guid _id = Guid.NewGuid();
        public Guid Id 
        { 
            get => _id; 
            set => this.RaiseAndSetIfChanged(ref _id, value); 
        }

        private string _playlistUrlOrId = string.Empty;
        public string PlaylistUrlOrId 
        { 
            get => _playlistUrlOrId; 
            set => this.RaiseAndSetIfChanged(ref _playlistUrlOrId, value); 
        }

        private string _playlistName = string.Empty;
        public string PlaylistName 
        { 
            get => _playlistName; 
            set => this.RaiseAndSetIfChanged(ref _playlistName, value); 
        }

        private DateTime _lastSyncedAt = DateTime.MinValue;
        public DateTime LastSyncedAt 
        { 
            get => _lastSyncedAt; 
            set => this.RaiseAndSetIfChanged(ref _lastSyncedAt, value); 
        }

        private bool _isActive = true;
        public bool IsActive 
        { 
            get => _isActive; 
            set => this.RaiseAndSetIfChanged(ref _isActive, value); 
        }

        private bool _isSyncing = false;
        public bool IsSyncing 
        { 
            get => _isSyncing; 
            set => this.RaiseAndSetIfChanged(ref _isSyncing, value); 
        }
    }
}
