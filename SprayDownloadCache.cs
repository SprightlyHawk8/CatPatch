using System;
using System.Collections.Generic;
using System.Threading;

namespace CatPatchspace
{
    internal sealed class SprayDownloadCache
    {
        private sealed class CompletedDownload
        {
            public string SprayId;
            public byte[] ImageBytes;
            public string Error;
        }

        private readonly object _lock = new object();

        private readonly Dictionary<string, byte[]> _cachedBytes =
            new Dictionary<string, byte[]>();

        private readonly Dictionary<string, List<Action<byte[]>>> _callbacksBySprayId =
            new Dictionary<string, List<Action<byte[]>>>();

        private readonly HashSet<string> _downloadsInProgress =
            new HashSet<string>();

        private readonly Queue<CompletedDownload> _completedDownloads =
            new Queue<CompletedDownload>();

        public void GetOrRequest(string sprayId, Action<byte[]> onReady)
        {
            if (string.IsNullOrWhiteSpace(sprayId) || onReady == null)
                return;

            sprayId = sprayId.Trim().ToLowerInvariant();

            byte[] cachedBytes;
            lock (_lock)
            {
                if (_cachedBytes.TryGetValue(sprayId, out cachedBytes))
                {
                    onReady(cachedBytes);
                    return;
                }

                List<Action<byte[]>> callbacks;
                if (!_callbacksBySprayId.TryGetValue(sprayId, out callbacks))
                {
                    callbacks = new List<Action<byte[]>>();
                    _callbacksBySprayId[sprayId] = callbacks;
                }

                callbacks.Add(onReady);

                if (_downloadsInProgress.Contains(sprayId))
                    return;

                _downloadsInProgress.Add(sprayId);
            }

            ThreadPool.QueueUserWorkItem(_ => DownloadWorker(sprayId));
        }

        public void Tick()
        {
            while (true)
            {
                CompletedDownload completed;

                lock (_lock)
                {
                    if (_completedDownloads.Count == 0)
                        return;

                    completed = _completedDownloads.Dequeue();

                    _downloadsInProgress.Remove(completed.SprayId);

                    if (completed.ImageBytes != null && completed.ImageBytes.Length > 0)
                        _cachedBytes[completed.SprayId] = completed.ImageBytes;
                }

                List<Action<byte[]>> callbacks = null;

                lock (_lock)
                {
                    if (_callbacksBySprayId.TryGetValue(completed.SprayId, out callbacks))
                        _callbacksBySprayId.Remove(completed.SprayId);
                }

                if (completed.ImageBytes == null || completed.ImageBytes.Length == 0)
                {
                    Plugin.Logger.LogWarning(
                        "Failed to download spray " + completed.SprayId + ": " + completed.Error);

                    continue;
                }

                for (int i = 0; callbacks != null && i < callbacks.Count; i++)
                    callbacks[i]?.Invoke(completed.ImageBytes);
            }
        }

        private void DownloadWorker(string sprayId)
        {
            CompletedDownload completed = new CompletedDownload
            {
                SprayId = sprayId
            };

            try
            {
                byte[] imageBytes;
                string error;

                if (GarythecatBackendClient.DownloadSpray(sprayId, out imageBytes, out error))
                    completed.ImageBytes = imageBytes;
                else
                    completed.Error = error;
            }
            catch (Exception ex)
            {
                completed.Error = ex.Message;
            }

            lock (_lock)
            {
                _completedDownloads.Enqueue(completed);
            }
        }
    }
}