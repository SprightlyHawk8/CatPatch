using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;

namespace CatPatchspace.SprayFallback;

// Byte delivery only. Placement, moderation, GIF decoding and rendering remain in Sprays/.
internal sealed class SprayService
{
    private readonly NetworkController _network;
    private readonly Task<SprayCache> _cache;
    private readonly SemaphoreSlim _http = new(2);
    private readonly Dictionary<string, Task<SprayImage>> _uploads = new();
    private readonly Dictionary<string, Task<SprayImage>> _downloads = new();
    private readonly HashSet<string> _seeding = new();
    private int _scene;

    internal SprayService(NetworkController network)
    {
        _network = network;
        string directory = Path.Combine(Paths.ConfigPath, "Cat-Patch", "SprayFallbackCache");
        bool clearOnStartup = CatPatchClientSettings.ClearSprayFallbackCacheOnStartup;
        _cache = Task.Run(() => new SprayCache(directory, 64L * 1024 * 1024, 256L * 1024 * 1024, clearOnStartup));
    }

    internal async Task<SprayImage> ReadAsync(string id)
    {
        var cache = await _cache;
        return await Task.Run(() => cache.Get(id));
    }

    internal async Task<SprayImage> StoreAsync(SprayImage image)
    {
        var cache = await _cache;
        return await Task.Run(() => cache.Put(image));
    }

    internal async Task<SprayImage> VerifyRelayAsync(SprayImage image, CancellationToken token)
    {
        if (image.Id.StartsWith("custom-", StringComparison.Ordinal))
            return image; // Constructor already binds content to SHA-256.
        var authoritative = await ReadAsync(image.Id);
        token.ThrowIfCancellationRequested();
        authoritative ??= await FetchBackend(image.Id, token);
        if (authoritative.Hash != image.Hash)
            throw new IOException("Peer image does not match the backend's image ID.");
        return authoritative;
    }

    internal async Task<bool> ContainsAsync(string id, int minimumSize = 1)
    {
        var cache = await _cache;
        return await Task.Run(() => cache.Contains(id, minimumSize));
    }

    internal async void Upload(string path, Action<string, byte[], string> success, Action<string> failure)
    {
        int scene = _scene;
        int preference = CatPatchClientSettings.SprayPreferenceGeneration;
        var token = _network.SessionToken;
        try
        {
            if (!CatPatchClientSettings.ShowSprays)
                return;
            string fullPath = Path.GetFullPath(path);
            string key = await Task.Run(() => FileKey(fullPath), token);
            token.ThrowIfCancellationRequested();
            if (!_uploads.TryGetValue(key, out var task))
            {
                if (_uploads.Count >= 16)
                    throw new IOException("Too many spray uploads are pending.");
                task = UploadCore(fullPath, key, token);
                _uploads[key] = task;
            }
            SprayImage image;
            try { image = await task; }
            finally
            {
                if (_uploads.TryGetValue(key, out var current) && current == task)
                    _uploads.Remove(key);
            }
            token.ThrowIfCancellationRequested();
            if (scene != _scene || preference != CatPatchClientSettings.SprayPreferenceGeneration
                || !CatPatchClientSettings.ShowSprays)
                return;

            if (image.BackendUploaded)
            {
                // A slow lobby transfer must not delay a working backend placement.
                if (CatPatchClientSettings.SendSprayFallback)
                    SeedHost(image, token);
            }
            else
            {
                if (!CatPatchClientSettings.SendSprayFallback)
                    throw new IOException("Backend unavailable. Enable 'Send sprays over Steam fallback' in the CP menu to use the lobby backup.");
                if (!await _network.UploadAsync(image))
                    throw new IOException("Backend unavailable and the lobby host could not accept the spray backup.");
                token.ThrowIfCancellationRequested();
                if (scene != _scene || preference != CatPatchClientSettings.SprayPreferenceGeneration
                    || !CatPatchClientSettings.SendSprayFallback)
                    return;
            }

            Plugin.Logger.LogInfo($"[Sprays] stage=upload.complete id={image.Id} bytes={image.Bytes.Length} source={(image.BackendUploaded ? "backend" : "lobby")}");
            success?.Invoke(image.Id, image.Bytes, path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || scene != _scene
                || preference != CatPatchClientSettings.SprayPreferenceGeneration)
                return;
            Plugin.Logger.LogWarning("[Sprays] stage=upload.failed error=" + ex.Message);
            failure?.Invoke(ex.Message);
        }
    }

    private async Task<SprayImage> UploadCore(string path, string key, CancellationToken token)
    {
        var cache = await _cache;
        var image = await Task.Run(() => cache.GetFile(key), token);
        if (image != null && image.BackendUploaded)
            return image;

        byte[] bytes = image?.Bytes ?? await Task.Run(() => ReadFile(path, key), token);
        await _http.WaitAsync(token);
        try
        {
            image = await Task.Run(() =>
            {
                bool uploaded = GarythecatBackendClient.UploadSpray(bytes, out var response, out string error);
                string id = SprayImage.Normalize(response?.spray_id);
                uploaded = uploaded && SprayImage.IsValidId(id) && !id.StartsWith("custom-", StringComparison.Ordinal);
                if (!uploaded)
                {
                    id = "custom-" + SprayImage.ComputeHash(bytes);
                    Plugin.Logger.LogWarning("[Sprays] stage=upload.backend_failed trying_lobby id=" + id + " error=" + error);
                }
                return new SprayImage(id, bytes, uploaded, key);
            }, token);
        }
        finally { _http.Release(); }
        token.ThrowIfCancellationRequested();
        return await StoreAsync(image);
    }

    private async void SeedHost(SprayImage image, CancellationToken token)
    {
        if (!_seeding.Add(image.Id))
            return;
        try
        {
            bool accepted = await _network.UploadAsync(image);
            token.ThrowIfCancellationRequested();
            Plugin.Logger.LogInfo($"[Sprays] stage=fallback.seed id={image.Id} accepted={accepted}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Plugin.Logger.LogWarning("[Sprays] stage=fallback.seed error=" + ex.Message); }
        finally { if (!token.IsCancellationRequested) _seeding.Remove(image.Id); }
    }

    internal async void Download(string id, Action<byte[]> ready)
    {
        id = SprayImage.Normalize(id);
        int scene = _scene;
        int preference = CatPatchClientSettings.SprayPreferenceGeneration;
        var token = _network.SessionToken;
        try
        {
            if (!CatPatchClientSettings.ShowSprays || !SprayImage.IsValidId(id))
                return;
            if (!_downloads.TryGetValue(id, out var task))
            {
                if (_downloads.Count >= 32)
                    throw new IOException("Too many spray downloads are pending.");
                task = DownloadCore(id, token);
                _downloads[id] = task;
            }
            SprayImage image;
            try { image = await task; }
            finally
            {
                if (_downloads.TryGetValue(id, out var current) && current == task)
                    _downloads.Remove(id);
            }
            token.ThrowIfCancellationRequested();
            if (scene == _scene && preference == CatPatchClientSettings.SprayPreferenceGeneration
                && CatPatchClientSettings.ShowSprays)
                ready?.Invoke(image.Bytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && scene == _scene && CatPatchClientSettings.ShowSprays)
                Plugin.Logger.LogWarning($"[Sprays] stage=download.failed id={id} error={ex.Message}");
        }
    }

    private async Task<SprayImage> DownloadCore(string id, CancellationToken token)
    {
        var image = await ReadAsync(id);
        token.ThrowIfCancellationRequested();
        if (image != null)
            return image;
        if (!id.StartsWith("custom-", StringComparison.Ordinal))
        {
            try { return await FetchBackend(id, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Sprays] stage=download.backend_failed trying_lobby id={id} error={ex.Message}");
            }
        }
        image = await _network.DownloadAsync(id);
        token.ThrowIfCancellationRequested();
        if (image == null)
            throw new IOException("The backend and lobby backup could not provide this spray.");
        Plugin.Logger.LogInfo($"[Sprays] stage=download.fallback id={id} bytes={image.Bytes.Length}");
        return image;
    }

    private async Task<SprayImage> FetchBackend(string id, CancellationToken token)
    {
        await _http.WaitAsync(token);
        SprayImage image;
        try
        {
            image = await Task.Run(() =>
            {
                if (!GarythecatBackendClient.DownloadSpray(id, out byte[] bytes, out string error))
                    throw new IOException(error ?? "Spray backend unavailable.");
                return new SprayImage(id, bytes, true);
            }, token);
        }
        finally { _http.Release(); }
        token.ThrowIfCancellationRequested();
        return await StoreAsync(image);
    }

    // Host relay is independent of the host's display preference.
    internal async Task<SprayImage> ReadHostedAsync(string id, CancellationToken token)
    {
        var image = await ReadAsync(id);
        token.ThrowIfCancellationRequested();
        if (image != null)
            return image;
        // A peer Get can only read cached/in-progress images. It must never
        // turn the host into a backend request proxy for arbitrary IDs.
        if (!_network.WasPlaced(id)) return null;

        // Placement can overtake the background upload. Wait for its cache commit.
        for (int elapsed = 0; elapsed < 30000; elapsed += 100)
        {
            if (elapsed >= 3000 && !_network.HasIncoming(id))
                break;
            await Task.Delay(100, token);
            image = await ReadAsync(id);
            token.ThrowIfCancellationRequested();
            if (image != null)
                return image;
        }
        return null;
    }

    private static string FileKey(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Spray file was not found.", path);
        return SprayImage.ComputeHash(Encoding.UTF8.GetBytes(path + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length));
    }

    private static byte[] ReadFile(string path, string key)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > SprayImage.HardByteSizeLimit)
            throw new IOException("Spray file exceeds the 20 MB safety limit or is empty.");
        using var reader = new BinaryReader(stream);
        var bytes = reader.ReadBytes((int)stream.Length);
        if (bytes.Length != stream.Length || FileKey(path) != key)
            throw new IOException("The spray file changed while being read. Try again.");
        SprayImageSafety.Validate(bytes);
        return bytes;
    }

    internal void ClearScene() { _scene++; _network.ClearPlaced(); }
    internal void ResetSession()
    {
        _scene++;
        _uploads.Clear();
        _downloads.Clear();
        _seeding.Clear();
    }

    internal static void Alert(string message) => HugController.ShowLocalMessage(message);

    internal void Close()
    {
        // Disk-cache disposal needs no Unity calls; keep it off the game thread.
        _ = _cache.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.Close(false);
        }, TaskScheduler.Default);
    }
}

internal static class SprayFallbackLog
{
    internal static void Verbose(string message) => Plugin.Logger.LogDebug("[Sprays] fallback " + message);
}
