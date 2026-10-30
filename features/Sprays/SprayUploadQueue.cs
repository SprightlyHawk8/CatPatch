using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CatPatchspace
{
    internal sealed class SprayUploadQueue
    {
        private sealed class UploadCacheEntry
        {
            public string SprayId;
            public byte[] ImageBytes;
            public string SourceFilePath;
        }

        private sealed class PendingUpload
        {
            public string CacheKey;
            public string SourceFilePath;
        }

        private sealed class CompletedUpload
        {
            public string CacheKey;
            public string SprayId;
            public byte[] ImageBytes;
            public string SourceFilePath;
            public string Error;
        }

        private readonly object _lock = new object();

        private readonly Queue<PendingUpload> _pendingUploads = new Queue<PendingUpload>();
        private readonly Queue<CompletedUpload> _completedUploads = new Queue<CompletedUpload>();

        private readonly Dictionary<string, UploadCacheEntry> _cacheByKey =
            new Dictionary<string, UploadCacheEntry>();

        private readonly Dictionary<string, List<Action<string, byte[], string>>> _callbacksByKey =
            new Dictionary<string, List<Action<string, byte[], string>>>();

        private readonly Dictionary<string, List<Action<string>>> _failureCallbacksByKey =
            new Dictionary<string, List<Action<string>>>();

        private readonly HashSet<string> _uploadsInProgress = new HashSet<string>();

        private bool _workerRunning;

        public void EnqueueFile(
            string sourceFilePath,
            Action<string, byte[], string> onUploaded,
            Action<string> onFailed = null)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                Plugin.Logger.LogWarning("[Sprays] stage=upload.enqueue failed: source file missing");
                onFailed?.Invoke("source_file_missing");
                return;
            }

            string cacheKey = BuildCacheKey(sourceFilePath);

            lock (_lock)
            {
                UploadCacheEntry cached;
                if (_cacheByKey.TryGetValue(cacheKey, out cached))
                {
                    Plugin.Logger.LogInfo("[Sprays] stage=upload.cache hit id=" + cached.SprayId);
                    onUploaded?.Invoke(cached.SprayId, cached.ImageBytes, cached.SourceFilePath);
                    return;
                }

                List<Action<string, byte[], string>> callbacks;
                if (!_callbacksByKey.TryGetValue(cacheKey, out callbacks))
                {
                    callbacks = new List<Action<string, byte[], string>>();
                    _callbacksByKey[cacheKey] = callbacks;
                }

                callbacks.Add(onUploaded);

                if (onFailed != null)
                {
                    List<Action<string>> failureCallbacks;
                    if (!_failureCallbacksByKey.TryGetValue(cacheKey, out failureCallbacks))
                    {
                        failureCallbacks = new List<Action<string>>();
                        _failureCallbacksByKey[cacheKey] = failureCallbacks;
                    }

                    failureCallbacks.Add(onFailed);
                }

                if (_uploadsInProgress.Contains(cacheKey))
                    return;

                _uploadsInProgress.Add(cacheKey);

                _pendingUploads.Enqueue(new PendingUpload
                {
                    CacheKey = cacheKey,
                    SourceFilePath = sourceFilePath
                });
                Plugin.Logger.LogInfo("[Sprays] stage=upload.enqueue file=" + Path.GetFileName(sourceFilePath));
            }
        }

        public void Tick()
        {
            ProcessCompletedUploads();
            StartNextUploadIfNeeded();
        }

        private void ProcessCompletedUploads()
        {
            while (true)
            {
                CompletedUpload completed;

                lock (_lock)
                {
                    if (_completedUploads.Count == 0)
                        return;

                    completed = _completedUploads.Dequeue();
                    _uploadsInProgress.Remove(completed.CacheKey);

                    if (!string.IsNullOrWhiteSpace(completed.SprayId) &&
                        completed.ImageBytes != null &&
                        completed.ImageBytes.Length > 0)
                    {
                        _cacheByKey[completed.CacheKey] = new UploadCacheEntry
                        {
                            SprayId = completed.SprayId,
                            ImageBytes = completed.ImageBytes,
                            SourceFilePath = completed.SourceFilePath
                        };
                    }
                }

                List<Action<string, byte[], string>> callbacks = null;
                List<Action<string>> failureCallbacks = null;

                lock (_lock)
                {
                    if (_callbacksByKey.TryGetValue(completed.CacheKey, out callbacks))
                        _callbacksByKey.Remove(completed.CacheKey);

                    if (_failureCallbacksByKey.TryGetValue(completed.CacheKey, out failureCallbacks))
                        _failureCallbacksByKey.Remove(completed.CacheKey);
                }

                if (string.IsNullOrWhiteSpace(completed.SprayId))
                {
                    Plugin.Logger.LogWarning("[Sprays] stage=upload.failed file=" + Path.GetFileName(completed.SourceFilePath) + " error=" + completed.Error);
                    for (int i = 0; failureCallbacks != null && i < failureCallbacks.Count; i++)
                        failureCallbacks[i]?.Invoke(completed.Error);

                    continue;
                }

                Plugin.Logger.LogInfo("[Sprays] stage=upload.complete file=" + Path.GetFileName(completed.SourceFilePath)
                    + " id=" + completed.SprayId + " bytes=" + completed.ImageBytes.Length);
                for (int i = 0; callbacks != null && i < callbacks.Count; i++)
                {
                    callbacks[i]?.Invoke(
                        completed.SprayId,
                        completed.ImageBytes,
                        completed.SourceFilePath);
                }
            }
        }

        private void StartNextUploadIfNeeded()
        {
            PendingUpload job;

            lock (_lock)
            {
                if (_workerRunning || _pendingUploads.Count == 0)
                    return;

                _workerRunning = true;
                job = _pendingUploads.Dequeue();
            }

            ThreadPool.QueueUserWorkItem(_ => UploadWorker(job));
        }

        private void UploadWorker(PendingUpload job)
        {
            CompletedUpload completed = new CompletedUpload
            {
                CacheKey = job.CacheKey,
                SourceFilePath = job.SourceFilePath
            };

            string stage = "file.read";
            try
            {
                Plugin.Logger.LogInfo("[Sprays] stage=file.read file=" + Path.GetFileName(job.SourceFilePath));
                byte[] sourceBytes = File.ReadAllBytes(job.SourceFilePath);

                completed.ImageBytes = sourceBytes;

                GarythecatSprayUploadResponse response;
                string error;

                stage = "upload.request";
                Plugin.Logger.LogInfo("[Sprays] stage=upload.request file=" + Path.GetFileName(job.SourceFilePath)
                    + " bytes=" + sourceBytes.Length + " gif=" + GifAnimationDecoder.IsGifBytes(sourceBytes));
                if (GarythecatBackendClient.UploadSpray(sourceBytes, out response, out error))
                    completed.SprayId = response.spray_id;
                else
                    completed.Error = error;
            }
            catch (Exception ex)
            {
                completed.Error = stage + ": " + ex.GetType().Name + ": " + ex.Message;
            }

            lock (_lock)
            {
                _completedUploads.Enqueue(completed);
                _workerRunning = false;
            }
        }

        private static string BuildCacheKey(string sourceFilePath)
        {
            try
            {
                return sourceFilePath + "|" + File.GetLastWriteTimeUtc(sourceFilePath).Ticks;
            }
            catch
            {
                return sourceFilePath;
            }
        }
    }
}
