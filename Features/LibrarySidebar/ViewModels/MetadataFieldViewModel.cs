using System;
using ReactiveUI;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class MetadataFieldViewModel : ReactiveObject
{
    private string _fieldName;
    private string? _fileValue;
    private string? _databaseValue;
    private string? _spotifyValue;
    private string? _editValue;
    private bool _isModified;
    private bool _isReadOnly;

    public string FieldName 
    { 
        get => _fieldName; 
        set => this.RaiseAndSetIfChanged(ref _fieldName, value); 
    }

    public string? FileValue 
    { 
        get => _fileValue; 
        set => this.RaiseAndSetIfChanged(ref _fileValue, value); 
    }

    public string? DatabaseValue 
    { 
        get => _databaseValue; 
        set 
        {
            this.RaiseAndSetIfChanged(ref _databaseValue, value); 
            IsModified = EditValue != _databaseValue;
        }
    }

    public string? SpotifyValue 
    { 
        get => _spotifyValue; 
        set => this.RaiseAndSetIfChanged(ref _spotifyValue, value); 
    }

    public string? EditValue 
    { 
        get => _editValue; 
        set 
        {
            this.RaiseAndSetIfChanged(ref _editValue, value);
            IsModified = _editValue != DatabaseValue;
        }
    }

    public bool IsModified 
    { 
        get => _isModified; 
        private set => this.RaiseAndSetIfChanged(ref _isModified, value); 
    }

    public bool IsReadOnly 
    { 
        get => _isReadOnly; 
        set => this.RaiseAndSetIfChanged(ref _isReadOnly, value); 
    }

    public bool HasConflict => !IsReadOnly && (FileValue != DatabaseValue || (SpotifyValue != null && DatabaseValue != SpotifyValue));

    public MetadataFieldViewModel(string fieldName, string? dbValue = null)
    {
        _fieldName = fieldName;
        _databaseValue = dbValue;
        _editValue = dbValue;
    }

    public void ApplyFile() => EditValue = FileValue;
    public void ApplySpotify() => EditValue = SpotifyValue;
    public void Revert() => EditValue = DatabaseValue;
}
