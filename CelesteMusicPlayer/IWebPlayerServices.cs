using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
namespace CelesteMusicPlayer;
// Boundary consumed by the native web host; implementation migration is tracked separately.
internal interface IWebPlayerServices {
    void BindNativeTaskbar(nint hwnd);
    object? BuildCrashPush(int sessionCount);
    object? BuildUncleanPush();
    Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; }
    TimeSpan EngineDurationValue { get; }
    bool ExecutePlaybackCommand(string? command);
    byte[]? ExtractCoverBytes(string audioPath);
    void FavoriteCurrentPublic();
    PlaylistItem? GetCurrentPlayingItem();
    IReadOnlyList<PlaylistItem> LibraryTracks { get; }
    nint NativeTaskbarHandle { get; }
    IPlaybackStateSource PlaybackStateSource { get; }
    void RecordPageCrash(string kind, int sessionCount);
    PlaylistItem? ResolveSourceTrack(string id);
    void SeekSourceSeconds(double seconds);
    void SetStereoSampling(string owner, bool wanted);
    void ShowMiniPlayerWindow();
    System.Collections.ObjectModel.ObservableCollection<PlaylistItem> UserPlaylist { get; }
    WebAlbumEntry[] WebAlbums(IReadOnlyList<PlaylistItem> source);
    PlaylistItem[] WebAllTracks();
    Task<object> WebAudioChainAsync();
    object WebAudioReadout();
    Task<object> WebAudioSettingsAsync(JsonElement msg);
    Task<object> WebBrowseAsync(JsonElement msg, object host);
    Task<object> WebCollectionOverviewAsync(string category, string name, CancellationToken cancellation);
    Task<object> WebDiagnosticsAsync(JsonElement msg);
    Task<object> WebDspAsync(JsonElement msg);
    string? WebFolderCoverPath(string id);
    Task<object> WebFoldersAsync(JsonElement msg, object host);
    Task<object> WebHomeAsync(JsonElement msg);
    object WebMore(JsonElement msg);
    object WebNowPlaying();
    object WebNowPlayingVisual();
    void WebPlayAll(IReadOnlyList<PlaylistItem> tracks);
    void WebPlayTrack(PlaylistItem item);
    object WebQueue(JsonElement msg);
    Task<object> WebRecoveryAsync(JsonElement msg);
    void WebSetPlaybackOrder(string value);
    void WebSetVolume(double volume);
    Task<object> WebSettingsAsync(JsonElement msg, object host);
    Task<object> WebSourcesAsync(JsonElement msg);
    Task<(PlaylistItem[] Tracks, string Directory, bool Canceled)> WebTagAddFilesAsync(bool folder, object host);
    PlaylistItem WebTagFromPath(string path);
    Task<object> WebTagsAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks);
    Task<object> WebTrackActionAsync(JsonElement msg, object host);
    void WebTransportAction(string action);
    Task<float[]> WebWaveformAsync(string id, System.Threading.CancellationToken cancellation);
}
